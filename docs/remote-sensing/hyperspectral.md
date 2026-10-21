# Hyperspectral Imager

## NASA catalog browsing (0.5.0 development)

Open **Scientific Data Library → Browse & Download Data → Data family → Hyperspectral**.
Choose AVIRIS-Classic, AVIRIS-NG or EMIT, select coordinates/viewport/rectangle/polygon or
an existing regional boundary, adjust dates and optional flightline filter, then search.
NASA CMR returns bounded pages of actual granules, source dates and original footprints.
The map distinguishes these remote outlines from verified retained local data. Neither
outline guarantees valid pixels. Offline cached searches retain their original as-of time.

Classic Collection `AVIRIS-Classic_L2_Reflectance_2154` V1 and NG
`AVIRIS-NG_L2_Reflectance_2110` V1 use family-specific original ENVI companions.
`EMITL2ARFL` V002 is **discovery / official access only**: native NetCDF-4 import is
unsupported. Current collection `C4079844428-LPCLOUD` V002 was verified directly in CMR; earlier V001 evidence remains historical and is not silently substituted. Missing spectral/grid metadata stays Unknown until original-file inspection.
### Earthdata Download & Import (P19A development)

1. On the companion PC choose **Connect Earthdata on this PC**. The native dialog opens
   [NASA's official profile](https://urs.earthdata.nasa.gov/profile). Sign in there and use
   **Generate Token**. Approve ORNL DAAC access on NASA's site when requested.
2. Paste the NASA **User Token**, not a password, into AeroScience's masked native field
   and save. Windows Credential Manager protects it for the current Windows user and
   configuration profile; production and Validation credentials have separate identities.
3. Select **Preview NASA Download & Import** on the exact Classic/NG scene. AeroScience
   verifies CMR identities, finds actual matching companions (NG files may be separate
   granules), and checks exact provider file lengths. No original cube is downloaded yet.
4. Review the original `.hdr`/`.bin`, advertised SHA-256 companions, dates, full-file bytes,
   storage allowance and approved transfer cap. Choose **Download & Import**.
5. The existing Download Manager reports actual durable work. After numeric/header/hash
   verification, the source is atomically published in the same Library used by manual
   imports. Original spectral values, channel mapping, masks, units and georeferencing
   are preserved; downloading a scene creates no flight science or grading credit.

Subsequent scenes reuse the saved connection until NASA expires/revokes it. NASA currently
documents a 60-day User Token lifetime, not permanent login. **Disconnect Earthdata on this
PC** removes this profile's local credential immediately, then cancels/drains active NASA
work and pauses its unfinished jobs. Credential removal still works with the Library drive
offline; unrelated provider transfers and retained science are preserved.
Revoke the token on NASA's profile page if required; local disconnection does not revoke
it server-side. AeroScience never asks for NASA passwords, collects browser cookies,
registers an invented OAuth client, operates an authentication proxy, or exports the token.
Paired browsers receive connection status and may operate approved downloads; they never
receive the token or manage its native secret field. Unrelated AeroScience features remain
usable without an Earthdata account.

These are complete original distributions, **not geographic subsets**. The independent
NASA original-scene cap defaults to 1 GiB (configurable 1 MiB–20 GiB). The existing automatic
subset default remains 64 MiB. NASA job approval and the preflight allowance must also fit;
metadata, retries and crash-uncertain reads count against them. Increase only explicit
limits you intend to authorize. Temporary storage must accommodate staging plus the
retained original copy. No original, protected source or completed product is deleted
to make room. Active flight acquisition preempts this background Library work.

Safe resumption requires provider byte ranges, a strong ETag and matching exact length.
Streaming prefix hashes and durable checkpoints protect partial data across interruption.
Unsupported ranges restart safely; changed identity requires a fresh preview. URLs from
CMR are restricted to the known ORNL original paths. The currently observed ORNL CDN
distribution/path is pinned, receives no bearer header and is held only in memory; a
provider routing change fails closed with browser fallback. Signed URLs are never saved
in jobs, source metadata or exports. Archives/JP2/NetCDF are not unpacked or converted.

The [P19A evidence](../research/v0.5.0-aviris-direct-acquisition.json) records genuine CMR
companion identities and public checksum access. **It does not establish protected numeric
downloads/imports.** No authorized Earthdata credential is available in the cloud task.
Classic and NG each still require owner-authorized original-file acquisition, independent
spectral/numeric comparison and offline reopening; native installed/device acceptance is
separate from deterministic synthetic tests and the cloud Windows vault probe.

### Browser/manual fallback

Choose **Open official NASA access / Earthdata** when direct access is unavailable or the
distribution is unsupported. NASA handles account login, permissions, terms and downloading.

After downloading, keep the matching reflectance `.hdr` and binary together and choose
**Verify downloaded AVIRIS files on this PC**. The exact granule/collection revision is
rechecked before opening the existing import wizard. Canceling NASA login/download or the
file picker leaves the Library unchanged. Removed/revised scenes require a new search.
Paired browsers can discover and open official access; trusted local-file selection remains
on the companion PC. Browser transfers are not managed Library jobs, so their budgets,
pause/resume and progress are controlled by the browser/provider. Check disk space before
downloading; AeroScience enforces Library limits again at import. No flight is created.
The catalog revalidates the official collection version and never substitutes another product.

[Live catalog evidence](../research/v0.5.0-nasa-validation.json) distinguishes metadata
acceptance from family-specific original-file numerical verification and installed testing.

## AVIRIS-Classic original-file verification

The assistant supports the documented V1 **224-band Float32/Float64 Level-2 reflectance**
ENVI `.hdr` + `.bin` distribution (`fYYMMDDtNNpNNrNN_corr/rfl/refl…_img`). Choose the
reflectance pair, not the three-band water-vapor `h2o` product or Level-1 radiance.
Keep original names. Wavelengths, bad-band flags, nodata, scale/gain/offset, interleave,
byte order and supported WGS84 geographic/rotated UTM grid come from the header.
Conflicting dates, units, dimensions, source stems or non-overlapping geography are rejected.
Missing wavelengths or georeferences are never guessed. Other revisions remain unsupported
until verified; generic advanced ENVI import is preserved.

Download any advertised `.sha256` companion too. AeroScience streams checksum validation
and retains the checksum with the originals; a locally selected sidecar alone does not
prove NASA authenticity, so compare it with the official provider. Originals are copied,
hashed and atomically published through the existing Library importer. Exact same-source
imports reuse the verified copy. Imported data and metadata reopen offline; importing a
historical source does not create a flight, acquisition event or grading credit.

The assistant does **not** unpack archives. Obtain the official individual companions;
if the provider supplies only an archive, extract it with trusted system software first.
Only the selected bounded header, matching numeric file and checksums are read/copied;
links/junctions, escaping filenames, wrong files and incomplete downloads are rejected.
Headers are capped at 1 MiB, checksums at 4 KiB, each original at 16 GiB and collections at
64 GiB, subject to tighter Library quota/free-space protections. No entire cube is loaded
into memory. Canceling verification/import leaves external originals intact and partial
staging unavailable for science.

Classic deterministic tests cover numeric preservation, masks, layouts/endian, rotation,
checksums, cancellation/identity and offline retention. **Genuine NASA Classic numeric
acceptance is pending**: the selected official header returned HTTP 401 without individual
Earthdata authorization. The existing NV5 Cuprite validation is retained as its own baseline,
not evidence that this official collection or every Classic distribution passed.

## AVIRIS-NG original-file verification

NG is validated as a separate family, not renamed Classic. Collection
`AVIRIS-NG_L2_Reflectance_2110` V1 uses `angYYYYMMDDtHHMMSS_corr/rfl…_img.hdr`
and its matching `.bin`. Supported layouts are the documented **425-band reflectance**
and **432-band corr from early 2014**, Float32/Float64, with actual header wavelengths,
scale/offset, nodata/bad bands and supported original WGS84 geographic/rotated UTM grid.
A Classic cube, water-vapor `h2o` companion, radiance, missing metadata or other revision
cannot be relabeled as NG reflectance.

NG headers and binaries can have **separate CMR granule IDs**. Use **Find matching header /
binary granules** on the selected product to search its exact original product stem.
Follow the actual official links and download both companions plus advertised checksum(s).
The assistant never constructs a guessed download URL. Auxiliary water-vapor products
are labeled **Download only**; EMIT is **Discovery / official access only**.

Independent NG synthetic tests cover 425/432 channels, BSQ/BIL/BIP, both byte orders,
Float32/Float64, original spectral/channel alignment, bad bands/nodata, checksums,
truncation, cancellation, exact retained reuse and offline reopening. The browser workflow
verifies and imports through production APIs and Library persistence using explicitly
synthetic local input, not NASA numeric acceptance.

**Genuine NG numerical acceptance remains pending**: the official original header returned
HTTP 401 without individual authorization. The official public SHA-256 for
`ang20140621t190550_corr_v1g_img.bin` was retrieved successfully; it does not establish that
the protected binary was downloaded or numerically validated. See the consolidated
[NASA evidence record](../research/v0.5.0-nasa-validation.json). Actual user-authorized
Windows browser download, native picker, physical iPad, MSFS and Quest checks remain open.

## Advanced local import reference

Normal pilot setup: [automatic quick start](quick-start.md) and [provider capabilities](automatic-data.md). Automatic NASA catalog discovery is supported; automated hyperspectral file preparation/authorization is not. The following ENVI workflow remains advanced.

The implemented minimum workflow reads **real georeferenced ENVI cubes** with ≥16 original wavelengths, not a generated spectrum or static graph. Candidate producers include NASA AVIRIS and AVIRIS-NG. Their products vary in format/projection and availability; not every portal item is directly supported. EMIT is a researched source, but its native NetCDF/HDF is not decoded here. No Earthdata token is embedded in the app.

Obtain a licensed radiance or reflectance cube, its original `.hdr`, wavelength list, bad-band mask and geographic metadata. Use WGS84 UTM north-up map info. Unsupported projections/rotation must be explicitly prepared in a scientific GIS; unlocated raw flight-line data cannot simply be assigned coordinates. Keep all real bands together. Wavelength units must be nm or micrometres; the reader converts wavelengths to nm and preserves measured values.

Create a `kind: "hyperspectral"` manifest with one band entry per cube channel, channel index starting at 0. For example (two entries shown—**the real manifest must contain at least 16 ordered bands**):

```json
"bands": [
  { "id": "band-001", "file": "actual-cube.img", "headerFile": "actual-cube.hdr", "format": "envi", "channel": 0, "unit": "reflectance", "scale": 1, "offset": 0, "noData": -9999 },
  { "id": "band-002", "file": "actual-cube.img", "headerFile": "actual-cube.hdr", "format": "envi", "channel": 1, "unit": "reflectance", "scale": 1, "offset": 0, "noData": -9999 }
]
```

Actual wavelengths are read from the header. If supplied in the manifest they must match. `badBand: true` must honor every zero in `bbl`; missing bands remain graph gaps. Use the real reflectance scale factor (if present in the header it is checked). Supported homogeneous units are `reflectance`, `radiance W/(m2 sr um)`, `radiance W/(m2 sr nm)`, `radiance mW/(cm2 sr um)` or `radiance uW/(cm2 sr nm)`. Do not relabel radiance as reflectance; automatic atmospheric correction/unit interconversion is not provided.

Enable Hyperspectral Imager over that collection, record and open **View Data → spectrum**. Select a geographic sample index; the viewer shows its coordinates and a chart of **original wavelength versus measured/scaled value**, with units. Canonical JSON and spectral CSV retain all sampled channels/quality gaps, source date and native grid. This does not identify minerals/species or perform spectral-library classification/calibration. The storage estimate grows with actual imported band count; 24×24 samples are bounded extractions, not a new full-resolution flight-line cube.
