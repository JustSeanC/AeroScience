# Railway public ingress decision — P26 completion pass

Checked 2026-10-29 against official documentation. **Deployment remains blocked.** This is a missing documented security contract, not a claimed failed live-provider test.

Railway's [edge architecture](https://docs.railway.com/networking/edge-networking) terminates public TLS and forwards requests to services. Its [public-network specifications](https://docs.railway.com/networking/public-networking/specs-and-limits) describe SNI, HTTPS, forwarded protocol/host/client address and informational Railway headers. They do not document a finite authenticated upstream peer set for an ASP.NET Core application. [Edge rules](https://docs.railway.com/networking/edge-rules) document matching and allow/block/redirect/cache/challenge actions, not an application-verifiable secret injection or upstream client certificate.

The existing application accepts at most four explicit proxy addresses, limits forwarded hops, checks the exact public origin, requires HTTPS and verifies PostgreSQL's hostname/certificate. These requirements are preserved. A fixture's loopback proxy is solely a fixture identity and must not be copied into a public deployment.

Rejected alternatives:

- Treating `X-Railway-Edge`, `X-Forwarded-Proto`, `X-Real-IP` or a public domain string as authentication.
- Trusting all addresses, the entire private mesh, arbitrary proxy chains or unrestricted automatic ASP.NET forwarding.
- Learning trusted peers from an anonymous request or public self-probe. Shared private-network peers/loopback forwarding do not establish an exclusive edge identity.
- Disabling certificate validation or running the web application as unrestricted root.

Closing this gate requires a supported finite proxy boundary or authenticated upstream transport that the application can validate. An authorized private account test must exercise real routing, hostname checks, forwarded-header spoofing, private-peer injection and peer rotation. No paid deployment should be approved merely to demonstrate startup with an invented allowlist. An additional ingress provider/tunnel would add accounts, trust and costs and has not been silently introduced.

## Ownership verification

Railway documents [OAuth/OIDC](https://docs.railway.com/integrations/oauth), [dynamic client registration](https://docs.railway.com/integrations/oauth/creating-an-app) and [selectively consented workspace/project scopes](https://docs.railway.com/integrations/oauth/scopes-and-user-consent). Sign-in identity alone does not establish ownership of this project. Client registration, exact HTTPS callback, PKCE/state, token verification and authoritative project-role checking must be validated together. No app was registered and no account token was requested in this pass.

The existing owner-controlled, expiring single-use setup credential therefore remains necessary. It is never embedded in a generated URL or printed in public logs. Removing it without a proven replacement would allow first-visitor ownership. The five-stage target remains unaccepted, including this visible hosting-dashboard action. Account MFA, saved single-use recovery codes and invitations retain their accepted implementation.
