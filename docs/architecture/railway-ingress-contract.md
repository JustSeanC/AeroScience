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

The existing owner-controlled, expiring single-use setup credential remains necessary until a proven replacement exists. It is never embedded in a generated URL or printed in logs. Removing it would permit first-visitor ownership. The native graph seals its generated bootstrap value. Official [variable documentation](https://docs.railway.com/variables#sealed-variables) confirms sealed values cannot be viewed in the UI or retrieved through the API. Therefore retrieving that generated code from private deployment settings is **not a working owner flow**. Do not unseal or log it as a workaround. Secure owner-controlled delivery or an authenticated project-owner replacement remains a technical release blocker. Account MFA, saved single-use recovery codes and invitations retain their accepted implementation.


## Offline follow-up — architecture decision and P28 gates

Official documents were re-read on 2026-10-29, without account access. The [network specifications](https://docs.railway.com/networking/public-networking/specs-and-limits) require TLS/SNI at the edge and document HTTP-to-HTTPS redirects, but do not authenticate the final upstream socket to the app. [Private networking](https://docs.railway.com/networking/private-networking) encrypts traffic with WireGuard and isolates environments; other services in that environment can still address a service. Encryption/environment membership is not authorization of an individual gateway. The [TCP proxy](https://docs.railway.com/networking/tcp-proxy) supplies a generated hostname and assigned port; it does not establish browser-trusted certificate ownership by AeroScience. [Edge rules](https://docs.railway.com/networking/edge-rules) document allow/block/challenge/redirect/cache, not authenticated header injection or upstream client certificates.

| Architecture evaluated | What it can establish | Precise unresolved requirement |
| --- | --- | --- |
| Existing app behind native HTTP edge | Exact origin, cookies, CSRF and finite configured proxy checks remain available | Supported authenticated edge identity or exclusive finite ingress peers, including changes after restart/rotation |
| Dedicated public gateway; app private only; authenticated gateway-to-app hop | Can isolate the backend and reject other private peers if its keys and route are correctly bound | The public gateway must authenticate its incoming edge path too. Merely signing forwarded data at the gateway moves the same unresolved trust boundary |
| Trust private-network addresses / POP header / public self-probe | No proof of exclusive edge identity | Rejected; no wildcard trust, learned anonymous peers or header-only authentication |
| Terminate browser TLS through native TCP proxy | Potential end-to-end transport if a valid owned certificate and exact origin can be provisioned | Native generated hostname/certificate authority, assigned-port routing and renewal are not proven; manual DNS/certificates would violate the beginner path |
| Add an external tunnel/provider | Could introduce another authenticated transport | Adds accounts, cost and infrastructure; not selected or silently provisioned |

**Decision:** do not add a gateway or expose the backend on the strength of an unproven ingress assumption. A gateway is justified only if a supported edge-to-gateway boundary and protected key/rotation contract can be demonstrated. Offline negative configuration checks retain refusal of wildcard/CIDR/forwarding-header substitutes; existing real-backend tests retain invalid-host, forged forwarding, CSRF, role and restart protections. These tests prove existing boundaries, not real Railway routing.

The minimum provider contract still needed is: **every accepted ingress socket is attributable to the configured trusted edge/gateway identity, and non-edge project traffic cannot impersonate it**, across deploy, restart and rotation. Native origin and certificate ownership must also be verified. An observation of one peer address is insufficient. If Railway cannot establish this contract without weakened trust, its deployment path must remain blocked and be revised before P29.

Owner-confirmed timing: P26 performs feasible repository engineering and automated validation. P28 owns the real Railway project, networking/security negatives, independent external recovery, actual costs and five-stage novice acceptance. No owner login, account setup or charge approval is needed during routine development. The ASP.NET Core/PostgreSQL domain, Identity and research/evidence architecture remain intact; P27 can be developed separately against authorized local infrastructure without assuming a working public Railway deployment. This sequencing does not waive any release blocker.

Required P28 records: exact image/source/template identities; origin/TLS and private-only database; direct app/gateway/internal-peer refusal; spoofed headers/origins and cross-user/organization refusal; trusted-peer/key rotation and replacement; owner-only bootstrap delivery/replay/concurrency; independent account/key/object loss and restore; controlled failed provider update; ordinary-user stage count and real provider charges. Technical checks belong to engineering. The owner performs only the ordinary five-stage browser journey after those prerequisites are ready.
