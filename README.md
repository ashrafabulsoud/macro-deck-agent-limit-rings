# Agent Limit Rings for Macro Deck

A [Macro Deck](https://macro-deck.app) widget that shows how much of your AI coding agents' usage limits you've used — **Codex, Claude Code, Cursor, and Grok** — as concentric rings, with the percent used and time until each window resets.

```
 ╭──────╮     ● Session
 │ ╭──╮ │     42%
 │ │  │ │     Resets in 2 hr 16 min
 │ ╰──╯ │     ● Weekly
 ╰──────╯     61%
              Resets in 2 days 9 hr
```

- **One ring per usage window the agent actually publishes.** Claude and Codex show Session (5-hour) + Weekly; Cursor shows its Monthly billing period; Grok shows its plan window. Windows an agent doesn't publish are simply not drawn — no empty rings, no dashes.
- Rings go outside → inside: Session, Weekly, Monthly. Each track is a dark shade of its own ring color.
- A ring and its legend dot turn **amber at 85%** and **red at 95%**.
- Live countdowns, refreshed every 10 seconds.

## How it works

There are two parts:

| Part | What it does |
| --- | --- |
| **The plugin** (`.macroDeckPlugin`) | Adds a "Limit rings" widget. It only reads Macro Deck variables and draws them. It makes no network calls and never sees any credentials. |
| **The refresh script** (`agent-limits-refresh.py`) | Run by a Macro Deck automation every 5 minutes. It reads each CLI's existing sign-in on your Mac, asks that vendor for your current usage, and writes the numbers into Macro Deck variables. |

You can use the plugin without the script if you fill the variables some other way (see [Variables](#variables)).

## Requirements

- **Macro Deck 3.0.0-beta.19 or newer**
- **For the refresh script:** macOS with Python 3 (`/usr/bin/python3` works), signed in to the CLIs you want to track:
  - Codex — `codex` CLI, signed in with ChatGPT
  - Claude — Claude Code, signed in with a Claude subscription
  - Cursor — the Cursor app, signed in
  - Grok — the Grok CLI, signed in

Any agent you don't use is skipped; its card just stays empty.

## Install

### 1. Install the plugin

1. Download `com.ashraf.agent-limit-rings-<version>.macroDeckPlugin` from the [latest release](../../releases/latest).
2. In Macro Deck, open **Integrations → Add → Install from file…** and pick the downloaded file.
3. The plugin is **unsigned**, so Macro Deck will ask you to allow it. Allow it.
4. Make sure **Agent Limit Rings** is switched on in the Integrations list.

### 2. Add the cards

**Option A — import the ready-made page (recommended).**
Download `Limits.macroDeckFolder` and import it from the Deck page's **Folders** panel (**Import folder from a .macroDeckFolder file**). You get a "Limits" folder with four cards (Codex, Claude, Cursor, Grok) on a 6 × 4 grid.

**Option B — add a widget by hand.**
Add a **Limit rings** widget to any folder, size it about 3 × 2 cells, and set its data (see [Widget settings](#widget-settings)).

### 3. Set up the refresh script (macOS)

1. Copy `agent-limits-refresh.py` to
   `~/Library/Application Support/app.macro-deck.macrodeck/`
   and make it executable:
   ```bash
   chmod +x ~/Library/Application\ Support/app.macro-deck.macrodeck/agent-limits-refresh.py
   ```
2. Run it once from Terminal to check it works and to create the variables:
   ```bash
   /usr/bin/python3 ~/Library/Application\ Support/app.macro-deck.macrodeck/agent-limits-refresh.py
   ```
   macOS may ask whether `python3` may read the **Claude Code** and **Cursor** keychain items. Choose **Always Allow** so the automation can run unattended.
   You should see lines like `read codex_5h=12, …` and `updated agent_codex_5h_percent=12, …`.
3. In Macro Deck, open **Automations** and add one that runs the program `/usr/bin/python3` with the script path as its argument. Trigger it **every 5 minutes** and **when the server starts**.

The cards fill in within a few seconds of the first successful run.

## Widget settings

Each card is configured through its widget data (JSON):

| Key | Required | Meaning |
| --- | --- | --- |
| `title` | yes | Name shown in the middle of the rings, e.g. `"Claude"` |
| `color` | yes | Hex color of the first ring, e.g. `"#E08A3C"` |
| `accentColor` | no | Hex color of the second ring. Default: blue next to a warm `color`, orange next to a cool one |
| `sessionVariable` / `weekVariable` / `monthVariable` | yes | Variable holding percent used (0–100) for that window. Use `""` for a window the agent doesn't have |
| `sessionResetVariable` / `weekResetVariable` / `monthResetVariable` | no | Variable holding the reset time as unix seconds. `""` hides that reset line |

Example (the Claude card):

```json
{
  "title": "Claude",
  "color": "#E08A3C",
  "sessionVariable": "agent_claude_5h_percent",
  "weekVariable": "agent_claude_week_percent",
  "monthVariable": "agent_claude_month_percent",
  "sessionResetVariable": "agent_claude_5h_resets_at",
  "weekResetVariable": "agent_claude_week_resets_at",
  "monthResetVariable": ""
}
```

## Variables

The refresh script writes these numeric variables. It only creates a variable for a window it actually read.

| Agent | Percent used | Reset (unix seconds) |
| --- | --- | --- |
| Codex | `agent_codex_5h_percent`, `agent_codex_week_percent` | `agent_codex_5h_resets_at`, `agent_codex_week_resets_at` |
| Claude | `agent_claude_5h_percent`, `agent_claude_week_percent` | `agent_claude_5h_resets_at`, `agent_claude_week_resets_at` |
| Cursor | `agent_cursor_month_percent` | `agent_cursor_month_resets_at` |
| Grok | `agent_grok_week_percent` (or `_month_` / `_5h_`, matching the plan's window) | `agent_grok_week_resets_at` (same rule) |

## Privacy and security

- **The plugin** reads Macro Deck variables only. No network access, no credentials.
- **The refresh script** runs locally on your Mac:
  - It reads sign-ins the CLIs already stored: the macOS keychain items `Claude Code-credentials` and `cursor-access-token`, `~/.grok/auth.json`, and Codex through `codex app-server`.
  - Each token is sent **only to its own vendor** (`api.anthropic.com`, `api2.cursor.sh`, `cli-chat-proxy.grok.com` / `grok.com`) to ask for usage, and to nothing else.
  - It talks to Macro Deck through the local loopback API (`127.0.0.1`), using the loopback secret Macro Deck keeps in its own Application Support folder.
  - Tokens are never printed or written to disk; error messages are filtered so tokens and emails don't end up in logs.
  - While the screen is locked it reads but doesn't write, so a locked deck never shows stale zeros as real usage.
- Read the script before you run it — it's a single file of plain Python with no dependencies.

> **Note:** the usage endpoints the script calls are what the official CLIs and apps use internally. They are not public APIs and may change without notice. If a vendor changes one, that agent's card stops updating until the script is updated; the other agents keep working.

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| A card stays empty | Run the script in Terminal and check the `skipped` line, e.g. `claude: claude usage HTTP 401` means you need to sign in to that CLI again. |
| `skipped … not written` | Macro Deck wasn't running, or the loopback API was unavailable. Start Macro Deck and run the script again. |
| Script prints `locked` | The screen was locked; it will write on the next run after you unlock. |
| Keychain prompt every 5 minutes | You chose "Allow" instead of "Always Allow". Run the script once in Terminal and choose "Always Allow". |
| Macro Deck crashes on start with a `CultureInfo` error | A .NET issue with some macOS region/calendar settings. Launch Macro Deck with `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8`. |

## Build from source

Requires the .NET 10 SDK.

```bash
dotnet build source/AgentLimitRings/AgentLimitRings.csproj -c Release
```

To build an installable package, publish each runtime and zip them with the manifest:

```bash
for rid in osx-arm64 win-x64 linux-x64; do
  dotnet publish source/AgentLimitRings/AgentLimitRings.csproj -c Release -r $rid \
    --self-contained false -o build/runtimes/$rid
done
rm -f build/runtimes/*/AgentLimitRings build/runtimes/*/AgentLimitRings.exe build/runtimes/*/manifest.json
cp source/AgentLimitRings/manifest.json build/
mkdir -p build/Assets && cp source/AgentLimitRings/Assets/icon.svg build/Assets/
(cd build && zip -qrD ../com.ashraf.agent-limit-rings-<version>.macroDeckPlugin manifest.json Assets runtimes)
```

## License

[MIT](LICENSE)
