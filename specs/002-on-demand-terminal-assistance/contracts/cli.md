# CLI Behavioral Contract

## Command grammar

```text
tt last
tt ask <question...>
tt ask last <question...>
tt configure --capture enabled
tt configure --capture disabled
```

- `question...` is required, preserves Unicode, and consists of the arguments remaining after shell
  and System.CommandLine quote processing.
- `tt ask --last ...` is invalid and is not an alias.
- `tt ask last ...` is the only capture-context form under `ask`.
- `tt ask last` without a following question is a usage error; quoted questions, multi-token text,
  and Unicode preserve their parsed content.
- Capture-only configure invocation requires no provider arguments. Existing provider-only configure
  remains valid with provider arguments all-or-none. Capture and provider-setting options cannot be
  combined in one invocation.
- Existing provider configuration forms and `tt start`, `tt on`, `tt off`, `tt status` retain their
  Feature 001 behavior.

## `tt last`

### Preconditions

- Invoked in a supported, currently integrated Windows PowerShell 5.1 session.
- Capture is enabled and session association is valid.
- A reliable strict previous completed command exists.
- Applicable provider trust consent and privacy gate permit the exact request.

### Success

Print inline in the invoking console:

```text
[翻译]
<Chinese translation or summarized translation>

[建议]
<one short recommendation, approximately 1–2 lines>
```

The command text may inform the model but is not itself translated. Existing Chinese and technical
tokens remain recognizable. The full original terminal output is not repeated.

### Deterministic non-provider outcomes

| Condition | Required outcome |
|---|---|
| No previous completed command | `No previous command output is available.` |
| Previous command has no captured output | Explain that the previous command has no translatable output; do not look backward. |
| Chinese-only output with no translatable English | `No translatable English content was found.`; no provider call. |
| Capture disabled | `tt last capture is disabled.` followed by enable-future guidance. |
| Capture unavailable/unhealthy | Clear capture-unavailable message; no provider call. |
| Boundary/session/capture unreliable | `[tt] Previous command output could not be recovered reliably.`; no guessed content. |
| Suspected secret | Explain that suspected sensitive content was not sent; send nothing. |

### Disclosure order

- If local retained content is `LocalHeadTail`, show a local-capture-truncation notice stating the
  middle is no longer locally retained.
- If provider input is `AiHeadTail`, show a separate notice stating the summarized result is based on
  only the beginning and end sent to the provider.
- When both apply, show both; neither state suppresses the other.

## `tt ask`

- Sends only the explicit question. It must not resolve session identity, open Capture, attach prior
  commands/output, or read/store conversation history.
- Each invocation is stateless.
- Response language is Simplified Chinese by default. An explicit other-language request in the
  question controls that response.
- A suspected-secret question is blocked before provider transmission.
- Suggested commands are inert displayed text.

## `tt ask last`

- Uses the exact same previous-command retrieval contract and failure states as `tt last`.
- Adds the explicit question to command text, retained output, and known exit/termination context.
- Answers the question; it is not forced into translation format.
- Is stateless and follows the same language rule as `tt ask`.
- Applies the same local/AI truncation, consent, privacy, and reliability disclosures as `tt last`.

## Inline safety

- No Feature 002 command opens a companion pane.
- No provider output is parsed as an instruction to execute.
- The CLI never runs a suggested command, changes a file, confirms an action, or invokes a repair.
- Failure output must not quote captured content or suspected secrets.
- Capture failures never terminate the parent shell or change the prior command's exit behavior.

## Process exit categories

Implementation may preserve existing numeric mappings, but tests must distinguish:

- success/informational safe no-send;
- command-line usage error;
- provider configuration/credential error;
- contextual capture unavailable/disabled/unreliable;
- local configuration/integration storage failure; and
- provider timeout/request/response failure.

Exact new numeric assignments are an implementation compatibility decision; user-facing behavior and
no-transmission guarantees above are the stable contract.
