# Pink Unicorn Proxy

Pink Unicorn Proxy is an experimental OpenAI/Anthropic HTTP endpoint that uses a small auxiliary LLM to rewrite corrected assumptions out of the complete conversation history before the request reaches the primary model.

The motivating failure mode is the invisible pink unicorn problem: once a model has committed to a bad cause, both the cause and the user's later negation keep that cause salient. A marked correction such as:

```text
!!NO!! The failure is caused by the expired certificate.
```

causes the proxy to ask a separately configured rewriter model for edits across the entire supplied history. The correction becomes an affirmative fact, and earlier user/assistant text that led toward the rejected conclusion can be rewritten at the same time.

```text
client ──► /openai/*    ──► configured OpenAI origin
       └─► /anthropic/* ──► configured Anthropic origin
                  │
                  └─ trigger ──► auxiliary history rewriter
```

There is no TLS interception. Clients opt in by using the proxy's OpenAI- or Anthropic-compatible base URL.

## Status

This is a greenfield, pre-1.0 project. Triggered transcripts are sent to a second model endpoint, model-produced edit plans are untrusted, and rewritten requests have additional latency and cost. Run it locally or behind a trusted authenticated ingress while evaluating it.

## Quick start

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet run --project src/PinkUnicornProxy --urls http://127.0.0.1:5080
```

Point clients at one of these base URLs:

| Provider API | Local base URL | Default primary upstream |
|---|---|---|
| OpenAI | `http://127.0.0.1:5080/openai/v1` | `https://api.openai.com` |
| Anthropic | `http://127.0.0.1:5080/anthropic` | `https://api.anthropic.com` |

An unmarked request is ordinary pass-through traffic. A marked request invokes the history rewriter:

```bash
curl http://127.0.0.1:5080/openai/v1/chat/completions \
  -H "Authorization: Bearer $OPENAI_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "your-primary-model",
    "messages": [
      {"role":"user","content":"The service cannot resolve its dependency."},
      {"role":"assistant","content":"This appears to be a DNS failure."},
      {"role":"user","content":"!!NO!! The TLS certificate expired."}
    ]
  }'
```

Configure the auxiliary endpoint before sending a trigger. Without one, a triggered request fails closed with `503` and is not sent to the primary upstream.

## Configure the history rewriter

The auxiliary protocol is independent of the primary provider. An Anthropic request can use an OpenAI-compatible local model to rewrite its history, and an OpenAI request can use Anthropic Haiku.

### OpenAI-compatible endpoint

`Endpoint` is the complete Chat Completions URL. The client sends `model`, system/user `messages`, `stream: false`, and the configured output-token field unless it is set to `Omit`, which keeps it compatible with many hosted and local implementations. It intentionally does not require provider-specific structured-output extensions; returned JSON is parsed and validated locally.

```json
{
  "PinkUnicorn": {
    "HistoryRewrite": {
      "TriggerTokens": [ "!!NO!!" ],
      "Protocol": "OpenAIChatCompletions",
      "Endpoint": "https://api.openai.com/v1/chat/completions",
      "Model": "your-lightweight-model",
      "MaximumOutputTokens": 4096,
      "OpenAIOutputTokenParameter": "MaxCompletionTokens",
      "MaximumPlannerRequestBytes": 8388608,
      "MaximumResponseBodyBytes": 1048576,
      "Timeout": "00:00:30"
    }
  }
}
```

Set the secret outside source control:

```bash
export PinkUnicorn__HistoryRewrite__ApiKey="$REWRITER_API_KEY"
```

A local OpenAI-compatible server works the same way:

```bash
export PinkUnicorn__HistoryRewrite__Endpoint="http://127.0.0.1:11434/v1/chat/completions"
export PinkUnicorn__HistoryRewrite__Model="your-local-model"
```

Loopback HTTP is allowed for local models. Remote auxiliary endpoints require HTTPS unless `AllowInsecureUpstreams` is deliberately enabled. Configure the endpoint directly; do not point it back through Pink Unicorn Proxy, which would create a recursive rewrite request.

Current OpenAI APIs use `max_completion_tokens`; many local compatibility servers use the older `max_tokens`. Select `MaxCompletionTokens`, `MaxTokens`, or `Omit` with `OpenAIOutputTokenParameter`. The response byte limit is a transport/memory bound, not a substitute for the model-side token cap.

### Anthropic Messages endpoint

```json
{
  "PinkUnicorn": {
    "HistoryRewrite": {
      "TriggerTokens": [ "!!NO!!" ],
      "Protocol": "AnthropicMessages",
      "Endpoint": "https://api.anthropic.com/v1/messages",
      "Model": "your-haiku-model",
      "AnthropicVersion": "2023-06-01",
      "MaximumOutputTokens": 4096,
      "Timeout": "00:00:30"
    }
  }
}
```

`ApiKey` becomes `Authorization: Bearer ...` for an OpenAI-compatible rewriter and `x-api-key: ...` for an Anthropic rewriter. `HistoryRewrite:Headers` can supply additional gateway-specific headers. Primary-provider authentication headers are never included in the auxiliary request, and auxiliary authentication headers are never forwarded to the primary provider. Secrets written inside conversation text are part of the history and therefore are sent to the rewriter.

The transport shapes follow the current [OpenAI Chat Completions API](https://developers.openai.com/api/reference/resources/chat) and [Anthropic Messages API](https://platform.claude.com/docs/en/api/messages).

## Trigger semantics

- Each configured token is matched as an ordinal, case-sensitive substring of a user text field. A field containing multiple token occurrences is counted once.
- Only user-authored text in the complete supplied history can trigger a rewrite.
- One or more configured tokens in historical user text establish a revision root through the last marker, extended just far enough to avoid bisecting a completed tool transaction. The root planner may edit every authorized user/assistant field in that root, not only the marker turn.
- If the first observed request already has later turns, a separate continuation plan sees the complete supplied history plus the immutable rewritten root and may edit only the unmatched suffix.
- Every trigger-bearing field must be rewritten as non-empty text with all trigger tokens removed.
- With the durable cache enabled and the matching entry still retained, a client that resends an already reviewed transcript receives the cached authoritative rewrite without another LLM call. A previously unseen continuation keeps that authoritative prefix and asks the LLM to review only newly appended editable text.
- Text such as `No, it is not DNS` has no special meaning without a marker. Human language decisions belong to the auxiliary model, not a deterministic phrase detector.

For multiple tokens, use normal ASP.NET array configuration or environment indexes:

```bash
export PinkUnicorn__HistoryRewrite__TriggerTokens__0="!!NO!!"
export PinkUnicorn__HistoryRewrite__TriggerTokens__1="!!REWRITE!!"
```

## Stable multi-turn replay

The first accepted edit plan becomes the authoritative revision for that original history while its durable SQLite entry is retained. The cache provides two guarantees:

- an identical resent history receives the exact same rewritten history bytes without another LLM call;
- when a later request extends that original message array, the proxy freezes the identical rewritten prefix, lets the LLM edit only the new suffix, and caches that reviewed continuation as the next authoritative revision.

This catches an assistant that reintroduces the discarded idea on a later turn without regenerating any prior text, keeping the prior history-token prefix stable for provider prompt caching. Actual cache eligibility and hits remain provider/model/configuration dependent. A suffix plan may be empty when the continuation is already consistent. Exact replays make no auxiliary call; a previously unseen suffix with editable user/assistant text normally makes one.

Structurally complete cold ancestor/descendant requests establish a shared revision root through the last marker (plus any required transaction-closing assistant turn) before either may extend it. Within one proxy process, concurrent root and identical full-history misses are single-flighted. Processes sharing one local SQLite file may both call the planner, but first-writer-wins publication ensures they forward only one committed authority.

A new marker in appended user text deliberately creates a new full-history revision and cache entry. Older entries remain available for conversation branches until disk-LRU/TTL eviction. The default retains approximately 10 GiB of logical entry data for a 30-day sliding TTL. Entries become ineligible exactly at TTL; a background sweep runs at the lesser of `CleanupInterval` and TTL to delete expired rows without traffic, then adaptively reclaims free pages and checkpoints WAL. SQLite reuses free pages, so the physical file follows a high-water mark and can take additional sweeps to shrink after a large deletion.

The cache stores exact rewritten transcript bytes and hashes of original message items in `data/pink-unicorn-cache.db`, relative to the application content root; it does not duplicate original transcript text or store primary request headers, edit plans, or auxiliary credentials. Transcript payloads are read from SQLite only for the current request—there is no managed-memory transcript cache. Exact replay therefore survives process restarts. Cache identity includes the provider/request kind, normalized trigger-token set, configured generation, and an internal rewrite-policy version. Changing only the auxiliary endpoint or model does not invalidate existing authorities; increment `HistoryRewrite:Cache:Generation` for an operator-defined policy epoch. Older namespace rows then remain on disk and count toward the logical size limit until TTL/LRU eviction. Disable the cache with `HistoryRewrite:Cache:Enabled=false` at the cost of deterministic replay and prompt-cache stability. `Audit` and `Off` modes do not open the database.

SQLite WAL supports multiple proxy processes on the same host and file. Publication is first-writer-wins: a racing process rereads and forwards the stored authority rather than its losing local proposal. A marked request fails closed if the cache cannot be read or its authority cannot be committed. The proxy tags its database and rejects unrelated, unsupported older, or newer schemas rather than overwriting them; in this pre-1.0 project, an incompatible cache requires a new database path/volume or a deliberate discard of replay history. Do not place the WAL database on a network filesystem or share it across hosts; multi-host deployment requires a different shared store.

The SQLite file contains conversation-derived plaintext, so protect the directory and backups and use encrypted storage when required. SQLite `secure_delete` is enabled, but expiry and deletion cannot erase copies in earlier WAL frames, snapshots, backups, or storage remanence. On Unix-like systems, newly created cache paths are restricted to the service account.

## What the model may edit

The auxiliary model does not return a replacement request. It returns a small edit plan using stable IDs and history indexes assigned by the proxy. Deterministic validation permits only complete replacements of enumerated user/assistant text fields.

The model cannot target:

- system or developer messages;
- tool arguments, tool results, IDs, or provider protocol objects;
- images, audio, files, or other non-text blocks;
- text with provider-managed citations, annotations, or log probabilities;
- top-level model, sampling, streaming, metadata, or unknown request properties.

An unknown ID, duplicate edit, remaining trigger, empty trigger replacement, excessive edit count, or malformed response rejects the entire plan. Nothing is forwarded to the primary model on that failure.

## Provider reasoning and tool safety

Visible text is not the only state that can retain a discarded premise.

- Anthropic: a new full-history revision removes complete `thinking` and `redacted_thinking` blocks, including their opaque `signature` or encrypted `data`; a newly edited suffix removes opaque blocks from that suffix. Individual fields are never modified. Opaque blocks already inside an authoritative cached prefix are replayed byte-exact because later suffix edits are causally downstream; a later marker that rebases the prefix removes them. Anthropic documents thinking signatures as opaque and requires replayed blocks to be passed back unchanged. See [extended thinking](https://platform.claude.com/docs/en/build-with-claude/extended-thinking#thinking-encryption).
- OpenAI Responses: complete `reasoning` and `compaction` items are removed from a newly revised root or edited suffix; immutable cached-prefix items remain exact.
- OpenAI Chat Completions: nonstandard assistant `reasoning`, `reasoning_content`, and `encrypted_content` properties follow the same root/suffix cleanup boundary.
- OpenAI provider-held state: a triggered request using `previous_response_id` or `conversation` returns `409`. History that is absent from the HTTP request cannot be selectively rewritten.
- Active tool call/result transactions are protocol atoms and are never partially changed. A cold rewrite that intersects one is rejected. A transaction appended after an authoritative cached prefix may pass only if suffix review proposes no semantic edit; its signed/opaque bytes remain exact.

## Transparency contract

If no trigger is found, the request entity bytes are forwarded exactly as received and no auxiliary request is made. The proxy does not add diagnostic request or response headers. End-to-end headers—including cookies, custom headers, vendor beta headers, and existing forwarding headers—are preserved.

On a successful rewrite, only the top-level history value (`messages` or `input`) is serialized and spliced into the original UTF-8 request. Every byte before and after that value remains untouched. `Content-Length` is updated only when the incoming request used it and the rewritten length differs.

A marked request carrying body-digest or HTTP-signature protection returns `409`, because changing its history would invalidate that protection. The same headers on an unmarked request remain untouched.

Like every HTTP reverse proxy, Pink Unicorn cannot preserve literal wire identity. The configured `/openai` or `/anthropic` mount prefix is removed, `Host` names the primary upstream, RFC hop-by-hop headers and HTTP framing are re-originated, and TLS terminates at the proxy. If optional proxy authentication is enabled, `X-Pink-Unicorn-Key` is consumed locally. Those are explicit transport exceptions to the transparency promise.

Uninspectable requests are passed through, not blocked:

- non-JSON or malformed JSON;
- non-identity content encoding;
- bodies larger than `MaximumRequestBodyBytes`;
- methods and provider paths that are not supported generation calls.

Consequently, a marker hidden in compressed or oversized content is not detected. This is the deliberate trade-off required by transparent pass-through. Applications that require mandatory enforcement must reject such traffic at an ingress layer.

An unmarked body with duplicate JSON property names still passes through. If any competing history contains a user marker, however, the request is rejected: different JSON consumers can select different values, so neither a unique history nor a safe splice can be guaranteed.

## Inspected request shapes

- OpenAI Chat Completions: `POST /openai/v1/chat/completions`, history in `messages`.
- OpenAI Responses: `POST /openai/v1/responses`, client-managed history in `input`.
- OpenAI Responses compaction: `POST /openai/v1/responses/compact`.
- Anthropic Messages: `POST /anthropic/v1/messages`, history in `messages`.
- Anthropic token counting: `POST /anthropic/v1/messages/count_tokens`.

All other paths under `/openai/*` and `/anthropic/*` are transparently forwarded to their fixed configured origin. Provider responses, including SSE streams, are opaque pass-through data.

## Full non-secret configuration

`HistoryRewrite:ApiKey` and the optional proxy `AccessToken` are deliberately omitted below; supply them through environment variables or another secret provider.

```json
{
  "PinkUnicorn": {
    "Mode": "Rewrite",
    "MaximumRequestBodyBytes": 4194304,
    "MaximumEditsPerRequest": 128,
    "MaximumConcurrentRequests": 32,
    "AllowInsecureUpstreams": false,
    "AllowPrivateUpstreams": false,
    "ActivityTimeout": "00:10:00",
    "ConnectTimeout": "00:00:15",
    "HistoryRewrite": {
      "TriggerTokens": [ "!!NO!!" ],
      "Protocol": "OpenAIChatCompletions",
      "Endpoint": null,
      "Model": null,
      "AnthropicVersion": "2023-06-01",
      "MaximumOutputTokens": 4096,
      "OpenAIOutputTokenParameter": "MaxCompletionTokens",
      "MaximumPlannerRequestBytes": 8388608,
      "MaximumResponseBodyBytes": 1048576,
      "Timeout": "00:00:30",
      "Headers": {},
      "Cache": {
        "Enabled": true,
        "DatabasePath": "data/pink-unicorn-cache.db",
        "Generation": 1,
        "MaximumBytes": 10737418240,
        "TimeToLive": "30.00:00:00",
        "CleanupInterval": "00:15:00",
        "BusyTimeout": "00:00:10"
      }
    },
    "Upstreams": {
      "OpenAI": "https://api.openai.com",
      "Anthropic": "https://api.anthropic.com"
    }
  }
}
```

Modes:

- `Rewrite`: invoke the auxiliary model and apply validated plans.
- `Audit`: detect markers, log only provider kind and marker count, and forward original bytes.
- `Off`: do not inspect request bodies.

For a remotely reachable deployment, set a random `PinkUnicorn__AccessToken` of at least 16 characters and send it in `X-Pink-Unicorn-Key`, or place the service behind authenticated ingress. Conversation text and credentials are never logged by the application.

## Container

```bash
docker build -t pink-unicorn-proxy .
docker volume create pink-unicorn-data
docker run --rm \
  -p 127.0.0.1:5080:8080 \
  -v pink-unicorn-data:/data \
  pink-unicorn-proxy
```

The image stores its SQLite database under `/data`. Mount that path to retain authoritative rewrites across container replacement. Docker otherwise creates an anonymous volume, which `docker run --rm` removes with the container. A bind-mounted directory must be writable by the image's non-root service account, and its host permissions remain the operator's responsibility.

`/health/ready` returns `503` after a runtime cache I/O or maintenance failure and recovers after a later successful cache operation. `/health/live` reports only process liveness.

`.env.example` is a template, not an automatically loaded ASP.NET configuration file. For `dotnet run`, copy it to `.env`, edit it, then export its assignments with `set -a; source .env; set +a`. For Docker, pass it with `docker run --env-file .env`.

Inside a container, `127.0.0.1` names the container itself. To reach a model running on the Docker host on Linux:

```bash
docker run --rm \
  --add-host host.docker.internal:host-gateway \
  --env-file .env \
  -e PinkUnicorn__HistoryRewrite__Endpoint=http://host.docker.internal:11434/v1/chat/completions \
  -e PinkUnicorn__AllowInsecureUpstreams=true \
  -p 127.0.0.1:5080:8080 \
  -v pink-unicorn-data:/data \
  pink-unicorn-proxy
```

`AllowPrivateUpstreams` protects literal private **primary provider origins**. Private/loopback auxiliary endpoints are intentionally allowed for local models; non-loopback HTTP still requires `AllowInsecureUpstreams`.

## Development

```bash
dotnet restore PinkUnicornProxy.sln --locked-mode
dotnet build PinkUnicornProxy.sln -c Release --no-restore
dotnet test PinkUnicornProxy.sln -c Release --no-build --no-restore
dotnet format PinkUnicornProxy.sln --verify-no-changes --no-restore
```

The tests cover whole-history planning, adversarial edit plans, trigger rediscovery, byte-span preservation, OpenAI-compatible and Anthropic auxiliary transports, opaque reasoning removal, tool/state deferral, header/body transparency, fixed-origin routing, and opaque response streaming.

## Fundamental limitation

The proxy can remove only context supplied through it. It cannot erase provider-held conversations, an agent framework's memory or summaries, vector stores, tool-side state, caches, or anything already observed by a model. A validated rewrite reduces stale premise salience in the next explicit request; it is not retroactive erasure.

Licensed under the [MIT License](LICENSE).
