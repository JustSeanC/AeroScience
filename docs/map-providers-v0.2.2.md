# v0.2.2 basemap provider decision and external route conventions

Research date: 2026-10-17. Prices and account conditions can change; these are observed public prices, not a guaranteed free service or an account entitlement. No AeroScience developer token or shared paid quota is distributed.

| Candidate | Official information retrieved | Decision |
| --- | --- | --- |
| Esri ArcGIS Location Platform | Pricing page advertises 2 million basemap tiles free monthly, then $0.15/1,000 under tile-based usage. Basemap sessions have a separate allowance/pricing model. API key access requires an account, appropriate privileges, attribution and account terms. | Selected, using each user's own restricted public client key and the documented authenticated Imagery style to discover its authorized raster source. Satellite + Labels adds the documented static imagery-reference layer. |
| Mapbox | Pricing page advertises 750,000 raster tile requests free monthly, then $0.25/1,000 in the first paid band. Raster API supports satellite, public tokens, attribution, URL restrictions, 401/403/429 errors and HTTP caching. | Viable alternative; not implemented in this release. A second provider is not needed for the core experience. |
| MapTiler | Current pricing describes Free as testing/PoC/prototyping/personal/non-commercial use, with logo requirements; Flex starts at $30/month. | Not chosen for a general public distribution whose future uses may exceed those restrictions. |

Official sources consulted:

- https://location.arcgis.com/pricing/
- https://developers.arcgis.com/rest/basemap-styles/
- https://developers.arcgis.com/rest/static-basemap-tiles/
- https://developers.arcgis.com/rest/static-basemap-tiles/arcgis-imagery-labels-tile-get/
- https://developers.arcgis.com/rest/static-basemap-tiles/types/
- https://developers.arcgis.com/documentation/security-and-authentication/api-key-authentication/
- https://developers.arcgis.com/documentation/esri-and-data-attribution/
- https://www.esri.com/en-us/legal/terms/master-agreement
- https://www.mapbox.com/pricing
- https://docs.mapbox.com/api/maps/raster-tiles/
- https://www.maptiler.com/cloud/pricing/
- https://operations.osmfoundation.org/policies/tiles/

The Esri attribution page is a client-rendered shell; its official page component was also retrieved and explicitly requires manually displaying Esri attribution and service metadata `copyrightText` for manual basemap integrations. The page's capabilities table supports distributing web/native/open-source API applications with the listed account types. The holder must review their applicable Esri agreement and current attribution requirements. AeroScience shows the raster source's supplied attribution (falling back to the authorized source's MapServer `copyrightText` metadata), Powered by Esri/logo, and the reference-layer credits. A provider response without attribution is rejected instead of displaying uncredited imagery. Actual live attribution/imagery verification requires a configured account and is pending.

## Configuration and privacy

In dashboard Settings → Maps & Basemaps on the Windows companion, review the privacy/billing notice, enable online maps, supply a **public client** API key with minimum required basemap privileges, select a preferred layer, save, then test the saved satellite connection. Never use privileged administrative/server credentials. Provider selection is currently Esri only. Offline and Streets require no satellite key. Provider configuration is restricted to authenticated loopback; authorized paired displays can use the public client token and select a previously enabled layer.

Keys are encrypted at rest with Windows DPAPI CurrentUser in the stable configuration directory's `maps.json`. They are not stored inside either movable database/library or the replaceable installation. Current-user DPAPI means Windows-profile transfers can require re-entering a key. No plaintext fallback is used. Authorized browser clients and the tile provider can see the public token in tile requests; this is not a promise of secret browser credentials. Configure account restrictions for the actual localhost/LAN origins, and test them. Referrer policy sends only the origin cross-site, compatible with provider referrer restrictions and the OSM tile policy.

No keys are included in logs, feedback collection, source or release inputs. Release validation rejects `maps.json`. Online imagery can be billable, and labels add requests. Set provider-side quotas, alerts and other account controls; a local request counter cannot enforce billing limits.

Online requests disclose IP, viewed geographic areas and normal browser/HTTP information. No scientific observations, full recorded mission or private notes are submitted. Offline is the fresh-install default. Selecting Offline removes tile layers/logo requests; there is no periodic provider test, prefetch, service worker, satellite download cache or bulk-area download. Ordinary browser/provider HTTP caching is used. In-flight recording and canonical geometry do not depend on a tile service.

The native toolbar deliberately uses a lightweight offline canvas map, not online tiles or a second transport. It reuses the bundled Natural Earth coordinates and shared geographic display helpers. Real Coherent/Quest acceptance remains pending.

## Official EFB PLN

Source: https://docs.flightsimulator.com/msfs2024/retail/content-configuration/mission-xml-files/efb-flight-plan-xml-pln-file-properties/

The current EFB format uses `SimBase.Document/FlightPlan.FlightPlan`, required Title/Descr/FPType/CruisingAlt/AppVersion with AppVersionMajor **12 or greater**. DepartureID/DestinationID are optional (maximum eight characters); omitted airports start/end at the first/last en-route waypoint. AeroScience does not invent airports. Descriptive airport names require a valid identifier or removal before PLN export. Users must supply cruising altitude. Each ordered science fix is a User ATCWaypoint with WorldPosition and a unique eight-character AS000001-style ICAOIdent. Coordinates use hemisphere/DMS notation; optional altitude is signed feet and is omitted when unspecified. No legacy mission/activity template or survey-pattern XML extension is used.

All original vertices remain in order. More than 100 points triggers explicit acknowledgement, a usability threshold rather than a claimed EFB limit. There is no automatic simplification, densification, or synthetic timestamp. EFB/FMS limits and import behavior are **pending actual MSFS testing**, including local/closed/helicopter plans. XML tests do not establish EFB acceptance. The original science plan and immutable historical snapshots retain metadata not representable in PLN.

## SimBrief, ICAO and VATSIM

Official SimBrief guide retrieved: https://dispatch.simbrief.com/guide/flightoptions/route

The guide explicitly documents custom coordinates such as `59N140W` and more precise `591730N1403212W`, plus DCT routing. The SimBrief option uses degrees/minutes/seconds to one arcsecond (up to about 15.5 m rounding per axis), preserves order and never submits a plan. Actual input/generation/FMS acceptance has not been tested with an authenticated SimBrief account. Long/local survey routes may be altered or rejected by external planners; AeroScience does not claim universal acceptance.

ICAO Item 15-style DDMMNDDDMME coordinates are separately labeled, rounded to whole minutes, and warned as unsuitable for precise survey reproduction. DD/DDM/DMS lists retain substantially more precision. Named sequences require an explicitly entered navigation identifier on every point; a field's presence does not verify it against a navigation database. Proprietary science names are never silently promoted to published fixes.

The VATSIM learning site and VATSIM UK documentation returned HTTP 403 to this cloud client. No live VATSIM filing was tested, and no filing/integration is implemented. Verify current regional/ATC conventions and operational suitability before use. A generated coordinate string is not an ATC clearance or a guarantee of accepted routing.
