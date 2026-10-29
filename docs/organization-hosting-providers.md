# Organization hosting providers — P25 research

Official documentation retrieved **2026-10-29 UTC**. This compares independent organization-owned accounts, not a central AeroScience service. No provider credentials, paid resources, public deployment or registry distribution were used. Every organization pays its own provider; the developer has no required per-organization hosting bill. Personal Research remains offline and independent.

## Actual readiness

| Provider | Status and next action | Reason |
| --- | --- | --- |
| Render | **Deployment Candidate** — Review Render Candidate | Maintained candidate Blueprint; existing .NET server container, paid managed PostgreSQL, generated secrets, persistent protected configuration. No live accepted template/button. |
| Railway | **Guided Setup** — Review Railway Requirements | Multi-service templates and volume-backed SSL-enabled Postgres are promising. AeroScience has not implemented/accepted a Railway provisioning template, hostname trust, protected certificate persistence or restore. |
| DigitalOcean | **Guided Setup** — Review DigitalOcean Requirements | App Platform can run the shared container; a production Managed Database needs separate provisioning. The standard Deploy button supports only **Dev Databases**, not a production managed DB. No accepted AeroScience adapter. |
| Advanced Compose | Advanced operator candidate | Same app, private verified-TLS PostgreSQL, HTTPS proxy, protected configuration and persistent volumes. Real local restart/replacement tested; not the beginner path. |
| Cloudflare | Research only | Workers/D1 are not ASP.NET Core/PostgreSQL. Tunnel does not supply compute/database. No backend fork, free-hosting claim or deploy action. |

No option says **Ready for Verified Deployment**, exposes **Deploy Now**, creates resources or authorizes charges. One managed candidate is implemented, rather than three independent deployment systems. The same organization assembly, PostgreSQL migrations, Identity, evidence, audit and authorization contracts remain authoritative.

## Cost assumptions (USD/month, excluding tax and optional domains)

| Component | Render | Railway | DigitalOcean App Platform |
| --- | --- | --- | --- |
| App compute | $7 `0.5c-512mb` listed starting paid web service | Metered CPU and memory for app **and** database | From $5 app |
| Database | $6 `0.1c-256mb`, 256 MB; smallest plan **not capacity accepted** | PostgreSQL service CPU/memory usage plus volume | Managed PostgreSQL compute from $15.15 |
| Evidence | In P17's paid PostgreSQL, **not an additional attachment bucket** | In PostgreSQL volume, counted in storage/billing | In Managed PostgreSQL, counted in storage/billing |
| Storage/configuration | PostgreSQL includes 1 GB; additional $0.30/GB. 1 GB app configuration disk $0.25 | Volume/storage and backup usage extra | DB storage listed $0.215/GiB/month, minimum 10 GiB adds about $2.15; protected certificate storage still needs an accepted provider path |
| Provider subscription | Hobby $0 + compute, positioned for prototypes; Pro $25 + compute may be appropriate for team production | Hobby minimum $5 includes $5 usage, **not $5 plus all usage**; Pro $20 minimum. Production suitability/limits require review | Selected App Platform/DB charges; confirm account/plan specifics |
| Backup/recovery | Paid DB offers logical backups/PITR under its retention terms. Configuration disk snapshots do **not** prove independent restore | Volume backups/PITR vary; enable, cost and independently restore before acceptance | Managed database backup terms and restore must be verified; additional retention/storage may cost more |
| Bandwidth | Hobby includes 5 GB according to current pricing; overages and outbound traffic depend on plan | Metered egress; inspect current resource table (not a hard cap) | Included allowance and additional outbound usage depend on selected app tier |
| Indicative total | **About $13.25** prototype base; **$26.25** with the $19/1 GB DB, or **$38.25** adding Pro workspace to the smallest pair, before overages/backups outside included retention | **No measured all-in quote**. $5–15 is only a target; always-on memory, CPU, evidence, backups and overages may exceed $20 | **About $22.30** minimum assumption ($5 + $15.15 + $2.15), before remaining configuration/recovery/bandwidth costs |

The $5–15 goal is not a guarantee. An under-$10 Render configuration cannot pay even the listed $7 app + $6 durable DB. Free/expiring databases are unsuitable. More than $20 may be necessary for database headroom, team access and recovery; do not suppress those costs or claim the 256 MB plan is enough. Local container measurements are synthetic-workload observations, not a representative organization benchmark, availability promise or provider bill.

## Why Render is the primary candidate, and what blocks it

Render offers paid managed PostgreSQL, a declarative Blueprint, generated secrets, HTTPS and protected disk provisioning without another AeroScience backend or account system. This is a stronger foundation than treating a cheap web instance as a complete research service.

A critical official constraint was found: **Render internal PostgreSQL uses self-signed TLS and does not support `verify-ca`/`verify-full`.** AeroScience requires `SslMode=VerifyFull`; the ordinary `fromDatabase.connectionString` is not safe to substitute. The candidate deliberately leaves its exact verified database endpoint/access rules, public origin and finite trusted proxy addresses unresolved. The paid DB starts with **no public allowlist**, rather than an open database. A provider-account integration must prove an appropriate hostname-verified/restricted endpoint, exact edge proxy behavior, non-root private disk ownership, accepted pinned source/image and dashboard-owned bootstrap. This is not yet a terminal-free deployment. No working button is advertised.

The candidate's provider-generated owner secret can be obtained only from the hosting owner's private service environment dashboard. It is pasted into **Setup code**, expires after 30 minutes and is atomically consumed. Rotating that specific private value and redeploying before initialization deliberately creates a new window; restarting with the same value does not extend it. No secret URL, log output or anonymous-first-visitor ownership is used.

## Official sources

- [Render pricing](https://render.com/pricing), [Blueprint specification](https://render.com/docs/blueprint-spec), [PostgreSQL connections and TLS limitations](https://render.com/docs/postgresql-creating-connecting), [database recovery/backups](https://render.com/docs/postgresql-backups), [persistent disks](https://render.com/docs/disks), [web services](https://render.com/docs/web-services).
- [Railway pricing](https://railway.com/pricing), [plans/resource prices](https://docs.railway.com/pricing/plans), [PostgreSQL](https://docs.railway.com/databases/postgresql), [templates](https://docs.railway.com/templates/publish-and-share), [volume backups](https://docs.railway.com/volumes/backups).
- [DigitalOcean App Platform pricing](https://www.digitalocean.com/pricing/app-platform), [Managed Database pricing](https://www.digitalocean.com/pricing/managed-databases), [Deploy button restrictions](https://docs.digitalocean.com/products/app-platform/how-to/add-deploy-do-button/), [database management](https://docs.digitalocean.com/products/app-platform/how-to/manage-databases/).
- [Supported .NET lifecycle](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core). Container uses the repository's .NET 10 LTS SDK/runtime patch, locked NuGet dependencies and digest-pinned official bases. Updates require reacceptance; a pin is not a promise of permanent security.

P26 owns operational backup/restore, retention, recoverable updates and administrator-security acceptance. Live-cloud/billing and owner-operated novice deployment are separate explicitly authorized actions, not implied by P25's local technical validation.
