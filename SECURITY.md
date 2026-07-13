# Security policy

## Reporting a vulnerability

Please report security issues privately to Adam Frisby at `adam@deepthink.com.au`. Do not include live API keys, full production transcripts, or other secrets. A minimal synthetic reproduction is preferred.

## Deployment notes

- Treat the proxy as a credential-bearing service: provider authorization headers pass through it.
- Treat the auxiliary history rewriter as a second data processor. A triggered request sends its complete supplied conversation history to that endpoint. Review its retention, training, residency, and access policies before production use.
- The stable-revision cache durably retains exact rewritten conversation history in a plaintext SQLite database for its configured sliding TTL. It stores hashes—not duplicate plaintext—of the original items, but the rewritten transcript itself must remain readable for replay. Protect the database directory, snapshots, and backups; use filesystem or volume encryption when at-rest encryption is required.
- On Unix-like systems the proxy restricts a newly created cache directory and its database/WAL files to the service account. Keep the service account and mounted volume private; pre-existing parent directories and external backup permissions remain the operator's responsibility.
- SQLite `secure_delete` is enabled and cleanup checkpoints/adaptively reclaims pages, but deletion cannot erase copies already captured by filesystem snapshots, backups, storage remanence, or earlier WAL frames. Expired rows become unusable at TTL and are physically deleted on the next sweep, which runs at the lesser of TTL and `CleanupInterval`; file shrinking can require further sweeps. Disable the cache when durable transcript retention is unacceptable; doing so also disables deterministic replay and provider prompt-prefix reuse.
- Do not put the WAL database on NFS or another network filesystem. Same-host processes may share one file; replicas on different hosts need a purpose-built shared store.
- Use separate credentials for the primary provider and history rewriter. The implementation does not copy either authentication header into the other request. Secrets embedded in conversation text are still part of the history sent to the rewriter.
- Terminate inbound TLS at a trusted reverse proxy or platform ingress for non-local deployments.
- Do not enable request-body logging in surrounding infrastructure unless transcript retention is intentional and governed.
- Keep primary and history-rewriter endpoints operator-controlled. Never derive them from request headers, model output, or query parameters.
- Restrict network access and bind addresses to the clients that should be allowed to use provider credentials.
- Configure `PinkUnicorn__AccessToken` for any remotely reachable instance and protect it behind authenticated TLS ingress.
- Keep `AllowInsecureUpstreams` and `AllowPrivateUpstreams` disabled unless their expanded trust boundary is intentional. Loopback HTTP is permitted for a deliberately configured local history model.
- Do not point the history rewriter endpoint back through Pink Unicorn Proxy; the embedded marker would recursively invoke rewriting.
- Auxiliary calls carry a reserved recursion marker and do not propagate inbound tracing/baggage. Pink Unicorn routes reject re-entry with `508`.
- Model output is untrusted. Preserve the stable-ID allowlist and fail-closed plan validation when extending the planner schema.
- Cached prefixes are immutable causal boundaries. Opaque reasoning already in a cached prefix is replayed byte-exact; opaque blocks in a newly revised root or edited suffix are removed as complete atoms. Never mutate a signature/encrypted field individually.
- Uninspectable requests pass through transparently. Enforce JSON, identity encoding, and body-size policies at ingress if markers must never bypass inspection.
- The application logs only provider kind/count outcomes and forwarding errors, never prompt bodies, plans, provider error bodies, or credentials.

Supported security fixes target the current `main` branch while the project is pre-1.0.
