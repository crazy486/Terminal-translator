# PowerShell Integration Contract

## Supported host

V1 contract: Windows Terminal + Windows PowerShell 5.1 Desktop with the TT CurrentUser/CurrentHost
profile integration loaded. Other shells, PowerShell versions, terminals, full-screen applications,
and `-NoProfile` sessions are not promised.

## Installation and ownership

- Enabling capture installs one TT-owned versioned loader under LocalAppData and one uniquely marked
  dot-source block in the supported profile.
- Installation is idempotent and does not replace unrelated profile content.
- Disabling/uninstall removes only TT-owned artifacts and preserves user changes around the block.
- Capture purpose is disclosed before enablement; the durable choice prevents per-shell prompting.

## Prompt composition

- The integration saves and invokes the effective user prompt function.
- TT maintenance executes in a failure-catching wrapper; prompt presentation remains the user's.
- Command boundaries do not depend on matching `PS ...>` or any prompt text.
- Opening/closing boundary facts are sidecar-only and Start/Stop result text is suppressed; no TT
  sentinel or maintenance status appears as ordinary user/command output.
- Multiline/continuation input remains a single PowerShell history command.
- A Feature 001 hosted `tt start` shell does not initialize Feature 002 capture.

## Session identity

- Each integrated PowerShell process creates an independent random session identity and nonce.
- The manifest binds identity to current-user SID, PowerShell PID, process-start identity, integration
  version, and command sequence.
- Child `tt.exe` receives the opaque identity/nonce through dedicated environment variables and must
  validate its invoking process association.
- No operation selects capture by modification time or crosses session identity.

## Boundary lifecycle

At prompt completion before the user's next command:

1. Preserve completion/history/interruption facts before TT maintenance changes automatic variables.
2. Close and flush the current transcript interval with session/sequence boundary metadata.
3. Validate the candidate; invalid/ambiguous data is not published.
4. Finalize the completed record and enforce retained capacity.
5. Start the next transcript interval.
6. Invoke/return the saved prompt presentation.

The `tt last` or `tt ask last` command currently running is not the finalized target it retrieves.
Finalized contextual TT command records are ineligible as future targets, preventing self-pollution.

## Retained capacity

- The exact 10,180,000-byte limit applies to logical bytes of completed-command content and capture
  metadata/index referenced by the single committed manifest generation, including that manifest.
  Unpublished candidates, old manifests, transaction files, unreferenced cleanup residue, and
  filesystem allocation slack are not eligible retained records.
- The active native Transcript staging file may temporarily exceed the limit while its command runs.
- At the next reliable command/prompt boundary, the retained generation must be restored/published at
  no more than the exact limit.
- Normal pressure evicts oldest complete records and prefers the newest complete record.
- One individually oversized command becomes a marked, boundary-safe HEAD + TAIL retained record.
- A partial or failed pre-commit publication is never visible. After atomic manifest replacement,
  the new generation alone is authoritative; failure deleting unreferenced older files is a
  post-commit cleanup failure, not rollback. Failure deleting any raw staging, unpublished candidate,
  transaction file, or superseded generation creates at most one protected, ineligible finalization
  residue set for the current transaction. It may include one oversized raw staging file. Capture
  stops and cannot open new staging until the entire set is cleaned, preventing accumulation.

## Failure and health

- Every capture/transcript/retention/cleanup exception is contained by the integration.
- It cannot prevent command execution, change the command's exit code, block normal stdin/stdout, or
  exit the shell.
- The first outage transition prints one nearby unavailable notice; subsequent failed prompts do not
  repeat it. A validated recovery may print one restoration notice.
- Health notices are emitted while no command transcript is active and are integration output, not
  command output.
- While unhealthy or after failed latest-command finalization, contextual retrieval fails closed; it
  does not silently return an older command.

## Clear, interruption, and async behavior

- `Clear-Host` clears presentation only. It is also an ordinary completed no-output command under
  strict previous-command semantics.
- Ctrl+C output is eligible only when the stopped transcript and termination boundary are reliable;
  otherwise contextual retrieval fails closed.
- Output appearing after prompt return is outside the prior closed command and is never reassigned
  backward. V1 performs no background provenance tracking.

## Storage and cleanup

- Staging and retained session content resides only below the TT LocalAppData capture root with a
  protected current-user DACL.
- It is excluded from normal logs, telemetry, sync/upload, diagnostics/support bundles, and
  user-facing show/export workflows.
- Normal session termination deletes the exact session directory.
- Stale data after crash/forced termination is ineligible and deleted at the next TT initialization.
- A cleanup-only process watcher may wait for the bound PowerShell process and delete its directory;
  there is at most one per session. It uses bounded idle resources, has no network/provider access,
  must never read/record terminal content or act as a capture source, exits after owner cleanup, and
  is not automatically restarted. Its failure leaves stale residue for later capture-related cleanup.
- Question-only `tt ask` may trigger a content-free owner-manifest stale sweep, but never opens
  command records, resolves current-session capture, or attaches Capture to its request.
