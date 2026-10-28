using System.Text.Json;
using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Ui;
using MacroDeck.Sdk.Variables;
using MacroDeck.Sdk.Widgets;
using MacroDeck.Ui.Model.Events;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Model.Nodes;
using MacroDeck.Ui.Model.Patches;
using MacroDeck.Ui.Runtime;
using MacroDeck.Ui.Widgets;

namespace AgentLimitRings;

public sealed class PluginIntegration : IPluginIntegration, IWidgetTypeProvider, IUiProvider
{
    const string LocalTypeId = "rings";
    static readonly WidgetTypeDescriptor Descriptor = new(
        LocalTypeId,
        LocalizedText.FromLiteral("Limit rings"),
        LocalizedText.FromLiteral("Usage rings, one inside the other, with each window's percent and time until reset."),
        DefaultData: """
            {"title":"Codex","color":"#3B82F6","monthVariable":"agent_codex_month_percent","weekVariable":"agent_codex_week_percent","sessionVariable":"agent_codex_5h_percent","monthResetVariable":"","weekResetVariable":"agent_codex_week_resets_at","sessionResetVariable":"agent_codex_5h_resets_at","backgroundColor":"#111111","border":{"style":"off"}}
            """,
        DataSchema: """
            {"type":"object","additionalProperties":true,"properties":{"title":{"type":"string"},"color":{"type":"string"},"monthVariable":{"type":"string"},"weekVariable":{"type":"string"},"sessionVariable":{"type":"string"},"monthResetVariable":{"type":"string"},"weekResetVariable":{"type":"string"},"sessionResetVariable":{"type":"string"},"accentColor":{"type":"string"}},"required":["title","color","monthVariable","weekVariable","sessionVariable"]}
            """,
        HasConfiguration: false);

    readonly List<WeakReference<RingValues>> _live = [];
    readonly object _gate = new();
    IUserVariableApi? _variables;
    CancellationTokenSource? _refresh;
    string? _widgetTypeId;

    public string ProviderName => "Agent limits";

    public IReadOnlyList<IActionDefinition> Actions { get; } = [];

    public IReadOnlyList<WidgetTypeDescriptor> GetWidgetTypes() => [Descriptor];

    public IReadOnlyList<UiSurfaceDeclaration> Surfaces { get; } =
    [
        new() { Kind = UiSurfaceKinds.Widget, SessionMode = UiSessionModes.Shared },
        new() { Kind = UiSurfaceKinds.Preview, SessionMode = UiSessionModes.Shared },
    ];

    public IReadOnlyList<UiPageDeclaration> Pages => [];

    public Task InitializeAsync(IIntegrationContext context)
    {
        _variables = context.UserVariables;
        return Task.CompletedTask;
    }

    public async Task InitializeAsync(IWidgetTypeProviderContext context, CancellationToken cancellationToken = default)
    {
        var registration = await context.RegisterWidgetTypeAsync(Descriptor, cancellationToken);
        _widgetTypeId = registration.WidgetTypeId;
        _refresh = new CancellationTokenSource();
        _ = RefreshLoop(_refresh.Token);
    }

    public Task ShutdownAsync()
    {
        _refresh?.Cancel();
        return Task.CompletedTask;
    }

    public Task<IUiSession?> CreateSessionAsync(UiSessionRequest request, CancellationToken cancellationToken)
    {
        if (_widgetTypeId is null || !UiWidgetSurface.IsFor(request.Surface, _widgetTypeId))
        {
            return Task.FromResult<IUiSession?>(null);
        }

        var data = UiWidgetSurface.Data(request.Surface);
        var title = Text(data, "title") ?? "Limits";
        var color = Text(data, "color") ?? "#3B82F6";
        var accent = Text(data, "accentColor") is { } custom && RingsView.IsHex(custom) ? custom : RingsView.AccentFor(color);
        var values = new RingValues
        {
            MonthVariable = Text(data, "monthVariable") ?? "",
            WeekVariable = Text(data, "weekVariable") ?? "",
            SessionVariable = Text(data, "sessionVariable") ?? "",
            MonthResetVariable = Text(data, "monthResetVariable") ?? "",
            WeekResetVariable = Text(data, "weekResetVariable") ?? "",
            SessionResetVariable = Text(data, "sessionResetVariable") ?? "",
        };

        if (UiWidgetSurface.IsSampleRequested(request.Surface))
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            values.Session.Set(42);
            values.Week.Set(61);
            values.SessionReset.Set(now + 2 * 3600 + 16 * 60 + 30);
            values.WeekReset.Set(now + 2 * 24 * 3600 + 9 * 3600 + 30 * 60);
            values.Tick.Set(1);
        }
        else
        {
            lock (_gate)
            {
                _live.Add(new WeakReference<RingValues>(values));
            }

            _ = ReadAll(CancellationToken.None);
        }

        var view = new UiView(request.Surface, RingsView.Build(title, color, accent, values));
        return Task.FromResult<IUiSession?>(new ViewSession(view));
    }

    async Task RefreshLoop(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ReadAll(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // The next tick tries again. A failed read leaves the last numbers in place.
            }

            BumpTicks();

            try
            {
                await timer.WaitForNextTickAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    async Task ReadAll(CancellationToken cancellationToken)
    {
        RingValues[] models;
        lock (_gate)
        {
            _live.RemoveAll(item => !item.TryGetTarget(out _));
            models = _live.Select(item => item.TryGetTarget(out var model) ? model : null).OfType<RingValues>().ToArray();
        }

        if (_variables is null || models.Length == 0)
        {
            return;
        }

        var context = new UserVariableContext(null, null, null);
        var readings = await ReadNames(models.SelectMany(model => new[] { model.MonthVariable, model.WeekVariable, model.SessionVariable }), AsPercent, context, cancellationToken);
        var resets = await ReadNames(models.SelectMany(model => new[] { model.MonthResetVariable, model.WeekResetVariable, model.SessionResetVariable }), AsUnix, context, cancellationToken);

        foreach (var model in models)
        {
            model.Month.Set(Lookup(readings, model.MonthVariable));
            model.Week.Set(Lookup(readings, model.WeekVariable));
            model.Session.Set(Lookup(readings, model.SessionVariable));
            model.MonthReset.Set(Lookup(resets, model.MonthResetVariable));
            model.WeekReset.Set(Lookup(resets, model.WeekResetVariable));
            model.SessionReset.Set(Lookup(resets, model.SessionResetVariable));
        }
    }

    async Task<Dictionary<string, double?>> ReadNames(
        IEnumerable<string> names,
        Func<object?, double?> parse,
        UserVariableContext context,
        CancellationToken cancellationToken)
    {
        var readings = new Dictionary<string, double?>(StringComparer.Ordinal);
        foreach (var name in names.Where(name => name.Length > 0).Distinct(StringComparer.Ordinal))
        {
            var read = await _variables!.ReadInContextAsync(name, context, cancellationToken);
            readings[name] = read.Status == UserVariableReadStatus.Found ? parse(read.Value) ?? parse(read.Text) : null;
        }

        return readings;
    }

    void BumpTicks()
    {
        RingValues[] models;
        lock (_gate)
        {
            models = _live.Select(item => item.TryGetTarget(out var model) ? model : null).OfType<RingValues>().ToArray();
        }

        foreach (var model in models)
        {
            model.Tick.Set(model.Tick.Value + 1);
        }
    }

    static double? Lookup(Dictionary<string, double?> readings, string name)
    {
        if (name.Length == 0)
        {
            return null;
        }

        return readings.TryGetValue(name, out var value) ? value : null;
    }

    static double? AsPercent(object? value)
    {
        double? number = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            string text when double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var parsed) => parsed,
            JsonElement element when element.ValueKind == JsonValueKind.String && double.TryParse(element.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
        if (number is not double percent || percent < 0)
        {
            return null;
        }

        return percent;
    }

    static double? AsUnix(object? value)
    {
        double? number = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            string text when double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var parsed) => parsed,
            JsonElement element when element.ValueKind == JsonValueKind.String && double.TryParse(element.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
        if (number is not double stamp || double.IsNaN(stamp) || double.IsInfinity(stamp))
        {
            return null;
        }

        if (stamp > 10_000_000_000d)
        {
            stamp /= 1000d;
        }

        if (stamp < 1_000_000_000d || stamp > 10_000_000_000d)
        {
            return null;
        }

        return stamp;
    }

    static string? Text(JsonElement data, string name)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    sealed class ViewSession : IUiSession
    {
        readonly UiView _view;

        public ViewSession(UiView view)
        {
            _view = view;
            _view.Changed += (_, args) => Changed?.Invoke(this, args);
            _view.HandlerFaulted += (_, args) => Faulted?.Invoke(this, new UiSessionFaultedEventArgs(args.EventName, args.Exception));
        }

        public event EventHandler? Changed;

        public event EventHandler<UiSessionFaultedEventArgs>? Faulted;

        public UiTree BuildTree() => _view.Tree;

        public IReadOnlyList<UiPatch> DrainPatches() => _view.DrainPatches();

        public void Dispatch(UiEvent uiEvent) => _view.Dispatch(uiEvent);

        public void Dispatch(UiEvent uiEvent, IUiEventOrigin origin) => _view.Dispatch(uiEvent);

        public ValueTask DisposeAsync()
        {
            _view.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
