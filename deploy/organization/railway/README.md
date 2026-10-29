# Railway-first native deployment candidate — P26

**Deployment Candidate. Not a published one-click template or production distribution.** No paid resource, Railway account login, template slug or deploy URL is created by these files. The beginner chooser deliberately offers **Review Railway Candidate**, not a working **Deploy with Railway** button. Fresh-account novice acceptance of five stages has not occurred.

The owner-approved journey is: create server; authenticate with Railway; approve actual charges; secure ownership/MFA/recovery; invite pilots. No terminal/DB/TLS/DNS/infrastructure-secret-file tasks may become hidden beginner prerequisites. The current private-dashboard one-time owner-code retrieval and independent recovery setup are visible unfinished actions within the intended security stage. Do not call the target achieved.

## Native composer recipe, not invented import schema

`template.candidate.json` is AeroScience's validated recipe for Railway's official native template composer. It is **not** a provider importable JSON schema. Official documentation supports creating a reusable template from scratch, adding container services, reference variables, generated `${{secret(...)}}` values, public networking and volumes. Creating/publishing the real template still requires an authenticated provider owner and accepted immutable images. Do not fabricate a template ID or bypass account/billing consent.

The PostgreSQL image extends the actual pinned Railway PostgreSQL 17 SSL image. On first initialization it creates a nonsuperuser application/database-owner role and a separate nonsuperuser CREATEDB recovery provisioner. The application never receives the bootstrap-owner password. Native references supply separate generated application, recovery and trust credentials. SQL has no public network port. Authorized evidence remains in PostgreSQL; there is no extra evidence archive service.

The application/operations image is the same `AeroScience.Organization` backend plus pinned native PostgreSQL/restic tools. The Railway startup profile handles the provider's root-owned volume with a short fixed-path initializer and **permanently drops to UID/GID 1654 before serving**. It does not run an unrestricted root application. The normal base and recovery images remain `USER 1654:1654`.

## Database certificate trust actually investigated

Official `railwayapp-templates/postgres-ssl` source inspected at `09ff77dca25f9af407827b9f27c7de9c5c660790`; runtime base index digest `5e806d28c18198f933d25e79989470a47a938f465bf0634e030b416961b3463e`. Its current initializer creates a private CA and SANs `localhost` plus `RAILWAY_PRIVATE_DOMAIN`, when supplied. “SSL-enabled” alone is not VerifyFull trust. CA renewal can replace that trust anchor; do not assume a public CA or a permanently static certificate.

`database-trust.py` is a bounded private public-certificate attestation helper in the PostgreSQL container, not a second organization backend. It sends **only the public root certificate**, authenticated by HMAC/SHA-256 over a fresh nonce and exact private hostname. The app validates that attestation before installing the CA, then connects with ordinary Npgsql **VerifyFull** to the exact private DNS name. It never disables certificate checks, accepts an arbitrary URL, retrieves private CA keys or silently pins an anonymous first response. The helper supports IPv4/IPv6 private networking. Native template secrets are independent of database/owner/key credentials.

Local acceptance uses the real pinned PostgreSQL image, proves hostname acceptance/wrong-host rejection and the restricted application process. Live Railway DNS, native secret references, CA renewal/reconnect and provider volume behavior still need acceptance.

## Ingress remains fail-closed

Railway terminates public TLS and forwards to the container. Its informational `X-Railway-Edge` POP identifier is not cryptographic proof or a stable trusted-proxy allowlist. No verified finite ingress trust contract has yet been established. The adapter refuses to serve without explicit finite trusted proxies rather than inventing them, trusting all peers or honoring arbitrary forwarded headers.

The local runtime fixture uses an explicit loopback test proxy entry solely to exercise startup/private TLS; it is not a deployable Railway proxy configuration. Native `/healthz/ready` is status-only and honors the documented `healthcheck.railway.app` Host only in Railway mode; it cannot bootstrap or read evidence. No public-origin relaxation is used for other endpoints.

## Recovery and price

The optional operations image supports authenticated encrypted restic snapshots paired with protected identity. Native variables can generate/persist a private repository key and reference scoped storage credentials without asking a beginner to generate files. Current Railway Buckets use virtual-hosted S3 addressing; `dns` is used for their modern endpoint. Original authorized evidence stays in PostgreSQL; a recovery Bucket holds only encrypted organizational backups.

Buckets alone do not satisfy project/account-loss recovery. They currently lack object locks/versioning/lifecycle/server-side encryption; use client encryption and an independently controlled recovery copy/key. Native inclusion of all required recovery resources in the actual reusable template, credential recovery, independent copy, retention and full restore/rollout are still gates. See [recovery architecture](../../../docs/architecture/organization-recovery.md).

Official current components (checked 2026-10-29): Hobby $5 minimum credited toward metered usage; RAM $10/GB-month, CPU $20/vCPU-month, volumes $0.15/GB-month, service egress $0.05/GB; Buckets $0.015/GB-month. Assumptions of 0.75–1 GB combined memory, 0.05–0.1 average vCPU, 5 GB volumes, 5 GB backups and 1 GB egress imply roughly **$9–13/month**, plus independently controlled recovery storage. This is not a measured bill/cap. Heavier workloads/Pro ($20 minimum) can exceed $20. Each organization pays Railway directly; AeroScience's developer need not pay for other organizations.

Official references: [native templates](https://docs.railway.com/templates/create), [variables](https://docs.railway.com/variables), [volumes/non-root ownership](https://docs.railway.com/volumes), [PostgreSQL template source](https://github.com/railwayapp-templates/postgres-ssl), [edge behavior](https://docs.railway.com/networking/edge-networking), [health checks](https://docs.railway.com/deployments/healthchecks), [Buckets](https://docs.railway.com/storage-buckets), [prices](https://docs.railway.com/pricing/plans).
