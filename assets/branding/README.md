# AeroScience Orbital Arc branding — reference assets

These are **scalable SVG redraws of the approved Orbital Arc logo concept**. They are reference assets for a **future application branding update**, not replacements of the live icons in this commit.

## Files

- `orbital-arc-primary.svg` — teal, transparent-background logo for the dashboard/header, app icon, browser favicon, documentation, and Windows icon derivations.
- `orbital-arc-toolbar-white.svg` — simplified white A with a black flight arc and a thin white edge, on transparency intended for the Microsoft Flight Simulator toolbar and other small monochrome icon uses.

## Design rules

- Use the **clean A with sweeping flight arc** consistently; **thicken the strokes of the A** without making its overall silhouette wider.
- **Do not** use the Earth/terrain-background Discord server icon in the AeroScience app. That artwork is reserved for Discord.
- No outer-space, stars, globe, or scenery elements in the application icon assets.
- Keep branding modifications scoped to appropriate UI and packaging assets; preserve functional code.

## Implementation touchpoints for Codex (next update)

Current live assets as of creation:
- `src/AeroScience.Engine/wwwroot/icon.svg` is loaded in `src/AeroScience.Engine/wwwroot/index.html` for both browser favicon and dashboard/header branding.
- `msfs-toolbar/PackageSources/html_ui/icons/toolbar/ICON_AEROSCIENCE.svg` is referenced by `msfs-toolbar/PackageSources/InGamePanels/aeroscience_panel.xml`.

When implementing:
1. Use the primary vector for the main UI and generate suitable PNG/ICO derivatives if needed by Windows/installer packaging.
2. Use the white A / black arc version for the sim toolbar; validate that the black swoosh remains visible on the dark UI (thin light edge provided). **Check simulator SVG limitations, raster sizes and legibility in-sim**, and simplify further if required.
3. Search for old brand/icon references in active pages, help/about surfaces, shortcuts, manifests, installers, and packaging.
4. Verify readability at 16/24/32/64/256 px; check both light and dark backgrounds where applicable.
5. Do not change mission, telemetry, scientific workflow, or toolbar functionality as part of this branding pass.

This folder intentionally does **not** change any live asset paths.
