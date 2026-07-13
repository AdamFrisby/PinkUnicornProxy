# Design

## Goals

Pink Unicorn Proxy derives a provider-valid request in which an explicitly marked rejected line of reasoning has been semantically removed from the complete supplied history.

The design has four primary invariants:

1. No marker means no auxiliary call and byte-exact request-entity pass-through.
2. Human-language judgment belongs to a configurable LLM, not phrase matching or string scrubbing.
3. Model output is untrusted and can mutate only pre-authorized user/assistant text handles.
4. A semantic edit removes provider-specific opaque reasoning from the revised causal region as complete protocol atoms; an earlier authoritative cached prefix remains byte-exact.

The proxy is fixed-origin and keeps a bounded, durable SQLite revision cache. Clients opt in by changing their provider base URL; the operator, not a request header, controls both primary origins and the auxiliary rewriter URL.

## Request pipeline

1. Remove the fixed `/openai` or `/anthropic` mount prefix and classify known JSON generation endpoints.
2. Forward unknown paths, methods, encodings, media types, and oversized bodies without inspection.
3. For an inspectable request, buffer and parse the complete UTF-8 body without normalizing it.
4. Build a provider-aware transcript view over `messages` or `input`.
5. Scan all user text slots for an exact configured marker such as `!!NO!!`.
6. If no marker exists, rewind the original request body and forward the exact original bytes.
7. Before calling the LLM, reject provider-held history, protected marker text, or active tool/thinking transactions that cannot be safely changed.
8. Give the auxiliary model either the marker-delimited revision root, or—for a continuation—the complete supplied history plus its immutable rewritten prefix and unmatched suffix. Expose stable IDs only for text the current plan may edit.
9. Parse the model's JSON edit plan and validate every operation against the immutable handle set.
10. Apply the complete validated plan to the derived JSON tree.
11. Remove stale Anthropic thinking blocks or OpenAI reasoning/compaction artifacts.
12. Atomically publish the mapping from original item hashes to the validated rewritten history in the bounded SQLite revision cache.
13. Serialize only the changed top-level history value and splice it between untouched byte ranges from the original request.
14. Forward once, without retrying a non-idempotent generation request, and stream the provider response opaquely.

Clients commonly resend their original transcript on every turn. The proxy therefore rediscovers historical markers on every request, then consults the durable revision cache instead of asking a nondeterministic model to recreate an existing alteration.

That rediscovery is followed by cache lookup. An exact original history replays its exact prior rewritten bytes. An array that extends a cached original freezes the rewritten prefix and authorizes the auxiliary model to edit only handles in the unmatched suffix. The planner sees both the complete original history and the authoritative rewritten prefix, so it can remove a later relapse without being allowed to regenerate earlier text. The reviewed continuation is then cached. A new marker in the suffix bypasses prefix reuse and creates a new full-history revision.

## Revision cache and prompt-prefix stability

Each successful rewrite stores:

- provider/request kind;
- SHA-256 hashes of canonical original history items;
- the exact compact rewritten history bytes;
- edit/opaque-removal counts and LRU/TTL metadata.

Original unrewritten transcript text is not duplicated into the cache; item hashes are sufficient for prefix matching. Rewritten text is necessarily retained as a SQLite BLOB so its exact bytes can be replayed. The application keeps no separate managed-memory transcript cache.

For exact hits, cached history bytes are spliced directly. For a continuation, the proxy copies the cached rewritten array's inner bytes verbatim and serializes only the reviewed suffix. This preserves the exact prior rewritten JSON and provider message/token prefix. An empty suffix plan is valid; an empty full-history plan is not, because every marker-bearing field must be replaced.

On a cold array history, the revision root ends at the last trigger-bearing item unless that boundary would bisect a tool transaction; in that case it extends through the minimum transaction-closing assistant item. The root is planned and locked independently of later turns, then any remainder is reviewed as a suffix. Thus concurrent ancestor and descendant requests converge on the same root regardless of which arrives first. Later continuations lock by complete original-history hash, while striped semaphores avoid an unbounded per-history lock table.

The cache is bounded by approximate logical entry bytes and sliding TTL. Successful reads advance a persisted access sequence and renew the TTL; insert/eviction transactions retain the most recently used entries within the disk budget. A background timer deletes expired rows, runs incremental vacuum work, and passively checkpoints the WAL even when marked traffic stops. Physical database/WAL size can differ temporarily from the logical bound because SQLite reuses free pages.

The database uses an application ID and schema version so an unrelated, older-unmigratable, or newer database is rejected rather than overwritten. A SHA-256 namespace incorporates the configured generation, normalized trigger-token set, and internal rewrite-policy version, allowing safe automatic policy separation and operator-defined epochs. Provider identities are stable text values rather than enum ordinals. Exact lookups use the primary key; continuation lookup is narrowed by an indexed trigger-root anchor before comparing ordered item hashes. SQLite runs in WAL mode with full synchronous commits and a bounded busy timeout. Read misses remain read-only; successful hits conditionally touch the same immutable row identity so eviction/reinsertion races become misses rather than reviving or returning a replacement row. Within one process, fixed striped semaphores single-flight LLM creation without retaining per-conversation objects. Across processes sharing the same local file, `INSERT OR IGNORE` plus an authoritative reread makes the first committed revision win; a loser discards its local proposal before forwarding.

WAL storage is local-host persistence, not a distributed database. It must not be placed on a network filesystem or shared between hosts. Durable transcript retention changes the threat model: `secure_delete` mitigates ordinary free-page recovery, but the database, WAL, backups, and filesystem snapshots remain sensitive and should reside on encrypted storage where required.

## Semantic planning protocol

For an initial correction, the model receives history as untrusted JSON data and can target every authorized text handle through the last marker. It is instructed to inspect assumptions and intermediate conclusions throughout that history, not merely edit the marker turn. For a continuation, it receives the complete original history, the immutable authoritative prefix, the unmatched suffix, and handles only for suffix text. A typical response is:

```json
{
  "edits": [
    { "id": "t1.s0", "replacement": "The certificate evidence is the leading explanation." },
    { "id": "t3.s0", "replacement": "The TLS certificate expired." }
  ]
}
```

`replacement` is the complete new text value. `null` is accepted only for a text block that can be removed without emptying its content array. Plain string messages must be affirmatively rewritten rather than structurally deleted.

The proxy does not ask the model to emit JSON paths or a replacement provider request. Stable IDs are meaningful only within one planning snapshot. The following invalidate the whole plan:

- an unknown or duplicate ID;
- more than `MaximumEditsPerRequest` operations;
- an empty replacement;
- any replacement that still contains a configured marker;
- failure to edit every marker-bearing user slot;
- deletion that would empty a content array;
- malformed, oversized, timed-out, or non-successful auxiliary output.

The structural validator is the security boundary. Prompt instructions reduce bad plans but are not relied upon for authorization.

## Editable and protected data

Editable handles are created only for plain user/assistant text:

- string `content` values;
- recognized OpenAI text/input-text/output-text blocks;
- Anthropic text blocks;
- string OpenAI Responses `input`.

System/developer roles and tool roles never receive handles. Non-text blocks, media, tool structures, IDs, arguments, results, top-level request configuration, and unknown siblings never receive handles. Text-bearing blocks with citations, annotations, or log probabilities are protected because their offsets or metadata depend on the original value.

Unknown fields are preserved by retaining raw JSON nodes. Outside the top-level history value, preservation is lexical: whitespace, property order, escaping, and unknown data remain the exact original bytes. Inside a successfully rewritten history value, unchanged nodes retain their semantic values but the array/string is compactly reserialized.

## Trigger policy

Markers are syntax, not natural-language classifiers. Matching is ordinal and case-sensitive, and only user text can activate it. A marker inside assistant output, system instructions, tool data, media, or an unknown field has no effect.

Unmarked negatives remain untouched. This prevents ordinary instructions such as “do not delete production” from being interpreted as history surgery and makes the cost/failure boundary explicit to the caller.

An uninspectable request is forwarded unchanged, which means a marker hidden in compressed or oversized data cannot be enforced. Transparent pass-through and mandatory marker enforcement are mutually exclusive at that boundary.

## Provider integrity rules

### Anthropic Messages

For a new full-history revision, the adapter removes complete `thinking` and `redacted_thinking` blocks. A semantic suffix edit removes those blocks from the unmatched suffix. It never edits thinking text, `signature`, or encrypted `data` individually.

An authoritative cached prefix is a causal boundary: opaque blocks already accepted into that prefix are replayed byte-exact when only later suffix text changes. They were generated or relayed against that same authoritative context, and later text cannot invalidate an earlier signature. A new marker rebases the history and removes opaque blocks throughout the new revision.

If a cold rewrite or a proposed suffix edit intersects an unfinished thinking/tool transaction, the request returns `409`. A clean transaction appended after a cached authoritative prefix can be relayed unchanged so an agent can finish its tool loop. Tool uses/results are tracked by ID across the complete transcript; a later ordinary assistant turn does not hide an unresolved earlier call.

Primary references:

- [Extended thinking and thinking encryption](https://platform.claude.com/docs/en/build-with-claude/extended-thinking#thinking-encryption)
- [Messages API](https://platform.claude.com/docs/en/api/messages)
- [Tool use](https://platform.claude.com/docs/en/agents-and-tools/tool-use/overview)

### OpenAI Chat Completions

The adapter edits only enumerated `messages` text. Assistant tool-call structures remain intact. On a semantic rewrite it removes nonstandard assistant `reasoning`, `reasoning_content`, and `encrypted_content` properties as complete properties.

### OpenAI Responses

Client-managed `input` can be rewritten. Complete `reasoning` and `compaction` items are removed from a newly revised root or edited suffix because they may encode the discarded premise; an immutable cached prefix follows the same causal replay rule as Anthropic opaque blocks.

`previous_response_id` and `conversation` refer to history held by the provider and absent from this request. A marked stateful request always returns `409`; changing an unrelated top-level reference would violate both the transparency contract and selective-rewrite guarantee.

Primary references:

- [Conversation state](https://developers.openai.com/api/docs/guides/conversation-state)
- [Responses API](https://developers.openai.com/api/reference/resources/responses)

## Auxiliary transports

`OpenAIChatCompletions` uses a complete configured Chat Completions URL and reads `choices[0].message.content`. It deliberately avoids optional structured-output and sampling fields for compatibility with lightweight local implementations.

The OpenAI-compatible token-limit field is configurable as `max_completion_tokens`, `max_tokens`, or omitted. The complete final auxiliary request is byte-bounded before any network call; history is never truncated because partial history would violate the semantic goal.

`AnthropicMessages` uses a complete Messages URL, top-level `system`, one user message, `max_tokens`, `x-api-key`, and a configurable `anthropic-version`. It reads text content blocks.

Both transports have bounded response bodies and a linked timeout. The primary request's authorization headers are never copied into the auxiliary request. Configured auxiliary credentials and headers are never copied into the primary request.

A reserved internal header marks auxiliary calls. Every Pink Unicorn provider route rejects that header with `508`, preventing a mistaken self/chained configuration from recursively planning its own planner request. Auxiliary activity propagation is disabled so inbound baggage/tracing metadata does not cross this data-processing boundary.

## Forwarding contract

YARP direct forwarding copies end-to-end request and response headers and handles streaming/cancellation. Pink Unicorn removes only a configured `X-Pink-Unicorn-Key` used for optional local authentication. It does not synthesize forwarding or diagnostic headers.

Literal wire identity is impossible through a reverse proxy: TLS is re-originated, the mount prefix and upstream `Host` differ, hop-by-hop fields are consumed, and HTTP framing/version/header casing can differ. The supported invariant is exact request entity bytes on a no-op, exact bytes outside the rewritten history value on an edit, and preservation of end-to-end header values subject to those documented transport exceptions.

Configured upstream origins prevent client-controlled SSRF. Insecure/private primary targets require explicit operator flags. A concurrency limiter bounds simultaneous requests.

## Failure policy

Before a marker is observed, transparency wins: unsupported or malformed traffic passes to the provider.

After a marker is observed, rewrite integrity wins: missing configuration, auxiliary or durable-cache failure, malformed/unsafe output, provider-held state, signed-body conflicts, and active transactions fail closed. A planned revision is not forwarded until its authoritative bytes have committed. The unmodified marked request is never silently sent to the primary model.

## Non-goals

- Guaranteeing what a nondeterministic auxiliary model will infer semantically.
- Editing system/developer instructions, tool data, media, or citation-bearing text.
- Rewriting realtime WebSocket conversation state.
- Persisting original transcript plaintext, edit plans, complete request bodies, headers, or credentials; only original item hashes, exact rewritten histories, and cache metadata are stored.
- Sharing the SQLite WAL cache across hosts or network filesystems.
- Erasing provider-held conversations, external agent memory, summaries, vector stores, caches, or tool-side state.
- Preserving TLS sessions, raw HTTP framing, header order/casing, or other transport-level wire identity.
