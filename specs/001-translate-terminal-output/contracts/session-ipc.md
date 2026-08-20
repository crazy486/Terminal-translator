# Session IPC Contract

## Transport

- Local Windows named pipes using UTF-8 JSON Lines, one object per line.
- Pipe names contain a random session ID and unpredictable nonce.
- Pipe ACL grants the launching user only and denies network access.
- Maximum serialized message size is 32 KiB.
- Protocol version for phase one is `1`.

Two pipes are used:

- `events`: host server to one companion client.
- `control`: host server to short-lived `on`, `off`, and `status` clients.

The host never waits for the event client when forwarding original ConPTY output.

## Handshake

Client request:

```json
{"type":"hello","protocol":1,"sessionId":"opaque","nonce":"opaque","role":"companion"}
```

Host response:

```json
{"type":"hello-ack","protocol":1,"sessionId":"opaque","state":"disabled"}
```

Rules:

- Session ID, nonce, protocol, current user, and allowed role must all match.
- Invalid or oversized first messages close the pipe without echoing supplied values.
- The companion retries connection with bounded delay while the program host starts.

## Event Messages

### Translation

```json
{
  "type":"translation",
  "protocol":1,
  "sessionId":"opaque",
  "generation":2,
  "sequence":42,
  "sourceText":"normalized transient source",
  "translatedText":"translated transient text",
  "layout":{"lineCount":2,"indent":[0,2]}
}
```

The companion discards events for an older generation or non-monotonic sequence. Source and
translation fields are display-only and must not be logged or persisted.

### State

```json
{"type":"state","protocol":1,"state":"enabled","providerHost":"example.invalid","model":"model-id"}
```

### Privacy Skip

```json
{"type":"privacy-skip","protocol":1,"code":"suspected-secret","count":1}
```

This message contains no source excerpt, matched value, or detector detail.

### Degradation

```json
{"type":"degraded","protocol":1,"code":"translation-overload","dropped":7}
```

Drop events are aggregated and emitted no more than once every 5 seconds.

### Provider Error

```json
{"type":"provider-error","protocol":1,"code":"timeout"}
```

Allowed codes: `canceled`, `timeout`, `authentication`, `rate-limited`, `unavailable`, and
`invalid-response`. Messages contain no provider body or terminal text.

### Session End

```json
{"type":"session-ended","protocol":1,"reason":"shell-exit","exitCode":0}
```

## Control Messages

### Enable

The public CLI gathers consent before sending this request.

```json
{
  "type":"enable",
  "protocol":1,
  "sessionId":"opaque",
  "providerFingerprint":"opaque",
  "consent":true
}
```

Response:

```json
{"type":"control-result","protocol":1,"operation":"enable","ok":true,"state":"enabled"}
```

### Disable

```json
{"type":"disable","protocol":1,"sessionId":"opaque"}
```

Response includes the new generation and `disabled` state. The host acknowledges only after new
work is rejected and pending translation has been canceled and cleared.

### Status

```json
{"type":"status","protocol":1,"sessionId":"opaque"}
```

Response:

```json
{
  "type":"status-result",
  "protocol":1,
  "state":"enabled",
  "providerHost":"example.invalid",
  "model":"model-id",
  "highQueued":0,
  "normalQueued":2,
  "privacySkipped":3,
  "overloadDropped":0
}
```

## Failure Semantics

- Malformed, unknown, oversized, or unauthorized messages close only that IPC connection.
- Companion disconnect changes translation display to unavailable but does not stop or block the
  shell. Provider work may be canceled until a companion reconnects.
- Control-pipe failure makes the individual control command fail; the program session continues.
- Host shutdown cancels all pipe operations and sends session-ended when possible without waiting
  for delivery.
