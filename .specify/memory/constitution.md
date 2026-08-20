<!--
Sync Impact Report
- Version change: unratified template -> 1.0.0
- Modified principles:
  - Placeholder Principle 1 -> I. CLI-First
  - Placeholder Principle 2 -> II. Privacy-First
  - Placeholder Principle 3 -> III. Minimal Scope
  - Placeholder Principle 4 -> IV. Provider-Agnostic Design
  - Placeholder Principle 5 -> V. Safe Execution
- Added principles:
  - VI. Maintainability
  - VII. Testability
- Added sections:
  - Product Constraints
  - Development and Review Gates
- Removed sections: None
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

### II. Privacy-First
Terminal content MUST remain local unless the user takes an explicit action that clearly authorizes
its upload. Before transmission, the application MUST make the destination and content scope clear.
Configuration defaults MUST minimize disclosure, and secrets or unrelated terminal history MUST NOT
be included in provider requests.

Rationale: terminal sessions routinely contain private code, credentials, paths, and operational
data, so consent and data minimization are non-negotiable.

### III. Minimal Scope
The project MUST exclude web UI, databases, user accounts, cloud synchronization, background
services, and similar infrastructure unless a validated core requirement makes them necessary.
Every such addition MUST include a written justification showing why a simpler local, CLI-based
solution is insufficient.

Rationale: a narrow product surface reduces privacy risk, operational burden, and maintenance cost.

### IV. Provider-Agnostic Design
Translation and AI behavior MUST be expressed through provider-neutral interfaces. Provider SDKs,
authentication, request formats, and response parsing MUST remain inside replaceable adapter
modules. Core parsing, configuration, and shell integration logic MUST NOT depend on a specific
model provider.

Rationale: provider isolation permits substitution, testing, and evolution without rewriting core
product behavior.

### V. Safe Execution
The application MUST NOT automatically execute generated, translated, or modified commands. It MUST
present the exact command to the user and obtain explicit confirmation before execution. Preview,
copy, or cancel paths MUST remain available, and confirmation MUST apply to the specific command
shown rather than to an open-ended future action.

Rationale: generated commands may be incorrect or destructive; execution authority remains with the
user.

### VI. Maintainability
The architecture MUST use clear module boundaries for translation, provider adapters, parsing,
configuration, and shell integration. Implementations MUST prefer straightforward control flow and
the standard library or existing dependencies. New dependencies and abstractions MUST have a
documented, current need and MUST NOT be introduced solely for hypothetical future expansion.

Rationale: simple, cohesive modules make security review and long-term change safer and faster.

### VII. Testability
Core translation orchestration, parsing, configuration, and shell integration MUST be independently
testable without network access, a live provider, or an interactive shell. External providers and
process execution MUST be replaceable with test doubles. Behavioral changes to these areas MUST
include automated tests covering success, failure, and safety-relevant paths.

Rationale: isolation and deterministic tests provide evidence that privacy and execution safeguards
continue to hold as the product changes.

## Product Constraints

- Local and terminal-native solutions MUST be the default design choice.
- Network transmission MUST occur only within a user-initiated operation and under the privacy rules
  in Principle II.
- Generated commands MUST be treated as untrusted text until the confirmation required by Principle
  V is received.
- Optional provider integrations MUST preserve a stable provider-neutral core contract.
- Persistent storage, telemetry, background processing, or graphical interfaces require an explicit
  specification and constitution compliance review before implementation.

## Development and Review Gates

- Each specification and implementation plan MUST identify its CLI behavior, data-flow boundaries,
  provider boundary, and command-execution implications.
- Reviews MUST reject changes that upload content implicitly, bypass per-command confirmation, or
  couple core logic directly to a provider.
- Tests for core modules MUST run deterministically with network and process boundaries substituted.
- New dependencies or infrastructure MUST include a scope and maintenance justification.
- Exceptions to any MUST rule require a constitution amendment before the conflicting change is
  merged.

## Governance

This constitution governs all project specifications, plans, tasks, reviews, and releases. Where
other project guidance conflicts with it, this constitution takes precedence.

Amendments MUST be proposed as an explicit documentation change that states the reason, affected
principles, compatibility impact, and any migration work. Adoption requires review and approval by
the project maintainers before dependent implementation is merged.

Constitution versions follow semantic versioning: MAJOR for removal or incompatible redefinition of
a principle or governance rule; MINOR for a new principle, section, or materially expanded mandate;
and PATCH for non-semantic clarification or correction. The last-amended date MUST change whenever
the version changes; the ratification date MUST remain the original adoption date.

Every feature specification, implementation plan, and code review MUST include a constitution
compliance check. Reviewers MUST document any justified complexity and MUST block unresolved
violations. Releases SHOULD include a final compliance review when they combine changes whose
interaction could affect privacy, provider isolation, or execution safety.

**Version**: 1.0.0 | **Ratified**: 2026-08-21 | **Last Amended**: 2026-08-21
