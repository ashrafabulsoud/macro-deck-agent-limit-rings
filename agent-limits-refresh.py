#!/usr/bin/env python3
"""Refresh Macro Deck rings for Codex, Claude, Cursor, and Grok.

Reads each already-signed-in session and writes percent-used numbers plus
the unix time each real window resets. Tokens stay in this process. A window
the product does not report is left unset, so that ring stays hidden.
"""

import getpass
import json
import os
import select
import shutil
import subprocess
import urllib.error
import urllib.request
from datetime import datetime, timezone

HOME = os.path.expanduser("~")
SUPPORT = os.path.join(HOME, "Library/Application Support/app.macro-deck.macrodeck")


def find_cli(name):
    """The CLI on PATH, else the usual ~/.local/bin install."""
    return shutil.which(name) or os.path.join(HOME, ".local/bin", name)


CODEX = find_cli("codex")
GROK = find_cli("grok")

# name -> (agent, window). Missing names are windows that product does not expose.
VARIABLES = {
    "agent_codex_week_percent": "codex_week",
    "agent_codex_5h_percent": "codex_5h",
    "agent_claude_week_percent": "claude_week",
    "agent_claude_5h_percent": "claude_5h",
    "agent_cursor_month_percent": "cursor_month",
    "agent_cursor_auto_percent": "cursor_month",
    "agent_grok_month_percent": "grok_month",
    "agent_grok_week_percent": "grok_week",
    "agent_grok_5h_percent": "grok_5h",
    "agent_codex_week_resets_at": "codex_week_reset",
    "agent_codex_5h_resets_at": "codex_5h_reset",
    "agent_claude_week_resets_at": "claude_week_reset",
    "agent_claude_5h_resets_at": "claude_5h_reset",
    "agent_cursor_month_resets_at": "cursor_month_reset",
    "agent_grok_month_resets_at": "grok_month_reset",
    "agent_grok_week_resets_at": "grok_week_reset",
    "agent_grok_5h_resets_at": "grok_5h_reset",
}


def display_percent(value):
    """Turn a 0-100 usage figure into the integer drawn in the ring."""
    if value is None:
        return None
    value = float(value)
    if value < 0:
        return None
    if value > 100:
        value = 100
    rounded = int(round(value))
    if rounded == 0 and value > 0:
        rounded = 1
    return rounded


def unix_time(value):
    """Unix seconds for a reset. Percents and tokens are ignored."""
    if isinstance(value, bool) or value is None:
        return None
    if isinstance(value, (int, float)):
        number = float(value)
        if number > 10_000_000_000:
            number /= 1000.0
        if number < 1_000_000_000:
            return None
        return int(number)
    if isinstance(value, str):
        text = value.strip()
        if not text or "eyJ" in text or "@" in text:
            return None
        if text.isdigit():
            return unix_time(int(text))
        if text.endswith("Z"):
            text = text[:-1] + "+00:00"
        try:
            parsed = datetime.fromisoformat(text)
        except ValueError:
            return None
        if parsed.tzinfo is None:
            parsed = parsed.replace(tzinfo=timezone.utc)
        return int(parsed.timestamp())
    return None


def window_reset(window):
    if not isinstance(window, dict):
        return None
    for key in ("resetsAt", "resets_at", "resetAt", "reset_at", "expiresAt", "expires_at", "endsAt", "ends_at"):
        found = unix_time(window.get(key))
        if found:
            return found
    return None


def _read_json_line(stream, timeout):
    """Read one JSON-RPC line. Codex may write logs before the response."""
    ready, _, _ = select.select([stream], [], [], timeout)
    if not ready:
        return None
    while True:
        line = stream.readline()
        if not line:
            return None
        line = line.strip()
        if not line:
            continue
        try:
            return json.loads(line)
        except json.JSONDecodeError:
            ready, _, _ = select.select([stream], [], [], timeout)
            if not ready:
                return None


def codex_windows():
    proc = subprocess.Popen(
        [CODEX, "app-server"],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
    )
    try:
        initialize = {
            "jsonrpc": "2.0",
            "id": 1,
            "method": "initialize",
            "params": {
                "clientInfo": {"name": "macrodeck-limits", "version": "1"},
                "capabilities": {},
            },
        }
        proc.stdin.write(json.dumps(initialize) + "\n")
        proc.stdin.flush()
        started = None
        for _ in range(8):
            message = _read_json_line(proc.stdout, 8)
            if message and message.get("id") == 1:
                started = message
                break
        if not started or started.get("error"):
            raise RuntimeError("codex app-server did not start")
        proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "initialized"}) + "\n")
        proc.stdin.write(
            json.dumps({"jsonrpc": "2.0", "id": 2, "method": "account/rateLimits/read"}) + "\n"
        )
        proc.stdin.flush()
        limits = None
        for _ in range(8):
            message = _read_json_line(proc.stdout, 8)
            if not message or message.get("id") != 2:
                continue
            limits = (message.get("result") or {}).get("rateLimits") or {}
            break
        if not isinstance(limits, dict):
            raise RuntimeError("codex rate limit missing")
        found = {}
        for name in ("primary", "secondary"):
            window = limits.get(name) or {}
            if not isinstance(window, dict) or window.get("usedPercent") is None:
                continue
            minutes = window.get("windowDurationMins")
            slot = _minutes_slot(minutes)
            if slot is None:
                print(f"codex extra window minutes={minutes} used={display_percent(window.get('usedPercent'))}")
                continue
            found[slot] = display_percent(window.get("usedPercent"))
            reset = window_reset(window)
            if reset:
                found[slot + "_reset"] = reset
            else:
                print(f"codex {slot} reset keys", ",".join(str(key) for key in window.keys()))
        if "codex_5h" not in found:
            raise RuntimeError("codex 5-hour window missing")
        return found
    finally:
        if proc.poll() is None:
            proc.kill()
        proc.wait(timeout=5)


def _minutes_slot(minutes):
    if minutes == 300:
        return "codex_5h"
    if minutes == 10080:
        return "codex_week"
    if minutes in (43200, 43800, 44640):
        return "codex_month"
    return None


def keychain(service, account=None):
    cmd = ["/usr/bin/security", "find-generic-password", "-s", service, "-w"]
    if account:
        cmd[2:2] = ["-a", account]
    return subprocess.check_output(cmd, text=True, stderr=subprocess.DEVNULL).strip()


def claude_windows():
    raw = keychain("Claude Code-credentials", getpass.getuser())
    token = json.loads(raw)["claudeAiOauth"]["accessToken"]
    req = urllib.request.Request(
        "https://api.anthropic.com/api/oauth/usage",
        headers={
            "Authorization": f"Bearer {token}",
            "anthropic-beta": "oauth-2025-04-20",
            "Accept": "application/json",
        },
    )
    try:
        with urllib.request.urlopen(req, timeout=20) as response:
            body = json.load(response)
    except urllib.error.HTTPError as error:
        raise RuntimeError(f"claude usage HTTP {error.code}") from None
    found = {}
    five_window = body.get("five_hour") or {}
    week_window = body.get("seven_day") or {}
    five = five_window.get("utilization") if isinstance(five_window, dict) else None
    week = week_window.get("utilization") if isinstance(week_window, dict) else None
    if five is not None:
        found["claude_5h"] = display_percent(five)
        reset = window_reset(five_window)
        if reset:
            found["claude_5h_reset"] = reset
        else:
            print("claude 5h reset keys", ",".join(str(key) for key in five_window.keys()))
    if week is not None:
        found["claude_week"] = display_percent(week)
        reset = window_reset(week_window)
        if reset:
            found["claude_week_reset"] = reset
        else:
            print("claude week reset keys", ",".join(str(key) for key in week_window.keys()))
    for key, value in body.items():
        if key in ("five_hour", "seven_day") or not isinstance(value, dict):
            continue
        if "utilization" in value and value.get("utilization") is not None:
            print(f"claude extra {key}={display_percent(value.get('utilization'))}")
    if "claude_5h" not in found:
        raise RuntimeError("claude 5-hour window missing")
    return found


def cursor_windows():
    token = keychain("cursor-access-token")
    req = urllib.request.Request(
        "https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage",
        data=b"{}",
        method="POST",
        headers={
            "Authorization": f"Bearer {token}",
            "Content-Type": "application/json",
            "Connect-Protocol-Version": "1",
            "Accept": "application/json",
        },
    )
    with urllib.request.urlopen(req, timeout=20) as response:
        body = json.load(response)
    plan = body.get("planUsage") or {}
    # autoPercentUsed is already percentage points. About 0.11 means under 1%.
    used = plan.get("autoPercentUsed")
    if used is None:
        raise RuntimeError("cursor monthly window missing")
    found = {"cursor_month": display_percent(used)}
    reset = cursor_reset(body)
    if reset:
        found["cursor_month_reset"] = reset
    for key, value in plan.items():
        if key == "autoPercentUsed" or not isinstance(value, (int, float)):
            continue
        low = key.lower()
        if any(word in low for word in ("week", "hour", "session")):
            print(f"cursor extra {key}={value}")
    return found


def cursor_reset(body):
    """Billing-period end, as unix seconds. Only the field name is printed."""
    found = []

    def walk(value, path):
        if isinstance(value, dict):
            for key, child in value.items():
                child_path = f"{path}.{key}" if path else str(key)
                stamp = unix_time(child)
                low = str(key).lower()
                if stamp and any(word in low for word in ("end", "reset", "expir")):
                    found.append((child_path, stamp))
                elif isinstance(child, (dict, list)):
                    walk(child, child_path)
        elif isinstance(value, list) and value:
            walk(value[0], path + "[]")

    walk(body, "")
    if not found:
        return None
    for path, stamp in found:
        low = path.lower()
        if "cycle" in low or low.endswith("end"):
            print(f"cursor reset {path}")
            return stamp
    print(f"cursor reset {found[0][0]}")
    return found[0][1]


def _walk_token(value):
    """Find a grok.com access token. The value is never printed."""
    if isinstance(value, dict):
        for key in ("access_token", "accessToken"):
            token = value.get(key)
            if isinstance(token, str) and len(token) > 20:
                return token
        # Grok Build stores the session token on `key` next to the refresh token.
        stored = value.get("key")
        if (
            isinstance(stored, str)
            and len(stored) > 20
            and ("refresh_token" in value or "auth_mode" in value)
        ):
            return stored
        for child in value.values():
            found = _walk_token(child)
            if found:
                return found
    elif isinstance(value, list):
        for child in value:
            found = _walk_token(child)
            if found:
                return found
    return None


def _grok_slot(period):
    low = period.lower()
    if "week" in low:
        return "grok_week"
    if "month" in low:
        return "grok_month"
    if "hour" in low or "session" in low:
        return "grok_5h"
    return None


def _safe_error_hint(payload, content_type):
    """A short status phrase. Never a token, email, or response body."""
    if "json" not in (content_type or "").lower():
        return "not-json"
    try:
        body = json.loads(payload)
    except json.JSONDecodeError:
        return "not-json"
    if not isinstance(body, dict):
        return "json"
    bits = []
    for key in ("code", "error", "status", "reason"):
        value = body.get(key)
        if isinstance(value, str) and 0 < len(value) <= 60 and "eyJ" not in value and "@" not in value:
            bits.append(f"{key}={value}")
    return " ".join(bits) or "json"


def grok_windows():
    auth_path = os.path.join(HOME, ".grok", "auth.json")
    token = _walk_token(json.loads(open(auth_path, encoding="utf-8").read()))
    if not token:
        raise RuntimeError("grok session missing")
    version = "1.0.70"
    try:
        raw = subprocess.check_output([GROK, "version"], text=True, stderr=subprocess.DEVNULL, timeout=8)
        for part in raw.split():
            if part[:1].isdigit():
                version = part
                break
    except (OSError, subprocess.SubprocessError):
        pass
    headers = {
        "Authorization": f"Bearer {token}",
        "Accept": "application/json",
        "x-grok-client-version": version,
        "x-grok-client-mode": "cli",
        "x-grok-client-identifier": "grok-build",
    }
    body = None
    last_status = None
    for url in (
        "https://cli-chat-proxy.grok.com/v1/billing?format=credits",
        "https://grok.com/billing?format=credits",
    ):
        req = urllib.request.Request(url, headers=headers)
        host = url.split("/")[2]
        try:
            with urllib.request.urlopen(req, timeout=20) as response:
                payload = response.read()
                content_type = response.headers.get("Content-Type", "")
                status = response.status
        except urllib.error.HTTPError as error:
            last_status = error.code
            hint = _safe_error_hint(error.read(), error.headers.get("Content-Type", ""))
            print(f"grok http {host} status={error.code} {hint}".rstrip())
            continue
        if "json" not in content_type.lower():
            last_status = "not-json"
            print(f"grok http {host} status={status} not-json")
            continue
        body = json.loads(payload)
        break
    if not isinstance(body, dict):
        raise RuntimeError(f"grok billing unavailable ({last_status})")
    config = body.get("config") if isinstance(body.get("config"), dict) else body
    credit = config.get("creditUsagePercent") if isinstance(config, dict) else None
    period = ""
    current = config.get("currentPeriod") if isinstance(config, dict) else None
    if isinstance(current, dict) and isinstance(current.get("type"), str):
        period = current["type"]
    slot = _grok_slot(period)
    if credit is None or slot is None:
        raise RuntimeError(f"grok period unset ({period or 'unknown'})")
    # creditUsagePercent is percentage points for the current plan window.
    print(f"grok period {period} used={display_percent(credit)}")
    found = {slot: display_percent(credit)}
    reset = unix_time(config.get("billingPeriodEnd") if isinstance(config, dict) else None)
    if reset:
        found[slot + "_reset"] = reset
    return found


def macrodeck_call(method, path, body=None):
    secret = open(os.path.join(SUPPORT, "loopback-secret"), encoding="utf-8").read().strip()
    port = open(os.path.join(SUPPORT, "loopback-port"), encoding="utf-8").read().strip()
    data = None
    headers = {
        "Accept": "application/json",
        "X-MacroDeck-Loopback-Secret": secret,
    }
    if body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(
        f"http://127.0.0.1:{port}{path}",
        data=data,
        headers=headers,
        method=method,
    )
    try:
        with urllib.request.urlopen(req, timeout=15) as response:
            raw = response.read()
            return json.loads(raw) if raw else None
    except urllib.error.HTTPError as error:
        detail = error.read().decode("utf-8", "replace")[:180]
        raise RuntimeError(f"macro deck {error.code}: {detail}") from None


def screen_locked():
    try:
        state = macrodeck_call("GET", "/api/system/lock-state")
    except Exception:
        return False
    return bool(isinstance(state, dict) and state.get("locked"))


def ensure_variables(names):
    payload = macrodeck_call("GET", "/api/variables")
    found = {}
    for variable in payload.get("variables") or []:
        if variable.get("name") in names:
            found[variable["name"]] = variable["id"]
    for name in names:
        if name in found:
            continue
        created = macrodeck_call(
            "POST",
            "/api/variables",
            {"name": name, "type": "numeric"},
        )
        variable = (created or {}).get("variable") or created or {}
        variable_id = variable.get("id")
        if variable_id:
            found[name] = variable_id
    return found


def main():
    readers = (
        ("codex", codex_windows),
        ("claude", claude_windows),
        ("cursor", cursor_windows),
        ("grok", grok_windows),
    )
    values = {}
    errors = []
    for name, reader in readers:
        try:
            values.update(reader())
        except Exception as error:
            text = str(error)
            safe = text if isinstance(error, RuntimeError) and len(text) < 120 and "Bearer" not in text and "eyJ" not in text else type(error).__name__
            errors.append(f"{name}: {safe}")
    print("read", ", ".join(f"{name}={value}" for name, value in values.items()) or "none")
    wanted = {
        variable: values[slot]
        for variable, slot in VARIABLES.items()
        if slot in values and values[slot] is not None
    }
    if not wanted:
        if errors:
            print("skipped", ", ".join(errors))
        return 1
    if screen_locked():
        # A new variable is seeded at 0. Writing it while the screen is locked
        # would paint that 0 as real usage, so leave missing rings as dashes.
        print("locked")
        if errors:
            print("skipped", ", ".join(errors))
        return 1
    ids = ensure_variables(list(wanted))
    updated = []
    for variable, value in wanted.items():
        variable_id = ids.get(variable)
        if not variable_id:
            errors.append(f"{variable}: not written")
            continue
        result = macrodeck_call(
            "PATCH",
            f"/api/variables/{variable_id}/value",
            {"id": variable_id, "value": str(value)},
        )
        if isinstance(result, dict) and result.get("success") is False:
            code = (result.get("error") or {}).get("code") or "rejected"
            errors.append(f"{variable}: {code}")
            continue
        updated.append(f"{variable}={value}")
    print("updated", ", ".join(updated) if updated else "none")
    if errors:
        print("skipped", ", ".join(errors))
        return 1 if not updated else 0
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
