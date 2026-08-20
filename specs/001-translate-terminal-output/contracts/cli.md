# CLI Contract

Executable name in documentation: `tt`.

## General Rules

- Human-readable output goes to stdout; errors go to stderr.
- Public commands never emit terminal source text, translations, or credential values in diagnostics.
- Unknown commands, missing values, invalid values, and conflicting options return usage errors.
- Internal commands are not shown in normal help and require a valid launcher-created session token.
- Translation starts disabled for every session.

## `tt configure`

Stores non-sensitive provider preferences.

```text
tt configure
  --endpoint <https-uri>
  --model <provider-model-id>
  --api-key-env <environment-variable-name>
  [--timeout-ms <100..10000>]
```

Validation:

- Endpoint must use HTTPS. Loopback HTTP is accepted only when `--allow-loopback-http` is supplied
  for local validation.
- Model and environment-variable name must be non-empty.
- The credential environment variable must exist when translation is enabled, but its value is
  never stored or printed.
- Settings contain adapter name, endpoint, model, key variable name, and timeout only.

Success output identifies the endpoint host, model, key variable name, and settings path. It never
prints the credential value.

## `tt start`

Creates the dedicated Windows Terminal session.

```text
tt start [--working-directory <path>]
```

Behavior:

1. Validate Windows Terminal, ConPTY support, Windows PowerShell, settings, and key-variable presence.
2. Create a cryptographically random session ID and same-user local pipe names.
3. Launch a new, uniquely named Windows Terminal window with one program pane and one companion pane.
4. Focus the program pane.
5. Return after Windows Terminal accepts the launch request; host lifecycle continues in the pane.

Starting does not grant consent or transmit terminal content.

## `tt on`

Enables translation within the current dedicated session.

```text
tt on
```

Preconditions:

- `TT_SESSION_ID` identifies a live same-user control pipe.
- Provider configuration and credential environment variable are valid.

Interactive confirmation displays:

- External endpoint host and model.
- Scope: eligible, non-sensitive English segments from this session only.
- Retention: no terminal source or translation history.
- Secret behavior: the entire suspected segment is skipped.

Only an explicit affirmative response grants consent. Empty, negative, EOF, or interrupted input
leaves translation disabled and sends nothing.

## `tt off`

Disables translation within the current session.

```text
tt off
```

Behavior:

- Atomically advances session generation.
- Stops acceptance of new candidates.
- Cancels provider requests and clears pending work.
- Suppresses late translations.
- Leaves Windows PowerShell and the companion pane running.

The command is idempotent.

## `tt status`

Reports content-free state.

```text
tt status
```

Output fields:

- session: active or unavailable
- translation: disabled, enabling, enabled, or degraded
- provider: endpoint host and model only
- queue: aggregate high and normal counts
- privacy-skipped and overload-dropped aggregate counts

No source text, translation text, full endpoint query, headers, or credential values are shown.

## Internal Commands

```text
tt __host --session <id> --working-directory <path>
tt __companion --session <id>
```

- The launcher supplies the unpredictable session nonce through the inherited
  `TT_SESSION_NONCE` environment variable rather than exposing it in command-line arguments.
- `__host` owns ConPTY, raw relay, analysis, provider calls, IPC, and session teardown.
- `__companion` performs the event-pipe handshake and renders translations and status.
- Direct invocation without the launcher-created nonce fails before creating pipes or a shell.

## Exit Codes

| Code | Meaning |
|------|---------|
| 0 | Command completed or the user declined consent without an operational error |
| 2 | Invalid command or option |
| 3 | Windows Terminal, ConPTY, or Windows PowerShell prerequisite missing |
| 4 | Provider configuration or credential environment variable invalid |
| 5 | No live translation session or IPC authentication failed |
| 6 | Session launch or terminal-host failure |
| 7 | Settings read/write failure |

The hosted Windows PowerShell exit code is propagated by the host process independently of control
command exit codes.
