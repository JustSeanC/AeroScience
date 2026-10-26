# Research Campaigns — optional self-hosting design (planned v0.7.0)

**Architecture planning only.** v0.3.0 implements no organizational server, campaign generator/evaluator, accounts, uploads or shared progress. Independent desktop installations remain offline-capable with no central AeroScience account or mandatory service. A future organization such as vNASA may choose to operate and pay for its own server; pilots explicitly select its URL/account and explicitly submit data.

## Proposed contracts and integration

- Versioned Organization, Campaign, Objective/TestCard, Assignment, EvidenceSubmission, Evaluation and Review records, all stable UUIDs, creation/revision UTC and schema versions. Objectives can be generated, manager-authored or hybrid; acceptance criteria record units, tolerances, allowed instruments, source quality and versioned evaluator policy.
- Flight-test example: ten altitude/airspeed/ambient-temperature/acquisition conditions for an F-15 evaluation, different pilots assigned different cards, campaign complete only after every required test has the defined approval. This is a future example, not a v0.3.0 validation claim.
- v0.3.0 `RemoteProductData` / `ScientificEvidenceContext` preserve aircraft identity/conditions, host receipt UTC, source observation/publication/processing dates, instruments/settings/mount, science mode, original plan snapshot, source hashes/grids/quality gaps and stable library/dataset/product IDs with relative references. `evidence.json` provides a compact starting point. Old observations remain immutable; future adapters migrate contracts without rewriting samples.
- Evidence references retained scientific files by stable ID/hash and declares which original samples are included. A reviewer can independently recompute only where sufficient source data/calibration/algorithms are available. Local hashes/pass claims do not prove a real simulated flight occurred. Missing proof/coverage is an explicit evaluation status, not assumed success.
- API exchanges campaign definitions/cards, compact submissions, versioned evaluations and progress. Continuous high-volume telemetry and automatic upload of scientific libraries are excluded. Large data attachments require separate explicit consent, licensing/size policy and resumable transfer design.

## Hosting, roles and security

Prefer one officially supported Docker Compose deployment: application/API, durable database, optional object storage, TLS reverse proxy and explicit backup volumes. Organizations control domain, member access, capacity and cost. Do not prescribe invented minimum RAM/CPU or call a non-existent Compose stack supported yet.

Proposed roles: owner/admin (host/security/membership), campaign manager (definitions/assignment), reviewer (evidence approval), pilot/contributor (own submissions), read-only observer. Apply least privilege and organization isolation on every object. Use maintained authentication, MFA/account recovery, invitation expiry, TLS, URL/server-identity confirmation, CSRF/session protections, audit trails, input/schema/archive validation, malware-safe attachments, quotas/rate limits, secret rotation and scoped credentials in OS secure storage. Prevent SSRF and untrusted server commands. Never reuse local dashboard pairing as organizational authentication.

## Offline synchronization proposal

Cache explicitly selected campaign definitions locally. Record locally and queue only **user-confirmed** submissions. Use idempotency keys, immutable evidence revisions, per-object versions/ETags, bounded retries, visible pending/conflict/rejection states and manual resolution for changed objectives. Never overwrite local archives to match an organization. Disconnecting an organization retains personal research; member removal and server shutdown need export/revocation procedures. Server approval can change independently of immutable local observations. No background automatic scientific uploads.

## Mandatory v0.7.0 Self-Hosting Guide deliverables

1. Beginner overview of server responsibilities and limits.
2. Measured minimum/typical requirements for small and larger organizations.
3. One supported, reproducible Docker Compose installation/configuration/commands.
4. Optional Windows host instructions **only if supported and tested**.
5. Domains, HTTPS/TLS and reverse-proxy setup.
6. Initial administrator, secure authentication and member invitations.
7. Desktop connection to a chosen organization URL/account.
8. Organization/campaign creation, generated objectives and manager test cards.
9. Roles, permissions and submission approvals.
10. Scheduled database/object backups and a **tested restore**.
11. Server upgrades, client compatibility, migrations and rollback.
12. Storage/upload quotas, monitoring and abuse protection.
13. Hardening, firewall, secrets and account recovery.
14. Health checks, logs and troubleshooting.
15. Data export, member removal, retention and permanent shutdown.
16. Clear operational costs and cost-reduction options.

A technically comfortable virtual-organization administrator must be able to follow that guide without reading/modifying source. These deliverables are release requirements for the eventual server; they are not fulfilled by this design document. Replay/coverage remains v0.4.0, advanced calibration v0.5.0, Navigraph v0.6.0, optional organizations v0.7.0 and full simulator science mapping v1.0.0.

## Owner-directed hosting simplicity requirement — 2026-10-23

**Beginner administration must be exceptionally simple**: installing/creating an organization should be feasible for an entirely nontechnical person through a guided workflow. No terminal/SSH, manual Compose/database/proxy/certificate commands, or required DNS customization in the **default path**. The current Compose-first hosting proposal remains the supported *advanced* self-host deployment concept, **not** the only beginner option. Reuse a single underlying ASP.NET Core organization backend, schemas, roles and privacy requirements in both deployments; avoid a second server implementation.

**Proposed (not yet accepted provider selection):** an official pinned-release **Deploy your organization** button using a managed application/database provider that generates a default HTTPS URL, with first-admin bootstrap, invitation-link sharing and health/updates/backup diagnostics entirely handled by a novice-oriented server portal. Research a provider's actual deploy-button persistence/production database support and pricing before claiming a truly one-click production setup. See detailed [v0.7.0-server-hosting-experience.md](v0.7.0-server-hosting-experience.md). An officially supported guided Linux VPS/Docker Compose option remains for those who want maximum infrastructure control.

**Queue deletion rule confirmed by owner:** If a pilot approves an offline submission and then permanently deletes its underlying local scientific evidence **before transmission**, hold/cancel the queued item; never preserve a stealth retained local copy or upload a substitute. The pilot has to review and authorize a valid replacement package. After actual upload, the recipient organization's separate submitted evidence is subject to disclosed server-side retention/backup/deletion rules, not a hidden *local* cache.

**Release gate:** unaided novice fresh-account cloud setup, secured admin initialization, invitation, campaign/claim, offline evidence submit/review, restart+backup+tested restore, update/rollback, and clear recurrent hosting charges. Do not invent a working deployment button or publish provider-specific commands until implementation is verified.

## Hosting default selected by owner — 2026-10-23

**Official one-click managed cloud deployment is the confirmed beginner default.** The existing Docker Compose/Linux self-hosting architecture remains the official advanced path; both launch the same independently operated organization backend. This is a *deployment-experience decision* only: no hosting provider, price, implementation or installer has been selected or verified. Hosting login/billing approval, MFA and first-admin bootstrap remain necessary user actions; everything technical should be configured automatically. Require real novice end-to-end testing and secure, durable evidence and database persistence with recoverable upgrades/backups before release. See [beginner hosting specification](v0.7.0-server-hosting-experience.md).
