# Phase 0 Research: Windows Terminal Output Translation

> **Historical scope note (2026-08-23):** These decisions and rejected alternatives record the
> architecture selected for Feature 001's Phase-1 hosted live-translation mode under the governance
> then in force. They remain the truthful rationale for that feature, but they do not prohibit a
> separately specified future feature from reconsidering transcripts, files, local capture,
> background components, or other mechanisms under Constitution 2.0.0.

## Runtime and Language

**Decision**: Build with C# 14 on .NET 10 LTS and require the latest serviced .NET 10 patch for
development and releases.

**Rationale**: .NET 10 is the current active LTS release through November 2028 and provides the
Windows interop, asynchronous I/O, HTTP, JSON, channels, process, and pipe APIs needed by this
Windows-only CLI. A single managed stack keeps the learning project coherent.

**Alternatives considered**:

- .NET 8: rejected because its support ends in November 2026.
- Rust or Go: viable, but would add more third-party packages or less direct Windows interop.
- PowerShell scripts: rejected because core parsing and session orchestration would be harder to
  isolate and test.

**Sources**: [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core),
[C# 14 overview](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-14).

## CLI and Dependency Budget

**Decision**: Use stable System.CommandLine 2.0.x for public and internal subcommands. Use explicit
constructor wiring in the composition root. Production dependencies are the .NET shared framework
plus System.CommandLine only.

**Rationale**: The command surface includes session start, control, status, and internal host and
companion roles. Stable parsing, validation, help, and parser-only tests justify one Microsoft-owned,
trim-friendly dependency. A Generic Host or dependency injection container is unnecessary.

**Alternatives considered**:

- Manual parsing: rejected because validation, arity, help, and internal commands would become
  home-grown parsing code.
- Generic Host and configuration packages: rejected as unnecessary phase-one infrastructure.

**Sources**: [System.CommandLine overview](https://learn.microsoft.com/en-us/dotnet/standard/commandline/),
[syntax](https://learn.microsoft.com/en-us/dotnet/standard/commandline/syntax).

## Windows Terminal Session Topology

**Decision**: The launcher creates a uniquely named Windows Terminal window containing exactly two
panes. The program pane runs an internal relay-host command; the companion pane runs an internal
display command. The two processes correlate through a random session ID and local IPC.

**Rationale**: Windows Terminal officially supports chained new-tab, split-pane, size, title, and
focus commands. It does not expose a documented pane-output capture contract, so it is used only for
layout. Session correlation, readiness, and lifecycle belong to the application.

**Alternatives considered**:

- Screen scraping or Windows Terminal automation: rejected as undocumented and brittle.
- Inline translations: rejected because they alter the interactive pane.
- Attaching to an existing pane: rejected by the clarified dedicated-session scope.

**Source**: [Windows Terminal command-line arguments](https://learn.microsoft.com/en-us/windows/terminal/command-line-arguments).

## Interactive Program Hosting

**Decision**: The program-pane relay creates and owns a Windows pseudoconsole, launches
Windows PowerShell inside it, forwards outer-console input to ConPTY, and writes ConPTY UTF-8/VT
output bytes immediately and unchanged to the program pane. Input and output are serviced
independently. Pane size changes are propagated to ResizePseudoConsole.

**Rationale**: ConPTY is the supported Windows mechanism for hosting interactive character-mode
applications. It preserves terminal-aware behavior for prompts and full-screen programs while
allowing a non-blocking copy of output for translation. Independent input and output service avoids
pipe deadlocks.

**Alternatives considered**:

- stdout/stderr redirection or Tee-Object: rejected because they change terminal detection, stream
  ordering, prompts, and TUI behavior.
- PowerShell transcripts or hooks: rejected because native interactive programs are not captured
  faithfully.
- Full custom terminal emulator: rejected as unnecessary scope; Windows Terminal remains the
  renderer.

**Sources**: [Pseudoconsoles](https://learn.microsoft.com/en-us/windows/console/pseudoconsoles),
[creating a pseudoconsole session](https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session),
[CreatePseudoConsole](https://learn.microsoft.com/en-us/windows/console/createpseudoconsole),
[ResizePseudoConsole](https://learn.microsoft.com/en-us/windows/console/resizepseudoconsole).

## Native Interop

**Decision**: Isolate Kernel32 ConPTY and console-mode calls in the Windows module, use
source-generated LibraryImport declarations, SafeHandle ownership, and explicit mode restoration.

**Rationale**: Source-generated interop is built into current .NET, exposes marshalling for review,
and keeps native lifetime management behind a narrow interface.

**Alternatives considered**:

- A third-party ConPTY wrapper: rejected to keep dependencies minimal and retain lifecycle control.
- DllImport throughout the application: rejected because it spreads platform details and ownership.

**Sources**: [P/Invoke](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/pinvoke),
[P/Invoke source generation](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/pinvoke-source-generation).

## Local Session IPC

**Decision**: Use two random per-session named pipes: a host-to-companion event pipe and a duplex
control pipe. Require a versioned handshake, bounded JSON Lines messages, current-user-only access,
and explicit rejection of remote clients.

**Rationale**: Named pipes are built into .NET and support local duplex IPC. Separate event and
control channels simplify ownership and prevent display backpressure from blocking control. The
companion retries until the host handshake is available because Windows Terminal pane commands may
start concurrently.

**Alternatives considered**:

- Loopback TCP: rejected because it expands the network and firewall surface.
- Files: rejected because terminal content must not persist.
- One shared pipe: rejected because slow display could complicate control responsiveness.

**Sources**: [.NET named pipes](https://learn.microsoft.com/en-us/dotnet/standard/io/how-to-use-named-pipes-for-network-interprocess-communication),
[named-pipe security](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights).

## VT Extraction and Segmentation

**Decision**: Keep raw rendering separate from an incremental UTF-8 and VT extractor. The extractor
collects printable graphemes, handles CR/LF/TAB/backspace, discards styling and control-string
payloads, and treats erase or cursor changes as candidate invalidation signals. Emit candidates on
logical line or blank-line boundaries and after a 120 ms idle interval for prompt-shaped text.
Coalesce adjacent indented lines. Skip malformed control strings over 4 KiB and candidates over
8 KiB rather than truncating them.

**Rationale**: VT sequences and UTF-8 characters can be split across arbitrary reads. Regex stripping
cannot safely handle split or control-string input, while complete xterm screen emulation would be
too large for phase one. The chosen subset covers common errors and interactive prompts without
claiming pixel-perfect reconstruction.

**Alternatives considered**:

- Regex ANSI stripping: rejected because it fails on split sequences and OSC/DCS payloads.
- Newline-only segmentation: rejected because prompts often wait for input without a newline.
- Full terminal screen model: deferred until evidence shows the bounded extractor is insufficient.

**Sources**: [Windows VT sequences](https://learn.microsoft.com/en-us/windows/console/console-virtual-terminal-sequences),
[Unicode text segmentation](https://unicode.org/reports/tr29/).

## Non-Blocking Overload Policy

**Decision**: Use a non-blocking, bounded two-lane queue: 16 high-priority prompt/error candidates
and 48 normal candidates, with a 256 KiB aggregate text ceiling. Replace matching CR-redraw
candidates, allow high-priority items to evict normal items, expire work not started within
1.5 seconds, and aggregate drop notices to at most one every 5 seconds. The ConPTY drain path never
waits for this queue.

**Rationale**: These fixed bounds make memory and degradation testable while prioritizing recent
interactive prompts. Translation overload cannot backpressure the original terminal.

**Alternatives considered**:

- Unbounded queue: rejected because it permits memory growth and stale translations.
- Blocking bounded queue: rejected because it can delay original output.
- Undifferentiated drop policy: rejected because progress noise could displace confirmations.

**Source**: [.NET channels](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels).

## Secret Detection and Transient Data

**Decision**: Scan complete candidates locally before enqueue and again before provider
serialization. Detect credential-context assignments, authorization values, JWT-shaped values,
private-key blocks, credential-bearing URIs, and high-confidence token formats. A match or detector
failure skips the entire candidate and emits only a generic reason code. Terminal content remains
session-owned memory and is never written to files, telemetry, exception text, or diagnostic logs.
Disable and teardown cancel work, increment the session generation, clear queues, and suppress late
results.

**Rationale**: False positives lose only a translation; false negatives can disclose credentials.
Whole-segment skipping is safer than partial redaction and directly implements the clarified
privacy policy.

**Alternatives considered**:

- Partial redaction: rejected because missed spans can leak context or credentials.
- User bypass: rejected because phase one forbids secret transmission.
- Disk cache or history: rejected by the specification and constitution.

**Sources**: [GitHub secret patterns](https://docs.github.com/en/code-security/reference/secret-security/supported-secret-scanning-patterns),
[OWASP logging exclusions](https://cheatsheetseries.owasp.org/cheatsheets/Logging_Cheat_Sheet.html#data-to-exclude),
[.NET cancellation](https://learn.microsoft.com/en-us/dotnet/standard/threading/cancellation-in-managed-threads).

## Translation Provider Boundary

**Decision**: Core owns an ITranslationProvider interface with provider-neutral request, result, and
error types. Phase one supplies one configurable HTTP adapter using a documented chat-completion
JSON contract. Core owns consent, secret gates, classification, ordering, timeout, and cancellation;
the adapter owns configuration validation, authorization, serialization, response validation, and
error mapping. Use a long-lived HttpClient with finite timeout and no automatic retry.

**Rationale**: This permits adapter replacement and deterministic fakes without leaking provider
types into core logic. Avoiding automatic retry minimizes duplicate transmission, cost, and latency.

**Alternatives considered**:

- Vendor SDK in core: rejected because it couples behavior to one provider.
- Fully user-templated arbitrary HTTP: rejected because it increases configuration and security risk.
- Plugin system: rejected as unnecessary phase-one scope.

**Sources**: [HttpClient guidelines](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines),
[System.Text.Json](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/overview).

## Packaging and Testing

**Decision**: Develop and test as framework-dependent net10.0 projects, then publish a self-contained
single-file win-x64 executable without trimming or Native AOT. Use MSTest.Sdk on
Microsoft.Testing.Platform. Unit tests use plain fakes; provider contracts use a fake
HttpMessageHandler; Windows integration tests exercise ConPTY, panes, IPC, Ctrl+C, resize, Unicode,
and exit-code fidelity. Live-provider tests remain opt-in.

**Rationale**: Self-contained distribution gives a simple user install, while avoiding trimming and
AOT compatibility work. The test split keeps core logic deterministic and network-free.

**Alternatives considered**:

- Framework-dependent distribution: smaller but requires users to install the runtime.
- Native AOT or trimming: deferred until startup or package size is measured as a problem.
- Live provider in normal tests: rejected because it is nondeterministic and transmits content.

**Sources**: [.NET single-file deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview),
[MSTest introduction](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-mstest-intro),
[unit testing best practices](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-best-practices).
