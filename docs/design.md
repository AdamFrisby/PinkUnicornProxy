# Design

## Goals

Pink Unicorn Proxy derives a provider-valid transcript in which an explicitly rejected claim is absent from editable model-visible history. The original client transcript is never persisted or mutated. The operation is deterministic, stateless, auditable through non-sensitive outcome headers, and a byte-exact no-op when no rewrite occurs.

The proxy is a fixed-origin reverse proxy, not a general forward proxy. Clients opt in by changing their API base URL; TLS to the real provider remains an ordinary outbound connection. Only the documented generation/token endpoints are admitted by default, and an optional proxy access token can protect remotely reachable deployments.

## Pipeline

1. Classify the fixed provider route and supported generation endpoint.
2. Admit an allowlisted POST endpoint, then buffer unencoded JSON up to the configured limit. Encoded, non-JSON, or body-signed generation requests fail closed in rewrite mode.
3. Parse with `JsonNode` so unknown properties and content block types survive a changed request.
4. Project only editable user/assistant text into a small transcript view. System/developer content and protocol blocks remain protected.
5. Detect every high-confidence correction in chronological order.
6. Apply each event to a derived JSON tree:
   - scrub exact rejected claims from preceding user/assistant prose;
   - scrub later assistant relapses into that same exact claim;
   - for a deictic rejection, remove the preceding removable assistant turn;
   - replace the correction with user-supplied affirmative truth or a neutral reassessment instruction.
7. Invalidate provider-specific opaque reasoning.
8. Validate atomic boundaries, serialize only when changed, and forward without retries.
9. Copy the upstream response as an opaque stream.

All correction turns are rediscovered on every request. This is required because most clients continue to hold and resend the unmodified local transcript.

## Precision policy

The natural-language rules are deliberately narrow and anchored to the entire user text. This avoids treating safety instructions or ordinary factual negatives as corrections. Explicit `[[forget: ...]] [[truth: ...]]` markers are recommended where deterministic behavior matters.

String scrubbing is case-insensitive, whitespace-normalized, and word-boundary aware. It operates at sentence/line granularity. Claims found only in code fences, inline code, or URLs make the rewrite unsafe rather than modifying those protected spans.

System and developer roles are immutable. Tool arguments and results are immutable. Unknown structured content is preserved. This means a claim deliberately embedded in a protected role cannot be given a hard forgetting guarantee by this proxy.

## Provider integrity rules

### Anthropic Messages

The Messages API is stateless, so the supplied `messages` array is the rewrite boundary. Completed historical thinking can be omitted. On any semantic rewrite, the adapter removes complete `thinking` and `redacted_thinking` blocks instead of editing `signature`, visible summaries, or encrypted `data`.

An active extended-thinking tool loop is different: Anthropic requires the latest assistant thinking/tool blocks to be replayed complete and unmodified. The proxy checks the complete request suffix—even when the correction occurred much earlier—and returns `409` rather than manufacture an invalid continuation. Client tool-use and tool-result units are never split.

Relevant primary documentation:

- [Extended thinking](https://platform.claude.com/docs/en/build-with-claude/extended-thinking)
- [Context editing](https://platform.claude.com/docs/en/build-with-claude/context-editing)
- [Handling tool calls](https://platform.claude.com/docs/en/agents-and-tools/tool-use/handle-tool-calls)
- [Messages API](https://platform.claude.com/docs/en/api/messages/create)
- [API versioning](https://platform.claude.com/docs/en/api/versioning)

### OpenAI Chat Completions

The adapter edits `messages` text and preserves unknown fields and non-text content. Assistant tool calls and tool results are protocol structures and are not partially removed. On a rewrite, nonstandard opaque assistant reasoning properties are discarded conservatively.

### OpenAI Responses

Client-managed `input` arrays can be rewritten. Complete `reasoning` items and encrypted `compaction` items are discarded after a semantic edit because they may encode the rejected premise.

`previous_response_id` and Conversations refer to history held by the provider and absent from the HTTP request. Selective rewriting is impossible at that boundary. The policies are:

- `Reject` (default): return `409` when a detected correction depends on provider-held context.
- `PassThrough`: forward the original request and report `unsupported-stateful-context`.
- `FreshStart`: remove the provider state references and send the rewritten explicit input as a new conversation.

Relevant primary documentation:

- [Conversation state](https://developers.openai.com/api/docs/guides/conversation-state)
- [Responses create reference](https://developers.openai.com/api/reference/resources/responses/methods/create)

## Forwarding properties

YARP direct forwarding handles hop-by-hop headers, cancellation, HTTP versions, and response streaming. Generation POSTs are never retried because they are not inherently idempotent. Upstream origins are configuration, not request input, which avoids an SSRF-capable open proxy. Client-supplied forwarding, cookie, proxy-authentication, and `X-Pink-Unicorn-*` headers are removed before forwarding. A concurrency limiter bounds simultaneous buffered/streaming requests.

See Microsoft’s [YARP direct forwarding](https://learn.microsoft.com/aspnet/core/fundamentals/servers/yarp/direct-forwarding) documentation.

## Non-goals for v0.1

- General semantic coreference resolution.
- Inventing or inferring a replacement cause.
- Modifying code, tool state, system/developer instructions, or media.
- Rewriting realtime WebSocket sessions.
- Persisting conversations or API credentials.
- Erasing external agent memory, provider-held state, caches, or vector stores.

An optional semantic resolver can be added later, but its output must remain an edit plan constrained to exact anchors; it must never receive authority to emit a replacement transcript directly.
