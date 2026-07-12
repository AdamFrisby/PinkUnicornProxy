# Proxy problem responses

The proxy uses `application/problem+json` for requests that cannot be forwarded without violating the configured rewrite guarantee.

## request-body-too-large

The inspectable request exceeded `MaximumRequestBodyBytes`. Increase the limit deliberately or reduce the explicit history. The request was not sent upstream.

## authentication-required

The instance has an `AccessToken`, and the request did not supply the matching value in `X-Pink-Unicorn-Key`. This proxy authentication header is consumed locally and never sent to the provider.

## path-not-allowed

The path is outside the default provider endpoint allowlist. Enable `AllowOtherPaths` only when the instance is intentionally allowed to relay the rest of the configured upstream origin.

## method-not-allowed

Supported generation routes accept `POST`. `CONNECT`, `TRACE`, and protocol upgrades are always blocked.

## non-json-body-cannot-be-rewritten

A supported generation request did not use `application/json` or a `+json` media type. Rewrite mode fails closed instead of passing an uninspectable body to the provider.

## encoded-body-cannot-be-rewritten

A supported generation request used a non-identity `Content-Encoding`. Rewrite mode currently requires unencoded JSON and does not decompress/recompress potentially hostile bodies.

## signed-body-cannot-be-rewritten

The request included a known content digest, HTTP Message Signature, or body-bound authorization scheme. Rewriting would invalidate that integrity protection, so the request was not sent upstream.

## stateful-context-cannot-be-rewritten

An OpenAI Responses correction referred to context behind `previous_response_id` or `conversation`. The hidden provider state cannot be selectively modified. Supply full client-managed `input`, choose `FreshStart` to abandon that state, or explicitly choose `PassThrough` without a forgetting guarantee.

## open-tool-turn-is-immutable

The correction intersects an active tool/thinking transaction. Complete the tool transaction first or abandon it and start from a clean, structurally complete transcript. The proxy will not alter signed thinking blocks or split tool call/result pairs.

## protected-content-cannot-be-rewritten

The rejected claim occurs in protected code, URL, media, or protocol content. The proxy does not edit those structures and therefore cannot make a forgetting guarantee for this request. Rebase the conversation without the protected occurrence or express the correction in a new clean transcript.

## correction-limit-exceeded

The request contains more detected correction events than `MaximumCorrectionsPerRequest`. This protects the proxy from unexpectedly expensive rewrite work. Raise the limit only for trusted workloads.
