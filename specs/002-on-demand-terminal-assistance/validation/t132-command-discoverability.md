# T132 Command Discoverability and Product Installation Validation

**Date**: 2026-08-30
**Platform**: Windows 11 x64, Windows PowerShell 5.1 Desktop
**Result**: PASS

## Previous architecture and root cause

Before T132, `tt configure --capture enabled` wrote the embedded
`TerminalTranslator.Profile.ps1` template to
`%LOCALAPPDATA%\TerminalTranslator\PowerShell` and inserted one marked dot-source block in the
CurrentUser/CurrentHost Windows PowerShell profile. The installer replaced
`__TT_EXECUTABLE_PATH__` with `Environment.ProcessPath`, which in manual acceptance was the unique
development artifact path.

The loader used that absolute path only for hidden `__capture` bridge, cleanup, and watcher calls.
It did not install a product binary, mutate PATH, create a shim, alias, or function. Consequently,
profile capture initialized successfully while PowerShell command resolution had no `tt` command.
Feature 001 registered `start`, `on`, `off`, `status`, and `configure` as CLI root commands and its
documentation assumed the executable directory had already been put on PATH; installation never
fulfilled that assumption. Development artifact path and installed integration target were therefore
mixed.

The pre-T132 LocalAppData root held the managed PowerShell loader, capture preference, provider
settings, provider consent grants, and live-session Capture directories when applicable. It held no
stable product executable.

## Selected architecture

T132 uses a PowerShell-native public alias plus a product-owned, content-addressed executable:

```text
%LOCALAPPDATA%\TerminalTranslator\
  PowerShell\TerminalTranslator.Profile.ps1
  Versions\<published-executable-sha256>\tt.exe
```

The published self-contained executable is copied through a same-directory temporary and validated
against the directory SHA256 before immutable publication. The loader binds both its hidden capture
bridge and the public `Alias tt` to the same `$script:TtExecutablePath`. The alias delegates directly
to the native executable, preserving arguments, stdout/stderr, redirection, `$LASTEXITCODE`, and `$?`
without the function-return state change observed during TDD.

No User or Machine PATH mutation is required. A new ordinary profile-enabled Windows PowerShell 5.1
session resolves `tt` immediately. Content addressing permits an upgrade to publish a new binary
while an old watcher/process still has the prior executable open. Already-loaded shells keep one
internally consistent old target; new shells load one internally consistent new target.

## Collision, idempotency, upgrade, and removal

- The loader calls `Get-Command -Name tt` before exposing the alias.
- If no command exists, it creates the TT-owned global alias.
- If its ownership marker and alias already exist, reloading updates the alias idempotently.
- If a non-TT command exists, it is preserved and one content-free warning says Terminal Translator
  did not replace it. There is no silent override.
- Reinstalling identical bytes reuses the same validated version and leaves one profile block.
- Installing changed bytes creates a new hash directory and replaces one loader; loader bridge and
  public command cannot point at different versions.
- Disable/removal removes the marked profile block and loader, preserves unrelated profile/product
  files, and best-effort removes TT-owned version directories. A currently executing/locked binary
  may remain as inert product residue, but no fresh shell exposes it after the profile block is gone.

## Automated evidence

Full Release gate:

```text
dotnet test TerminalTranslator.sln --configuration Release --no-restore -p:UseAppHost=false
523/523 PASS
```

The pre-T132 automated baseline was 519; T132 added four tests. New/focused coverage proves:

- fresh profile command resolution;
- product-owned LocalAppData layout;
- argument forwarding with spaces and Unicode input;
- stdout/stderr separation;
- success and failure `$LASTEXITCODE` plus native `$?` state;
- redirected output and absence of spinner escape output;
- existing-command collision preservation/warning;
- upgrade target replacement, repeated-install idempotency, and cleanup;
- contextual invocation marker behavior for the final literal `tt last` form.

Additional focused results:

```text
PowerShell command + contextual marker/finalization: 12/12 PASS
Redirected spinner regressions:                     9/9 PASS
Contextual strict-target selection:                  2/2 PASS
```

This covers repeated contextual success and provider failure/timeout/no-English/privacy outcomes;
the marker is created from CLI arguments before provider execution, so result category cannot change
self-exclusion. Provider reliability and retry behavior were not changed.

The complete 523 gate passed the existing T127-T131 native producer, capture, exit-attribution,
spinner, privacy, and provider-contract suites. During two later invocations of the publish script's
parallel Windows subset, the existing long PS5.1 native outcome stress case timed out at different
random fact indices; it passed in the complete gate and in an isolated rerun. No capture timing or
producer code was changed for T132. The artifact was therefore produced with the script's supported
`-SkipTests` switch after the complete gate had passed.

## Acceptance artifact and consistency proof

```text
Artifact:
D:\Projects\Terminal Translator\artifacts\publish\t132-command-discoverability-acceptance\tt.exe

SHA256:
7437900A6DFE4DF6EC9E56736127389FC93349D7A5D5B7BF2E437883C75B8BE4

Loader target / installed product:
C:\Users\gutia\AppData\Local\TerminalTranslator\Versions\7437900a6dfe4df6ec9e56736127389fc93349d7a5d5b7bf2e437883c75b8be4\tt.exe

Installed SHA256:
7437900A6DFE4DF6EC9E56736127389FC93349D7A5D5B7BF2E437883C75B8BE4

Fresh-shell resolution:
NAME=tt
TYPE=Alias
TARGET=C:\Users\gutia\AppData\Local\TerminalTranslator\Versions\7437900a6dfe4df6ec9e56736127389fc93349d7a5d5b7bf2e437883c75b8be4\tt.exe
tt status exit=5 (canonical no-live-session result)
```

The publish script rejects an artifact-bound loader, requires a LocalAppData Versions target, compares
published/installed SHA256, starts a fresh profile-enabled Windows PowerShell, verifies the managed
alias definition and command target, and verifies `tt status` exit propagation.

## Fresh Windows PowerShell 5.1 manual acceptance

Close all current PowerShell windows, open a normal Windows PowerShell 5.1 window, and do not set
`$tt` or dot-source a loader:

```powershell
Get-Command tt | Format-List Name,CommandType,Definition
$script:TtExecutablePath

tt status
$LASTEXITCODE

tt configure --help
$LASTEXITCODE

tt start --help
$LASTEXITCODE

Write-Output "The package installation completed successfully."
tt last
$LASTEXITCODE

tt last
$LASTEXITCODE

tt last > result.txt
$LASTEXITCODE
Get-Content -Raw .\result.txt
```

Expected: `Get-Command` reports the managed Alias and installed LocalAppData target; every command is
resolved without a variable/full path/PATH edit; `status` reports no live Feature 001 session with
exit 5 when appropriate; help commands exit 0; contextual invocations retain the same real previous
command; redirected output contains no interactive spinner escape output.
