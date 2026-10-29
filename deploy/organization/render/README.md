# Render deployment candidate — not a supported Deploy button

Render is P25's primary managed-provider candidate, not a verified beginner deployment. No provider resources, registry release, public template or billing operation have been created. `render.candidate.yaml` is a maintained provisioning specification for review. It deliberately leaves unresolved security inputs unset. It is **not production-ready** or an instruction for beginners to edit variables.

The paid application and paid PostgreSQL are the existing C# server and database. P17 received payloads/receipts already live transactionally in PostgreSQL. The 1 GB app disk is exclusively protected identity configuration, not an evidence archive. The final accepted container's source revision and local image identity are recorded in the P25 validation record; an immutable distribution image is a separate release action. `SOURCE_REVISION` is the Docker build argument, not a claimed provenance value invented at runtime.

Before a live candidate can be accepted, prove all of:

- A provider-validated blueprint referencing a pinned accepted source/container, exact current plan IDs and bill. No free/expiring database.
- A reachable PostgreSQL endpoint with hostname-verified TLS and network restrictions. Render explicitly documents that its **internal self-signed PostgreSQL endpoint does not support verify-full**. Its ordinary `fromDatabase.connectionString` is therefore intentionally not used. Do not weaken AeroScience to `Require` or trust-all certificates. External verified endpoints and narrow access rules need provider-account testing.
- A private writable configuration disk owned by non-root UID 1654. Its certificate and separate provider-generated password survive replacement. No shell step may be hidden behind a beginner claim.
- An exact HTTPS origin and a proven finite trusted-proxy address contract. Do not enable ASPNETCORE_FORWARDEDHEADERS_ENABLED or trust every proxy/network. Normal provider health checks must use the approved origin; the in-container loopback check is separate.
- Hosting owner can copy the generated `Organization__BootstrapSecret` in the provider's **private service environment dashboard**, paste it into **Setup code**, and rotate that value/redeploy if the 30-minute window expires. It is never in a URL, app response or public log. Access to the provider account is the ownership prerequisite. Successful setup atomically consumes it; restart cannot extend its lifetime.
- Live novice deployment, billing approval, MFA/recovery, independent backup/restore, retention and recoverable upgrades remain acceptance gates. P26 owns operational durability; P25 does not assert these passed.

Official sources and all-in cost assumptions are in [provider comparison](../../../docs/organization-hosting-providers.md). Organizations pay their own provider accounts. The developer has no mandatory central service or cross-organization bill.
