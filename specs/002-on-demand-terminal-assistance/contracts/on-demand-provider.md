# On-Demand Provider Behavioral Contract

This is an internal provider-neutral behavior contract, not an HTTP endpoint or wire schema.

## Request kinds

### LastTranslation

Permitted context:

- strict previous command text as explanatory context;
- selected ordered previous-command output;
- known exit/termination/interruption facts; and
- completeness/disclosure instructions.

Required behavior:

- Translate English natural language in output to Simplified Chinese.
- Do not translate the command text.
- Preserve existing Chinese and technical tokens (paths, URLs, shell commands/options, error codes,
  identifiers, code, package/module names) as far as practical.
- If input is HEAD + TAIL, produce a summarized translation without claiming complete line coverage.
- Return a translation body and one short 1–2 line recommendation.

### QuestionOnly

Permitted context: only the user's explicit question and response-language instruction. Capture,
previous commands, and conversation history are forbidden.

Required behavior: answer once, in Simplified Chinese unless the question explicitly requests another
language.

### QuestionWithPreviousCommand

Permitted context: explicit question plus the same trustworthy previous-command fields permitted for
LastTranslation.

Required behavior: answer the question using the context; do not force a translation-shaped answer.
Use Simplified Chinese unless the question explicitly requests another language.

## Input selection and budget

- V1 variable-input policy defaults to 8,192 UTF-8 bytes and uses any smaller adapter capability.
- Command, question, selected output, and termination fields count toward the variable budget.
- Fixed instructions and response allowance must also fit adapter/provider capacity.
- Output exceeding the remaining budget uses UTF-8-safe HEAD + TAIL and marks AI input truncation.
- Remaining output bytes are divided equally, with an odd byte assigned to TAIL. Cuts move inward to
  valid rune boundaries and may prefer an earlier line boundary without exceeding either share; when
  none exists, the rune-safe hard cut is used.
- Provider input selection never removes or relabels prior local capture truncation.
- If required command/question/metadata cannot fit reliably, no provider request is made.

## Authorization sequence

```text
construct exact selected request
  → verify matching provider/content-scope consent fingerprint
  → run SecretDetector/privacy gate over every outbound field
  → adapter asserts authorized/gate-passed state
  → serialize and transmit
```

- Capture enablement is not provider consent.
- `tt ask` uses a question-only consent scope. `tt last` and `tt ask last` share one disclosed
  previous-command terminal-context scope.
- A trust-boundary-affecting configuration change invalidates prior consent.
- A secret match sends none of the request, not a filtered remainder.
- The provider cannot receive content before the gate.

## Response and error safety

- Enforce timeout, response-size, status, redirect/cookie, credential, and parse safeguards consistent
  with the existing production provider adapter.
- Normalize provider failures without including question/capture content in logs or diagnostics.
- Treat response text as untrusted inert content.
- Never dispatch a returned command/snippet to PowerShell or filesystem APIs.
- Do not retain responses as conversation history.

## Feature 001 compatibility

If a safe HTTP transport leaf is shared, the existing live translation provider must retain its
current translation-specific prompt, source-size bound, error mapping, and test behavior. The new
request kinds do not flow through Feature 001's live queue/coordinator or companion renderer.
