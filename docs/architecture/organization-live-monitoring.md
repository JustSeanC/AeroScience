# P27 live monitoring and typed organization events

## Reuse and authority

One existing ASP.NET Core organization application, PostgreSQL database, Identity/membership ledger, scoped browser-approved desktop connection, OS vault, MissionCoordinator, MissionStore and scientific-session controller remain authoritative. No new recorder, scientific archive, raw telemetry database, organization identity system or evaluator exists.

The relay reads the actual active local flight. A bounded `OperatingIntelligence(includeHistory:false)` projection omits session history/actions; `LiveResearchCards` omits historical evaluation/detection scans. At most the existing 16 frozen flight cards/open attempts are read, eight organizational statuses are shared, and previously observed attempt IDs use original indexed lookups. Network work runs in a separate periodic worker, outside the recorder critical path. No active consent means no organization calls or scans from that worker.

Location, source/UTC and flight identity are authenticated **pilot/device-reported current state**, not remote attestation that MSFS or an aircraft produced truthful data. Scientific acceptance remains exclusively in P17–P21's explicit received-evidence/review contracts.

## Transport and resource bounds

Bounded HTTP snapshots and five-second authenticated polling avoid long-lived subscriptions whose initial authentication could outlive membership. Every read/write revalidates after the original PostgreSQL membership lock. Writers require the new `ShareLiveFlight` grant, consent version/revision, exact pilot/grant/organization/flight, policy revision and expiry. Native readers require that separate live grant; previous read/claim/submit grants remain unchanged. All active roles can instead use the cookie-authenticated portal.

Limits: one server application instance; 64 active shared sessions; four independently consented destinations per Engine; 16 KiB permitted update; eight card and eight known instrument summaries; 32 concurrent organization HTTP requests under the existing global limiter; no limiter queue; 1,000 writes/minute and 600 reads/minute per observed IP. The latter corresponds to roughly 50 five-second viewers behind one IP before other live requests. These are bounded processing limits, not lifetime caps on retained audit/event records. Multiple application replicas/distributed presence are not supported by this checkpoint.

Faster writes coalesce without retaining a backlog. Accepted sequence and one current-payload hash persist as anti-replay metadata, including after position expiration; retries never refresh an old position's age. The hash is cleared when consent ends. Current coordinates, aircraft payloads and numerical values exist only in bounded process memory, never consent/audit/event rows. Positions expire at 30 seconds, stale begins at 15 seconds, inactive consent expires at 120 seconds and maximum consent is eight hours or credential expiry. Worker cleanup and reads enforce expiry; restart changes the instance identity and refuses old sharing sessions.

The frontend clears failed/hidden views, expires its own current DTO/markers, and resets a selected expired map view. Freshness conservatively includes the full request duration, so delayed responses cannot renew expired positions; responses completed while hidden are discarded. This uses monotonic local elapsed time without assuming synchronized client/server clocks. It uses the existing bundled Leaflet renderer with an offline reference grid, no track lines or invented sensor geometry. No browser storage or raw-evidence cache is introduced.

## Consent and restrictions

Sharing is off by default. Start binds the exact organization/flight and disclosed content/audience; there is no consent inheritance to a later sortie. A second organization needs independent explicit approval. Stop cancels the current send locally before waiting for server confirmation. A network failure never creates an offline live-data queue. A disconnected server may retain the last point until its 30-second TTL, which is disclosed rather than called immediate remote erasure.

Membership/stamp/grant expiry, release/suspension of associated claims, policy revision, sensitive-campaign restrictions and restart invalidate sharing. Read and write authorization is inside the same membership transaction boundary as revocation. Cookie/native Origin/CSRF separation, strict source JSON, exact public origin, TLS, existing rate limits and OS-vault protections remain intact.

Sensitive-campaign enforcement checks **all active restricted claims held by that pilot**, including untagged flight requests. This is intentionally broader than a self-declared flight tag; the server cannot inspect private unsubmitted archives to prove what flight the pilot is conducting. Claims from another organization are never queried or leaked.

Card IDs must match the existing deterministic P16 local identity for the exact issued claim/hash; revisions and point IDs are checked against the frozen publication. Display names come from the server, not caller assertions. Values are finite, unit-specific, bounded and UTC-qualified. Unsupported, paused, stale and missing inputs cannot become fresh positions or scientific passes.

## Durable event boundary

`OrganizationEvents.Add` is called only inside authoritative domain transitions and their existing transaction. Stable namespaced event IDs and unique idempotency keys survive command retries, projection rebuild and restart. Events are append-only; metadata uses indexed keyset pages of 50. There is no arbitrary event-publication endpoint.

Implemented sources: explicit sharing start/end; authenticated reports of an original local recorded/evaluated attempt; P17 actual received receipts; P18 original requirement findings/requests/rejections; P19 verified complete-card approved contribution publication/supersession; P21 documented exception decisions. Local reports never assert received/approved science. A point finding never promotes an entire multi-card package. Public approved-card events require the same hash-verified complete-card P19 projection; ordinary members receive no raw evidence, notes or account names. Actor IDs are redacted in other members' public views; contributor/reviewer records retain exact internal identities and campaign authorization. The reserved pilot-marked-completion type does not claim a new manual completion feature exists.

Private receipt/review/exception events require contributor, actor or authorized campaign reviewer/manager access. Membership is current on every page. Live events have no coordinates; permanently retained audit metadata cannot reconstruct a route. Actual evidence remains solely in the original explicitly authorized P17 storage.

## Historical A/B boundary — superseded by P27-C integration below

No webhook configuration, outbound delivery or secret is implemented in the A/B checkpoint. Discord is explicitly isolated per the owner's allowed staged integration. During the original A/B pass, both official documentation hosts returned HTTP 403 (older developer endpoint: error 1010), and the official source-document requests were unavailable during this pass. This is not proof of Discord API acceptance or a reason to invent an unsafe HTTP contract.

The follow-up must verify current official execution/allowed-mentions/rate-limit contracts, protect destination credentials with existing organization key material, validate public HTTPS targets against rebinding/redirect/private-address attacks, bind independent pilot external disclosure to destination/policy revisions, and implement a bounded restart-safe outbox. Lost acknowledgement cannot be silently treated as safe permission to duplicate an external message. Organization visibility alone never authorizes external science disclosure. Tests must cover revocation, rotation, stale consent, terminal/unknown outcomes, rate limits, duplicate commands and no secret logging. Do not expose nominal working buttons until this acceptance gate passes.

## Hosting and release gates

The same unchanged P26 blockers remain: authenticated finite Railway ingress and secure owner-bootstrap delivery, real native provisioning, independent external disaster recovery/updates, actual costs and fresh-account five-stage novice acceptance. P27 local fixtures waive none of them. No paid resources, public images/templates, release or production installer is authorized here.

Official Discord webhook, message and rate-limit documents were successfully retrieved in the A/B closure follow-up. The original refusal remains historical evidence; current documentation access is available. This does not constitute a completed outbound implementation or provider acceptance.


## P27-C integrated delivery

The previously absent C implementation now uses the existing server, PostgreSQL, Identity, key ring, event identities and portal. [Discord notification architecture](organization-discord-notifications.md) describes settings, separate external flight consent, explicit current approval/exception disclosure, encrypted credentials, pinned verified HTTPS, bounded durable processing and honest ambiguous outcomes. [P27-C validation](../validation/v0.7.0-p27c.md) is authoritative for its own final acceptance; A/B Full is preserved independently. No P26 release gate is waived.


Combined P27 engineering acceptance: source `62e564c2627963f117b439c5477c45cf3aa341f5`, [Full 41010892379](https://github.com/JustSeanC/AeroScience/actions/runs/41010892379). A/B and C are technically accepted; real Discord/MSFS/hosting and all P26 operational gates remain separate. [Final evidence](../validation/v0.7.0-p27c.md).
