# Terminal Translator

Terminal Translator's current Phase-1 live mode runs a dedicated Windows PowerShell session in
Windows Terminal and shows useful English output with Simplified Chinese translations in a separate
companion pane. In this mode, the program pane remains the source of truth: translation never
replaces its output and never writes to its input.

## Requirements

For development:

- Windows 11 x64 with ConPTY support.
- Windows Terminal with the `wt.exe` execution alias enabled.
- Windows PowerShell 5.1 available as `powershell.exe`.
- .NET 10 SDK (the version selected by `global.json`).

The published `win-x64` executable is self-contained and does not require a separately installed
.NET runtime. Windows Terminal and Windows PowerShell 5.1 are still required. Git and Python are
optional, but useful when exercising the representative validation scenarios.

## Build and run from source

From the repository root:

```powershell
dotnet restore
dotnet build TerminalTranslator.sln -c Release --no-restore
dotnet run --project src/TerminalTranslator.Cli -c Release -- --help
```

When running a public command from source, place its arguments after `--`:

```powershell
dotnet run --project src/TerminalTranslator.Cli -c Release -- configure `
  --endpoint "https://provider.example/v1/chat/completions" `
  --model "provider-model" `
  --api-key-env "TT_PROVIDER_API_KEY"
```

For a self-contained executable, run:

```powershell
./scripts/publish.ps1
$env:Path = "$PWD\artifacts\publish\win-x64;$env:Path"
tt --help
```

The PATH assignment above is only a development-artifact convenience. To install the supported
Windows PowerShell integration and the stable product command, run the published executable once:

```powershell
& .\artifacts\publish\win-x64\tt.exe configure --capture enabled
```

This copies the product binary below `%LOCALAPPDATA%\TerminalTranslator\Versions`, installs the
managed profile loader, and exposes a native-semantics `tt` alias in each new Windows PowerShell 5.1
profile session. Close the current shell and open a new ordinary shell, then `Get-Command tt` and all
public commands work without a repository path or PATH update. If another command named `tt` already
exists, Terminal Translator preserves it and warns instead of replacing it.

The capture loader decides native I/O mode immediately before process creation. Ordinary
capture-safe native commands use PowerShell's joined stdout/stderr reader for reliable `tt last`
capture, while TTY-sensitive applications such as Codex keep genuine console stdin/stdout/stderr and
launch normally. Additional interactive executable names can be supplied for a session through the
semicolon- or comma-separated `TT_TTY_SENSITIVE_APPLICATIONS` environment variable.

## Provider configuration

Terminal Translator currently supports a chat-completion-compatible HTTP endpoint. Put the API
key in an environment variable before starting the session:

```powershell
$env:TT_PROVIDER_API_KEY = "<test-or-provider-key>"

tt configure `
  --endpoint "https://provider.example/v1/chat/completions" `
  --model "provider-model" `
  --api-key-env "TT_PROVIDER_API_KEY" `
  --timeout-ms 10000
```

`--timeout-ms` is optional and accepts 100 through 10000 milliseconds; the current default is
10000 milliseconds. HTTPS is required in normal use. Loopback HTTP is accepted only with
`--allow-loopback-http`, which exists for local deterministic validation.

Configuration is stored under
`%LOCALAPPDATA%\TerminalTranslator\provider-settings.json`. The file contains the endpoint,
model, timeout, and the environment-variable **name** only. It never contains the API key value.
The key must be available in the environment inherited by `tt start`; setting `$env:...` affects
only the current PowerShell process and processes started from it.

## Commands

| Command | Purpose |
|---------|---------|
| `tt configure --endpoint <uri> --model <id> --api-key-env <name> [--timeout-ms <100..10000>]` | Store non-sensitive provider settings. |
| `tt start [--working-directory <path>]` | Open the managed Windows Terminal window and dedicated PowerShell session. |
| `tt on` | Disclose the destination/scope, request consent, and enable translation. |
| `tt off` | Disable translation, cancel pending work, and suppress late results. |
| `tt status` | Show content-free state, provider host/model, queue counts, and aggregate privacy/overload counts. |
| `tt configure --capture enabled\|disabled` | Install/enable or disable the ordinary PowerShell capture integration and managed `tt` command. |
| `tt last` | Translate the strict previous completed command output inline. |
| `tt ask <question...>` | Ask one stateless terminal-oriented question. |
| `tt ask last <question...>` | Ask one stateless question using the strict previous command context. |

Run `on`, `off`, and `status` inside the managed program pane. Their authenticated session context
is created by `tt start`; invoking them in an unrelated terminal reports that no live translation
session is available.

## Session model

`tt start` opens one Windows Terminal window with two panes:

- The program pane hosts the real Windows PowerShell child through ConPTY. Keyboard input, paste,
  Ctrl+C, resize, Unicode, output, and the child exit code stay on this path.
- The companion pane receives transient translation and content-free status events. It cannot
  inject text or commands into the program.

Every new session starts with `translation: disabled`. Starting the session does not send terminal
content to the provider. `tt on` first shows the configured external host, model, eligible-content
scope, retention behavior, and secret-skipping rule. Only an explicit `y` enables transmission.

Consent is scoped to the current session generation and provider fingerprint. `tt off` advances
the generation, cancels and clears pending translation work, and rejects late results while leaving
PowerShell running. A later `tt on` asks for consent again and authorizes only the new generation.

## Privacy and failure behavior

- Secret screening happens locally before queueing and again before provider serialization.
  Credential assignments, authorization values, token-like strings, private-key material, and
  malformed sensitive blocks cause the entire candidate to be skipped. Detection uncertainty or
  detector failure is fail-closed.
- Privacy notices contain only a generic reason and aggregate count; they never echo the suspected
  secret.
- Terminal source and translations are transient in-memory session data. They are cleared on
  disable/teardown and are not written to settings, history files, product logs, or telemetry.
- Provider errors, timeouts, malformed responses, overload, and companion disconnects affect only
  the optional translation path. Raw ConPTY output and input continue independently.
- Translation queues and retained candidate text are bounded. Under sustained overload, candidates
  may expire, be replaced, evicted, or dropped; short content-free degradation notices are
  aggregated instead of flooding the companion.
- Provider requests are not automatically retried. Redirect following and cookies are disabled.
- If the companion disconnects, the shell remains usable. A reconnect can receive future events,
  but content missed while disconnected is not replayed.

## Offline validation

The repository includes a deterministic loopback provider used only by tests. It supports success,
timeout, HTTP error, malformed, empty, and oversized-response scenarios without contacting the
public internet.

Run the complete offline validation workflow with:

```powershell
./scripts/validate.ps1
```

The script performs an offline restore using already-cached SDK/packages, Release build, Core,
Windows, CLI, corpus, interactive, security, provider-failure, user-story, and full-solution tests,
then publishes and smoke-tests the self-contained executable.

## Current limitations

- Phase one is Windows 11 x64 only and hosts Windows PowerShell 5.1; PowerShell 7 and non-Windows
  shells are not selected automatically.
- Translation is English-to-Simplified-Chinese only.
- Only the configurable chat-completion-compatible HTTP adapter is implemented.
- Eligibility and secret detection are conservative heuristics. Some low-value text may be skipped,
  and suspicious but benign text may produce a privacy skip.
- Translation display requires the companion pane. Disconnected-period translations are not stored
  or replayed.
- The current Phase-1 implementation has no translation history, terminal-content persistence,
  telemetry, GUI, cloud synchronization, or background service. Commands such as `tt last` are not
  implemented; this describes current capability rather than a permanent architecture prohibition.
- Interactive fidelity is designed for Windows Terminal/ConPTY behavior; other terminal hosts are
  outside the current support boundary.

See [the validation guide](specs/001-translate-terminal-output/quickstart.md) for the full manual
acceptance matrix.
