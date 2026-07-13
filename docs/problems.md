# Proxy problem responses

The proxy uses `application/problem+json` only after it knows a request cannot be forwarded safely. Ordinary unmarked or uninspectable provider traffic passes through.

## authentication-required

The instance has an `AccessToken`, and the request did not supply the matching value in `X-Pink-Unicorn-Key`. This proxy-local header is consumed and never sent to the provider.

## recursive-history-rewriter-request

An auxiliary planner request was routed back into a Pink Unicorn provider route. The reserved internal marker is rejected with `508` before recursion can consume proxy or model capacity. Configure the auxiliary URL as a direct model endpoint.

## ambiguous-json-properties

A marked request contains duplicate JSON property names. Different parsers can select different values, so the proxy cannot establish a unique history or safe byte splice. The request is rejected before any auxiliary or primary call. An unmarked duplicate-key body remains transparent pass-through.

## history-rewriter-not-configured

A marker was present, but `HistoryRewrite:Endpoint` and `HistoryRewrite:Model` are not configured. The request was not sent to the primary provider.

## history-rewriter-unavailable

The configured auxiliary endpoint timed out, could not be reached, returned a non-success status, or exceeded the configured response byte limit. The marked request was not sent to the primary provider.

## history-rewrite-cache-unavailable

The durable SQLite cache could not be read, committed, or cleaned safely. The marked request was not sent to the primary provider, and an uncommitted rewrite was not treated as authoritative. Check database path permissions, free disk space, schema compatibility, and whether another process holds a long-running write transaction.

## history-planner-input-too-large

The complete serialized auxiliary planning request exceeded `HistoryRewrite:MaximumPlannerRequestBytes`. History is never truncated because omitting earlier turns would defeat whole-history rewriting. Increase the bound deliberately or reduce the supplied history.

## invalid-history-rewriter-response

The auxiliary endpoint returned a response that was not a valid protocol response or did not contain the required JSON edit-plan shape.

## unsafe-history-rewriter-output

The model proposed an unknown/duplicate target, an empty or still-marked replacement, an unsafe deletion, no material edit, or failed to rewrite every marker-bearing field. The whole plan was rejected.

## history-edit-limit-exceeded

The auxiliary endpoint proposed more operations than `MaximumEditsPerRequest`. Raise the limit only for trusted workloads after considering request size, latency, and cost.

## signed-body-cannot-be-rewritten

The marked request included a known content digest, HTTP Message Signature, or body-bound authorization scheme. Rewriting would invalidate that integrity protection.

## stateful-context-cannot-be-rewritten

An OpenAI Responses request used `previous_response_id` or `conversation`. That provider-held history is absent from the request and cannot be selectively changed. Supply complete client-managed `input` in a new request.

## open-tool-turn-is-immutable

The marker intersects an active tool/thinking transaction. Complete the transaction first or abandon it and submit a structurally complete transcript. The proxy will not alter signed thinking blocks or split tool call/result pairs.

## protected-content-cannot-be-rewritten

A marker occurs in text with provider-managed citations, annotations, or log probabilities. Rewriting the text would invalidate its dependent metadata, so the request is rejected.
