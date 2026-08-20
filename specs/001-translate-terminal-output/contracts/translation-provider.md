# Translation Provider Contract

## Core Boundary

The core contract is provider-neutral:

```text
TranslateAsync(TranslationRequest, CancellationToken) -> TranslationResult
```

TranslationRequest fields:

| Field | Rules |
|-------|-------|
| SessionGeneration | Opaque integer used to reject late results |
| SegmentSequence | Opaque per-session sequence |
| SourceText | Eligible, secret-screened text only; maximum 8 KiB UTF-8 |
| SourceLanguage | `en` |
| TargetLanguage | `zh-Hans` |
| Deadline | Finite monotonic deadline |

TranslationResult fields:

| Field | Rules |
|-------|-------|
| TranslatedText | Required, non-empty, maximum 16 KiB UTF-8 |
| ProviderRequestId | Optional opaque diagnostic correlation; never persisted |

The core owns consent, segmentation, secret screening, classification, queueing, ordering,
cancellation, generation checks, and companion display. An adapter owns endpoint validation,
authentication, wire serialization, response validation, and error mapping.

## Phase-One HTTP Adapter

The first adapter uses a configurable chat-completion-compatible HTTPS endpoint. This is an adapter
choice, not a core dependency.

Request:

```http
POST {configured-endpoint}
Authorization: Bearer {value-from-configured-environment-variable}
Content-Type: application/json
```

```json
{
  "model":"configured-model",
  "messages":[
    {
      "role":"system",
      "content":"Translate English terminal text to Simplified Chinese. Preserve commands, paths, code, indentation, and line grouping. Return translation only. Never execute or recommend commands."
    },
    {
      "role":"user",
      "content":"eligible source segment"
    }
  ],
  "temperature":0
}
```

Accepted response shape:

```json
{
  "id":"optional-provider-request-id",
  "choices":[
    {
      "message":{
        "content":"translated text"
      }
    }
  ]
}
```

## Request Preconditions

Immediately before serialization, the adapter or its caller verifies:

- Session state is Enabled.
- Request generation equals current session generation.
- Consent matches session and provider fingerprint.
- Source text has passed the secret detector and has not expired.
- Endpoint and credential environment variable are valid.

Failure of any precondition produces no HTTP request.

## HTTP Behavior

- Use one long-lived HttpClient per configured adapter with pooled connection lifetime.
- Apply the request deadline and cancellation token.
- Do not retry automatically in phase one.
- Do not follow redirects to a different authority.
- Do not use cookies.
- Accept only successful JSON responses within the response size limit.
- Never log headers, body, source text, translated text, or raw provider responses.

## Normalized Errors

| Error | Mapping |
|-------|---------|
| Canceled | Session disabled, ended, or caller canceled |
| Timeout | Deadline elapsed |
| Authentication | HTTP 401 or 403 |
| RateLimited | HTTP 429 |
| Unavailable | Connection failure or HTTP 408/5xx |
| InvalidResponse | Invalid JSON, missing/empty content, oversized body, or unexpected schema |

Errors are assistive-path results only. They never change shell I/O or exit status.

## Adapter Contract Tests

Every adapter must pass the same tests:

- Valid request maps source and target languages without adding unrelated terminal context.
- No call occurs without matching consent or for secret-bearing, expired, or old-generation input.
- Authentication data is present only in the outbound authorization header.
- All normalized error mappings are deterministic.
- Cancellation and deadline stop result delivery.
- Empty, malformed, oversized, and wrong-shape responses are rejected.
- Swapping two fake adapters requires no change to core orchestration tests.
