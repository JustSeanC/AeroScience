# ✈️ AeroScience

**Airborne Science for Microsoft Flight Simulator 2024**

*Turn your flights into scientific research missions.*

AeroScience is a free, open-source application that brings airborne scientific research to **Microsoft Flight Simulator 2024**.

Inspired by real-world atmospheric research, aerial surveying, and NASA airborne science missions, AeroScience lets you plan research flights, operate virtual scientific instruments, collect observations, and analyze your results.

Whether you're flying a small Cessna, a business jet, or a high-altitude research aircraft, AeroScience gives your flights a scientific purpose.

## 🌟 Current release — v0.8.0 Beta 1 (owner-accepted baseline)

**[Download AeroScience v0.8.0 Beta 1](https://github.com/JustSeanC/AeroScience/releases/tag/v0.8.0-beta.1)** for Microsoft Flight Simulator 2024. Published November 22, 2026, and accepted by the project owner as the current release baseline. GitHub lists it as a regular release.

The **actual installer and application still identify themselves as `0.8.0-beta.1`**, with a deliberately separate beta installation, settings, and data profile. This accepted build has **not** been renamed, rebuilt as a distinct `v0.8.0` installer, or certified for in-place upgrade/migration of earlier installations. The Windows x64 installer and SHA-256 checksum are attached to the release.

### 🛩️ Fly, record, and collect science

- Guided Home, Missions, and Current Flight workflows help choose research objectives and operate a flight.
- Flight recording and scientific collection have **independent controls**, including science arm, start, pause, resume, and end.
- Scientific missions and experimental test cards distinguish simulated, recorded, and unsupported measurements instead of fabricating evidence.
- Maps, Results Overview, Explore, and Evidence support reviewing flights alongside source-backed environmental observations and their provenance.

### 🌍 Explore genuine scientific sources

- Work with supported terrain, geographic, satellite, and **historical LiDAR** data, including retained originals and derived displays where supported.
- Original source dates, quality, units, datums, processing, and missing coverage matter. A satellite scene or historical LiDAR cloud is **not** a live measurement collected by your simulated aircraft.
- Supported scientific products and import methods depend on the provider, available source files, licensing, and format. Unsupported transformations or absent source coverage remain explicitly unavailable.

### 👥 Research alone or with an organization

Local personal research does not require joining an organization or automatically uploading flight history. Supported organization workflows include assignments, evidence submission/review, and separately consented temporary live sharing. Organization hosting and external services have their own configuration and validation requirements; a production one-click deployment is **not** certified by this release.

### 🔒 Your data and testing boundaries

The accepted installer keeps its beta profile separate from prior AeroScience installations. The matching MSFS 2024 toolbar is installed and removed explicitly through beta Settings, with MSFS closed. Only one companion runs at a time. Feedback export is local and reviewed by you before any manual sharing; original research, credentials, and logs are not automatically uploaded.

Longer endurance flights, larger scientific datasets, additional aircraft/hardware/providers, comprehensive accessibility checks, and fluent review of non-English translations remain areas for continued validation. For accurate limitations and safe installation/rollback, see the [tester guide](docs/beta/v0.8.0-beta.1/START-HERE.html), [known limitations](docs/beta/v0.8.0-beta.1/KNOWN-ISSUES.md), and [optional QA cards](docs/beta/v0.8.0-beta.1/TEST-CARDS.md).

**Next direction:** [v0.9.0 scientific-mission scope](docs/roadmap/v0.9.0-scope.md) is planning, not shipped functionality. Older [v0.7.0 product decisions](docs/roadmap/v0.7.0-product-decisions.md) and research handoffs are preserved as historical context, not current release announcements.

---

## 🌎 What You Can Do With AeroScience

**🗺️ Plan Research Missions**

Create custom flight plans for atmospheric research, geographic surveys, observation flights, and more. Define research areas, waypoints, flight altitudes, survey patterns, and scientific objectives.

**🔬 Collect Scientific Data**

Use virtual instruments to collect atmospheric measurements such as temperature, pressure, wind, and air density. Explore terrain, thermal, and multispectral observations using real-world scientific datasets.

**✈️ Fly Your Aircraft**

AeroScience connects to MSFS 2024 to track your aircraft's location, altitude, speed, heading, and flight path. Use the in-simulator toolbar or the AeroScience dashboard to manage your research.

**📊 Analyze Your Results**

After your flight, explore collected observations on interactive maps, review scientific measurements, compare missions, replay flight paths, and export your findings.

**🌐 Explore Real-World Science**

AeroScience integrates actual Earth observation datasets, including satellite imagery and geographic information, to connect your simulated flights with real scientific information.

Earlier v0.6.0 work introduced FAA airport, runway, navigation and supported airspace references alongside research routes. Retained references work offline; published TFR boundaries and advisory mission screening show source freshness and coverage limitations. Official chart catalogs and downloads remain available, while in-app chart rendering is deferred. NASA EMIT support has source-format and genuine-original validation limits; consult the current in-app capability and provenance messages rather than assuming an imported product is supported. See the [aviation guide](docs/v0.6.0-aviation-reference.md) and [EMIT guide](docs/research/v0.6.0-emit-integration.md) for supported capabilities and limitations.

## 🚀 How Does It Work?

1. **Create a mission.** Choose a research objective, select your instruments, and plan your route or survey area. You can also start an unplanned flight.

2. **Launch MSFS 2024.** Fly your aircraft normally while AeroScience connects to the simulator and displays your flight information.

3. **Start collecting science.** Activate your selected instruments and record observations along your flight path. Science collection can be managed separately from flight recording.

4. **Complete your research flight.** End the mission and save your flight history, scientific observations, and results.

5. **Explore your discoveries.** Open Science Results or Mission History to examine maps, measurements, charts, and exportable research data.

No specialized aircraft or complicated flight simulator modifications are required.

## 🔭 Universal & Realistic Modes

AeroScience supports two approaches to airborne research.

**Universal Mode** makes scientific exploration accessible across compatible MSFS aircraft, regardless of whether they were originally designed for scientific research.

**Realistic Mode** introduces additional considerations inspired by real airborne science operations, including instrument calibration, aircraft operating conditions, and sensor limitations.

Both modes emphasize collecting and understanding scientific observations.

## 🛰️ Where Does the Science Come From?

AeroScience combines two different sources of information:

**Simulator measurements** provide atmospheric conditions and aircraft information reported by Microsoft Flight Simulator during your flight.

**Real-world scientific datasets** provide geographic and remote-sensing observations from external sources, including satellite-based Earth observations.

These external datasets represent observations from their original collection dates, not live measurements made by your simulated aircraft.

AeroScience brings these sources together to create an educational, research-inspired airborne science experience.

## 💻 Getting Started

AeroScience is designed for Windows PCs running Microsoft Flight Simulator 2024.

Download the available Windows installer from the [GitHub Releases page](https://github.com/JustSeanC/AeroScience/releases), complete setup, and open AeroScience from your Start menu.

The application includes a desktop dashboard and an MSFS toolbar interface. No programming or development tools are needed to use the installed application.

Your missions and collected data are stored locally, and previously saved results can be reviewed offline. Downloading new external scientific datasets requires internet access.

## 🧪 Project Status

**Current owner-accepted release:** [AeroScience v0.8.0 Beta 1 — Accepted Baseline](https://github.com/JustSeanC/AeroScience/releases/tag/v0.8.0-beta.1). This is published as a regular GitHub release, while its installed product, channel, and file names retain the `0.8.0-beta.1` identity.

**Active source branch:** [`main`](https://github.com/JustSeanC/AeroScience/tree/main). The accepted release tag points to the exact owner-tested runtime source `c40db9311c9359b124f2373a2205a3708aecad58`; subsequent documentation-only handoffs and maintenance in `main` are not additional installer builds.

**Historical continuity:** Old development branches were retired after their unique v0.7.1 maintenance/build and research-campaign tips were preserved under `archive/pre-v080-cleanup/` tags. Existing published releases, scientific evidence, build receipts, and historical roadmap documentation remain available.

**Future development:** Use `main` as the source baseline; v0.9.0 work is planned, not yet an available feature release.

AeroScience is designed for flight simulation, education, and recreational scientific exploration, not certified scientific instrumentation or real-world operational research systems.

## 💬 Community & Support

Have questions, found a bug, or have an idea for a new scientific instrument or feature?

Join the AeroScience community!

**[Join our Discord](https://discord.gg/mpYyazCqvJ)**

You can also submit bug reports and suggestions through [GitHub Issues](https://github.com/JustSeanC/AeroScience/issues).

---

**AeroScience** — Bringing airborne science to virtual aviation.

Free • Open Source • Built for Microsoft Flight Simulator 2024

[Releases](https://github.com/JustSeanC/AeroScience/releases) | [Changelog](CHANGELOG.md) | [Documentation](docs/README.md) | [Discord](https://discord.gg/mpYyazCqvJ)
