<!--
Sync Impact Report
- Version change: 1.0.0 -> 2.0.0
- Modified principles:
  - II. Privacy-First -> II. Authorized External Disclosure
  - III. Minimal Scope -> III. Purpose-Limited Local Data
  - IV. Provider-Agnostic Design -> V. Provider-Agnostic Design
  - V. Safe Execution -> VI. Safe Execution
  - VI. Maintainability -> VII. Maintainability
  - VII. Testability -> VIII. Testability
- Added principles:
  - IV. Justified Architecture
- Added sections:
  - Amendment Record
- Removed sections: None
- Compatibility impact:
  - Backward-incompatible governance change: local persistence and background or resident
    components are no longer presumptively excluded mechanisms. They require explicit,
    outcome-based review instead.
  - Existing Phase-1 hosted live-translation requirements and implementation remain unchanged.
- Follow-up TODOs: None
-->
# Terminal Translator Constitution

## Core Principles

### I. CLI-First
The product MUST provide its primary user experience inside the terminal. Core translation,
configuration, inspection, and command-handling workflows MUST be usable without a web or desktop
interface. Any non-terminal interface requires a documented use case that cannot be served
reasonably by the CLI and MUST remain secondary to it.

Rationale: terminal-native operation is the defining product boundary and keeps the tool available
where its users already work.

### II. Authorized External Disclosure
Terminal-derived content MUST NOT be transmitted to an external provider or other third party
unless the user has triggered the relevant feature and its applicable authorization policy has
been satisfied. Before transmission, the product MUST make the destination and content scope clear.
Every externally transmitted item MUST pass the feature's documented privacy gate and MUST be
limited to the content required for that operation. Local capture or local retention MUST NOT be
treated as consent for telemetry, synchronization, or provider upload.

Rationale: external disclosure crosses a trust boundary and is materially different from processing
or retaining data on the user's own device.

### III. Purpose-Limited Local Data
A feature MAY capture or persist terminal-derived data locally when an approved specification and
architecture decision establish a necessary purpose. The design MUST document what is retained,
why it is needed, where it is stored, who can access it, capacity bounds, lifetime, cleanup
conditions, behavior after crashes, user controls, backup or synchronization exposure, and whether
any automatic upload can occur. Retention MUST be no broader or longer than the feature purpose,
and the product MUST NOT keep an unbounded or indefinite full terminal history by default.

Sensitive local data requires an explicit assessment of access controls, secret exposure, crash
recovery, diagnostic leakage, and cleanup. Local-only storage does not remove these duties, but the
possibility of sensitive content does not by itself prohibit a bounded local capture design.

Rationale: privacy is protected by explicit purpose and verifiable lifecycle boundaries, not by
assuming that every local storage mechanism is equivalent to external disclosure.

### IV. Justified Architecture
Architecture investigation MUST begin from the user goal and consider technically credible
candidates before selecting a mechanism. Reliability, user experience, compatibility, complexity,
performance, privacy, security, and maintenance MUST be compared for shortlisted candidates.
Privacy and security MAY reject a candidate immediately when it violates a non-negotiable boundary;
otherwise they MUST be evaluated with the same concrete evidence as the other dimensions.

Background or resident components MAY be used when required by an accepted architecture. Their
startup behavior, lifecycle, resource cost, failure isolation, privacy impact, maintenance burden,
and cleanup behavior MUST be justified. A phase-specific decision not to use a mechanism MUST NOT
be interpreted as a permanent product prohibition.

Rationale: governance should constrain outcomes and trust boundaries while leaving room to discover
the most reliable architecture.

### V. Provider-Agnostic Design
Translation and AI behavior MUST be expressed through provider-neutral interfaces. Provider SDKs,
authentication, request formats, and response parsing MUST remain inside replaceable adapter
modules. Core parsing, configuration, and shell integration logic MUST NOT depend on a specific
model provider. Feature specifications MAY select an external, local, offline, or other translation
engine when their requirements and architecture assessment justify it.

Rationale: provider isolation permits substitution, testing, and evolution without rewriting core
product behavior.

### VI. Safe Execution
The application MUST NOT automatically execute generated, translated, or modified commands. It MUST
present the exact command to the user and obtain explicit confirmation before execution. Preview,
copy, or cancel paths MUST remain available, and confirmation MUST apply to the specific command
shown rather than to an open-ended future action.

Rationale: generated commands may be incorrect or destructive; execution authority remains with the
user.

### VII. Maintainability
The architecture MUST use clear module boundaries for translation, provider adapters, parsing,
configuration, and shell integration. Implementations MUST prefer straightforward control flow and
the standard library or existing dependencies. New dependencies and abstractions MUST have a
documented, current need and MUST NOT be introduced solely for hypothetical future expansion.

Rationale: simple, cohesive modules make security review and long-term change safer and faster.

### VIII. Testability
Core translation orchestration, parsing, configuration, and shell integration MUST be independently
testable without network access, a live provider, or an interactive shell. External providers and
process execution MUST be replaceable with test doubles. Behavioral changes to these areas MUST
include automated tests covering success, failure, and safety-relevant paths.

Rationale: isolation and deterministic tests provide evidence that privacy and execution safeguards
continue to hold as the product changes.

## Product Constraints

- Network transmission of terminal-derived content MUST satisfy Principle II. A continuing or
  background feature requires a current authorization model defined by its specification; a one-time
  local capture decision is not sufficient authority to transmit.
- Local retention MUST satisfy Principle III. Full terminal history MUST NOT be retained without
  explicit product need, defined bounds, user-visible behavior, and a cleanup policy.
- Terminal-derived data MUST NOT silently become telemetry, analytics, cloud synchronization,
  training data, or diagnostic content. Each additional use and trust boundary requires explicit
  specification and review.
- Credentials MUST NOT be hard-coded in the repository or emitted through user-visible diagnostics.
  Credential storage and access MUST be separated from terminal-content retention decisions.
- Generated commands MUST be treated as untrusted text until the confirmation required by Principle
  VI is received.
- Optional provider integrations MUST preserve a stable provider-neutral core contract.

## Development and Review Gates

- Each feature specification MUST define user-visible behavior and separately state the semantics
  of local capture, local persistence, and external transmission when they apply.
- Research MUST enumerate credible candidates and record feasibility evidence before an architecture
  is selected. Shortlisted candidates MUST receive concrete privacy and security assessment.
- The selected architecture MUST be recorded in research or an ADR with trade-offs and rejected
  alternatives. The implementation plan MUST describe how that decision satisfies the feature
  specification and this constitution.
- Mechanisms such as ConPTY, transcripts, files, bounded buffers, resident processes, pane layouts,
  provider types, queue capacities, and timeout values belong in feature specifications only when
  user-observable, or otherwise in research, ADRs, plans, and tasks. They MUST NOT become
  constitution rules merely because one feature used or rejected them.
- Reviews MUST reject implicit external disclosure, unbounded or undocumented retention, local
  capture repurposed as telemetry, hard-coded credentials, bypassed command confirmation, and
  unresolved security trade-offs.
- Tests for core modules MUST run deterministically with network and process boundaries substituted.
  New dependencies or infrastructure MUST include scope and maintenance justification.
- Historical feature documents MUST remain truthful about the decisions and acceptance conditions
  in force for that feature. A future feature MAY choose differently only through its own explicit
  specification and architecture review.
- Exceptions to any MUST rule require a constitution amendment before the conflicting change is
  merged.

## Amendment Record

### 2.0.0 - Outcome-Based Privacy and Architecture Governance

- Modified Principles II and III because their prior wording could be read as treating a Phase-1
  memory-only, non-resident architecture as a permanent product boundary before alternatives were
  investigated.
- Added explicit separation among local capture, local persistence, and external transmission.
- Replaced mechanism-oriented exclusion with bounded-retention requirements and an evidence-based
  architecture review gate.
- Preserved the protected values: no unauthorized external disclosure, data minimization,
  purpose-limited retention, no covert telemetry, credential protection, and reviewable security
  trade-offs.
- Classified as a backward-incompatible governance change because future specifications may now
  justify mechanisms that the former Minimal Scope principle presumptively excluded.
- Does not change Feature 001 requirements or the shipped Phase-1 implementation: that mode remains
  ConPTY-hosted, two-pane, external-provider based, fail-closed for suspected secrets, and free of
  terminal-content persistence or resident capture components.

## Governance

This constitution governs all project specifications, plans, tasks, reviews, and releases. Where
other project guidance conflicts with it, this constitution takes precedence. Feature-specific
requirements govern only their stated feature and version scope; historical architecture decisions
do not silently become product-wide principles.

Amendments MUST be proposed as an explicit documentation change that states the reason, affected
principles, compatibility impact, migration work, and effect on existing features. Adoption requires
review and approval by the project maintainers before dependent implementation is merged.

Constitution versions follow semantic versioning: MAJOR for removal or incompatible redefinition of
a principle or governance rule; MINOR for a new principle, section, or materially expanded mandate;
and PATCH for non-semantic clarification or correction. The last-amended date MUST change whenever
the version changes; the ratification date MUST remain the original adoption date.

Every feature specification, architecture decision, implementation plan, and code review MUST
include a constitution compliance check appropriate to its stage. Reviewers MUST document justified
complexity and MUST block unresolved violations. Releases SHOULD include a final compliance review
when combined changes could affect privacy, provider isolation, retention, or execution safety.

**Version**: 2.0.0 | **Ratified**: 2026-08-21 | **Last Amended**: 2026-08-23
