using MacroDeck.Ui.Components;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Runtime;

namespace AgentLimitRings;

/// <summary>
/// Rings on the left, one legend block per window on the right.
/// Outer to inner is 5-hour, week, then month. A window the agent does not publish is not drawn at all.
/// </summary>
static class RingsView
{
    const string Ink = "#FFFFFF";
    const string Label = "#E4E4E7";
    const string Muted = "#A1A1AA";
    const string Card = "#1C1C1E";
    const double Thickness = 0.105;
    const double Step = 0.735;

    public static UiElement Build(string title, string color, string accent, RingValues values)
    {
        var windows = new[]
        {
            new Window("session", "Session", values.Session, values.SessionReset),
            new Window("week", "Weekly", values.Week, values.WeekReset),
            new Window("month", "Monthly", values.Month, values.MonthReset),
        };
        var palette = new[] { color, accent, Shade(accent, 0.45) };

        string ColorOf(Window window)
        {
            var index = Present(windows).IndexOf(window);
            return index < 0 ? Card : palette[Math.Min(index, palette.Length - 1)];
        }

        var rings = new List<UiElement>();
        var legend = new List<UiElement>();
        foreach (var window in windows)
        {
            rings.Add(new UiWhen
            {
                Key = window.Key + "RingWhen",
                Condition = () => window.IsPresent,
                Content = () => Ring(window, windows, () => ColorOf(window)),
            });
            legend.Add(new UiWhen
            {
                Key = window.Key + "BlockWhen",
                Condition = () => window.IsPresent,
                Content = () => Block(window, values, () => ColorOf(window)),
            });
        }

        rings.Add(new UiStack
        {
            Key = "center",
            Direction = "vertical",
            Justify = UiComponentJustify.Center,
            Align = "center",
            Fill = true,
            Children =
            [
                new UiTextRun
                {
                    Key = "title",
                    Text = title,
                    Size = UiLength.OfParent(0.11, 0.08),
                    Weight = "semibold",
                    Color = Muted,
                    Align = "center",
                    MaxLines = 1,
                },
            ],
        });

        return new UiModifier
        {
            Key = "card",
            Background = UiGradient.Linear(110,
            [
                new UiGradientStop { Offset = 0, Color = Card },
                new UiGradientStop { Offset = 0.45, Color = Card },
                new UiGradientStop { Offset = 1, Color = Mix(Card, color, 0.22) },
            ]),
            Child = new UiStack
            {
                Key = "body",
                Direction = "horizontal",
                Justify = UiComponentJustify.Start,
                Align = "center",
                Fill = true,
                Gap = UiLength.OfParent(0.06, 0.04),
                Padding = UiLength.OfParent(0.08, 0.05),
                Children =
                [
                    new UiLayer
                    {
                        Key = "rings",
                        MainSize = UiLength.OfParent(0.86, 1.2),
                        Children = rings,
                    },
                    new UiStack
                    {
                        Key = "legend",
                        Direction = "vertical",
                        Justify = UiComponentJustify.Center,
                        Align = "start",
                        Fill = true,
                        Gap = UiLength.OfParent(0.07, 0.04),
                        Children = legend,
                    },
                ],
            },
        };
    }

    static UiTransform Ring(Window window, Window[] windows, Func<string> color)
    {
        return new UiTransform
        {
            Key = window.Key + "Zoom",
            Zoom = UiValue.From(() => ZoomOf(window, windows)),
            OriginX = 0.5,
            OriginY = 0.5,
            Fill = true,
            Fallback = new UiTextRun
            {
                Key = window.Key + "Fallback",
                Text = UiText.From(() => window.Name + " " + PercentText(window.Percent.Value ?? 0)),
                Color = UiValue.From(color),
            },
            Children =
            [
                new UiGauge
                {
                    Key = window.Key,
                    Level = UiValue.From(() => Math.Clamp(window.Percent.Value ?? 0, 0, 100) / 100.0),
                    StartAngle = 0,
                    EndAngle = 360,
                    // The transform scales the stroke too, so inner rings get a wider stroke to look equally thick.
                    Thickness = UiSize.From(() => UiLength.OfParent(Thickness / ZoomOf(window, windows), 0.04)),
                    LevelColor = UiValue.From(() => Warn(window, color())),
                    TrackColor = UiValue.From(() => Mix(Card, color(), 0.26)),
                    Fill = true,
                },
            ],
        };
    }

    static UiStack Block(Window window, RingValues values, Func<string> color)
    {
        return new UiStack
        {
            Key = window.Key + "Block",
            Direction = "vertical",
            Justify = UiComponentJustify.Start,
            Align = "start",
            Gap = UiLength.OfParent(0.01, 0.01),
            Children =
            [
                new UiTextRun
                {
                    Key = window.Key + "Label",
                    Text = "● " + window.Name,
                    Spans = UiValue.From<IReadOnlyList<UiTextSpan>>(() =>
                    [
                        new UiTextSpan { Text = "● ", Color = Warn(window, color()) },
                        new UiTextSpan { Text = window.Name, Color = Label },
                    ]),
                    Size = UiLength.OfParent(0.11, 0.06),
                    Weight = "semibold",
                    Color = Label,
                    Align = "start",
                    MaxLines = 1,
                },
                new UiTextRun
                {
                    Key = window.Key + "Percent",
                    Text = UiText.From(() => PercentText(window.Percent.Value ?? 0)),
                    Size = UiLength.OfParent(0.27, 0.14),
                    Weight = "bold",
                    Color = Ink,
                    Align = "start",
                    MaxLines = 1,
                },
                // Always present: the renderer does not lay out a line inserted later into an existing block.
                new UiTextRun
                {
                    Key = window.Key + "Reset",
                    Text = UiText.From(() => ResetText(window, values)),
                    Size = UiLength.OfParent(0.085, 0.05),
                    Weight = "regular",
                    Color = Muted,
                    Align = "start",
                    MaxLines = 1,
                },
            ],
        };
    }

    /// <summary>
    /// The outermost present window fills the ring area; each further one steps in by the same ratio.
    /// </summary>
    static double ZoomOf(Window window, Window[] windows)
    {
        var index = Present(windows).IndexOf(window);
        return index < 0 ? 0 : Math.Pow(Step, index);
    }

    static List<Window> Present(Window[] windows) => windows.Where(window => window.IsPresent).ToList();

    static string Warn(Window window, string color)
    {
        if (window.Percent.Value is not double percent)
        {
            return color;
        }

        if (percent >= 95)
        {
            return "#EF4444";
        }

        return percent >= 85 ? "#F59E0B" : color;
    }

    static string ResetText(Window window, RingValues values)
    {
        _ = values.Tick.Value;
        return Stamp(window.Reset) is double stamp ? "Resets in " + Remaining(stamp) : "";
    }

    static double? Stamp(UiState<double?> reset)
    {
        if (reset.Value is not double stamp || stamp < 1_000_000_000d)
        {
            return null;
        }

        return stamp;
    }

    static string Remaining(double unixSeconds)
    {
        var left = DateTimeOffset.FromUnixTimeSeconds((long)unixSeconds) - DateTimeOffset.UtcNow;
        if (left.TotalMinutes < 1)
        {
            return "<1 min";
        }

        if (left.TotalDays >= 1)
        {
            var days = (int)left.TotalDays;
            var text = days == 1 ? "1 day" : $"{days} days";
            return left.Hours > 0 ? $"{text} {left.Hours} hr" : text;
        }

        if (left.TotalHours >= 1)
        {
            var hours = (int)left.TotalHours;
            return left.Minutes > 0 ? $"{hours} hr {left.Minutes} min" : $"{hours} hr";
        }

        return $"{left.Minutes} min";
    }

    static string PercentText(double percent)
    {
        return Math.Clamp((int)Math.Round(percent), 0, 100) + "%";
    }

    /// <summary>
    /// A contrasting second ring: blue next to a warm main color, orange next to a cool one.
    /// </summary>
    public static string AccentFor(string hex)
    {
        if (!IsHex(hex))
        {
            return "#F0883E";
        }

        var r = Convert.ToInt32(hex.Substring(1, 2), 16);
        var b = Convert.ToInt32(hex.Substring(5, 2), 16);
        return r > b ? "#4A9EFF" : "#F0883E";
    }

    public static string Shade(string hex, double towardWhite)
    {
        return towardWhite >= 0 ? Mix(hex, "#FFFFFF", towardWhite) : Mix(hex, "#000000", -towardWhite);
    }

    static string Mix(string from, string to, double amount)
    {
        if (!IsHex(from) || !IsHex(to))
        {
            return IsHex(from) ? from : "#3B82F6";
        }

        int Channel(int offset)
        {
            var a = Convert.ToInt32(from.Substring(offset, 2), 16);
            var b = Convert.ToInt32(to.Substring(offset, 2), 16);
            return (int)Math.Clamp(Math.Round(a + (b - a) * amount), 0, 255);
        }

        return $"#{Channel(1):X2}{Channel(3):X2}{Channel(5):X2}";
    }

    public static bool IsHex(string? hex) =>
        hex is { Length: 7 } && hex[0] == '#' && hex.Skip(1).All(Uri.IsHexDigit);

    sealed record Window(string Key, string Name, UiState<double?> Percent, UiState<double?> Reset)
    {
        public bool IsPresent => Percent.Value is >= 0;
    }
}

sealed class RingValues
{
    public UiState<double?> Month { get; } = new(null);
    public UiState<double?> Week { get; } = new(null);
    public UiState<double?> Session { get; } = new(null);
    public UiState<double?> MonthReset { get; } = new(null);
    public UiState<double?> WeekReset { get; } = new(null);
    public UiState<double?> SessionReset { get; } = new(null);
    public UiState<int> Tick { get; } = new(0);

    public string MonthVariable { get; init; } = "";
    public string WeekVariable { get; init; } = "";
    public string SessionVariable { get; init; } = "";
    public string MonthResetVariable { get; init; } = "";
    public string WeekResetVariable { get; init; } = "";
    public string SessionResetVariable { get; init; } = "";
}
