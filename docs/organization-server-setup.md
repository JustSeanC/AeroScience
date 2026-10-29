# Organization server setup — P25

Open **My Research → Organizations → Create an Organization Server**. The same hosting page is available as `/organization-hosting.html` in the organization portal. Review the three providers, their complete cost assumptions and actual readiness. **No provider is currently Ready for Verified Deployment.** The page cannot create a paid account or authorize billing. Personal research does not need a server.

After a legitimate hosting owner has provisioned a candidate, **Setup code** requires their protected one-time code. For the managed candidate the owner obtains it from their provider's **private service environment settings**, `Organization__BootstrapSecret`. They paste it into the password-style input, name the organization and create a strong administrator account. Never share the code publicly or put it in a URL. A first visitor without it cannot take ownership. The default managed window is 30 minutes, persisted in PostgreSQL; restarts do not reset it. Only an explicit hosting-owner secret rotation can renew an unused expired window. Successful setup consumes it transactionally with the administrator and organization.

Sign in, then open **Organization Setup**:

1. **Secure Your Organization** explains the saved account and remaining MFA/recovery requirement.
2. **Your Organization** shows its saved identity and exact verified HTTPS address.
3. **Start Research** opens **Create First Program** or **Open Research Programs**. Use the existing **Create Campaign** editor and normal publication controls.
4. **Invite Pilots** opens **Members → Create Invitation**. Choose an expiry, select **Create Invitation Link**, then **Copy Invitation Link**. Share it yourself through any messaging method. No SMTP account is needed.
5. **Connect AeroScience** explains the existing matching-code browser approval. **Open Management Portal** returns to the same portal; no second account system is created.

**Server Health** is active-administrator-only, including its API. **Check Server Health** runs bounded real database/schema, TLS, received-storage and setup checks. It displays application/source version, the public origin, the check time and honest persistence/protection status. It never shows connection strings, certificate paths/passwords, bootstrap codes, member secrets or database internals. A manager/reviewer/pilot is not granted diagnostics authority just by seeing an organization page.

**Deployment Starts** means the app launched. **Deployment Healthy** means relevant application/storage checks passed. **Production Ready** remains **No** until live-provider, administrator MFA/recovery, independent backup/restore, retention and recoverable upgrade acceptance pass. The page does not contain a switch to claim these passed.

The server receives only explicitly selected and authorized P17 packages. Hosting does not access a pilot's private archive, start telemetry streaming or expand a read-only connection. A received remote copy is independent of the local original and follows the organization's disclosed retention policy. Local deletion before delivery still holds/cancels a queued transfer; there is no hidden deployment cache.

No owner terminal or database acceptance checklist is required for P25. [Provider research](organization-hosting-providers.md), [security reference](architecture/organization-deployment.md), and the [advanced operator path](../deploy/organization/self-hosted/README.md) identify technical gates without pretending they are beginner steps.
