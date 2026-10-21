# Instrument calibration and provenance

This describes the v0.5.0 development implementation (P03). Installed Windows/MSFS,
physical iPad and Quest acceptance remain pending. Existing 0.4.2 QA instructions are
unchanged; this is not a release announcement.

Open **Science Collection → instrument configuration → Calibration & provenance**.
The same controls are available in mission plans and reusable presets. Reference
specifications reuse the existing instrument/profile catalog and retain version, hash,
references and limitations. The displayed camera/scanner geometry is not a calibration
of a satellite scene. Simulator instrument calibration accuracy is **Unknown**; external
pixel recalibration through an airborne profile is **Unavailable**. Original provider
scales, offsets, QA, wavelengths, georeferencing and observation dates remain authoritative.

On the companion PC, create an attributed **user-defined** revision, record its basis
and optional public reference, then save it. Saving a definition does not apply it to
the active instruments; select it and use **Apply instrument configuration**, save the
plan or save the preset. Revisions are immutable: editing creates the next revision.
Other paired browsers may select saved definitions, but cannot create definitions.
Choose **Original values / reference only** to stop applying a definition to future
acquisitions. This does not remove existing observations, revisions or mission evidence.

Optional **separate user-adjusted scalar channels** support ambient temperature (degC),
pressure (inHg), wind speed (kt) and density (slug/ft3). The versioned method
`user-affine-1` computes `derived = original * gain + offset`: gain is dimensionless;
offset uses the original channel unit. This is user-asserted arithmetic, not a certified
physical calibration or increased simulator accuracy. Wind direction is circular and
has no supported adjustment. Remote raster/spectral channels cannot use this operation.
Remote instruments can retain user metadata without applying numeric changes.

Input bounds and UTC validity dates are optional applicability constraints. Missing,
invalid, out-of-range, expired or nonfinite inputs/results produce no valid derived
observation. Physically impossible scalar results are withheld. The valid original
measurement still records normally; an adjustment never shuts down original acquisition.
Without an explicit adjustment, the numeric processing remains unchanged.

Derived rows use a distinct `.user-adjusted` measurement ID and **Derived** provenance.
Each records its original channel/value/unit/time, complete definition, revision/hash and
formula. Raw observations retain their original IDs, values and provenance. Counts and
storage estimates include configured derived rows and their metadata; these are not
additional independent observations or Good Data credit. P08/P09 will assess yield and
mission grading separately.

Mission start/configuration events freeze accepted definitions. Offline JSON, research
packages and replay retain those snapshots even if the settings catalog changes; science
CSV identifies derivations and includes their revision/hash in the source label. Legacy
missions have no calibration claim added. Mission schema remains 8 and Library schema 2;
the optional fields do not rewrite old records or alter the native SimConnect layout or
toolbar protocol. Settings revisions use the existing atomic configuration directory;
unreadable/future settings are preserved and cannot be silently replaced.

See [scientific methods](v0.4.0-scientific-methods.md),
[reference profiles](v0.4.0-instrument-profiles.md),
[development progress](roadmap/v0.5.0-development-tracker.md) and
[validation tiers](development-validation.md). P04 adds instrument-specific operating reports to Science Collection and historical review.
Reports retain exact inputs/configuration and separate operating compliance, derived validity
and source QA. Modeled overlap is not valid scientific coverage; unknown internal payload
temperature remains unknown. Read-only assessment APIs support offline replay cutoffs.
These advisories do not veto raw acquisition. Multi-session integration is the P05 milestone.
