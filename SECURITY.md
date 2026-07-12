# Security policy

## Reporting a vulnerability

Please report security issues privately to Adam Frisby at `adam@deepthink.com.au`. Do not include live API keys, full production transcripts, or other secrets. A minimal synthetic reproduction is preferred.

## Deployment notes

- Treat the proxy as a credential-bearing service: provider authorization headers pass through it.
- Terminate inbound TLS at a trusted reverse proxy or platform ingress for non-local deployments.
- Do not enable request-body logging in surrounding infrastructure unless transcript retention is intentional and governed.
- Keep upstream origins operator-controlled. Never derive them from request headers or query parameters.
- Restrict network access and bind addresses to the clients that should be allowed to use provider credentials.
- Configure `PinkUnicorn__AccessToken` for any remotely reachable instance and protect it behind authenticated TLS ingress.
- Keep `AllowOtherPaths`, `AllowInsecureUpstreams`, and `AllowPrivateUpstreams` disabled unless their expanded trust boundary is intentional.
- Response diagnostic headers contain counts and outcomes, not transcript text.

Supported security fixes target the current `main` branch while the project is pre-1.0.
