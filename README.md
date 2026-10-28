# ✈️ AeroScience

**Airborne Science for Microsoft Flight Simulator 2024**

*Turn your flights into scientific research missions.*

AeroScience is a free, open-source application that brings airborne scientific research to **Microsoft Flight Simulator 2024**.

Inspired by real-world atmospheric research, aerial surveying, and NASA airborne science missions, AeroScience lets you plan research flights, operate virtual scientific instruments, collect observations, and analyze your results.

Whether you're flying a small Cessna, a business jet, or a high-altitude research aircraft, AeroScience gives your flights a scientific purpose.

## 🌟 Coming in AeroScience v0.7.0 — Research Programs & Collaborative Science

**v0.7.0 is in active development.** This section describes the **intended experience when the release is complete**, not a promise that every feature is available in today's installer. Some of these workflows already have development implementations and automated tests; others still need integration, hosting, or real-simulator acceptance. The [latest public release is v0.6.0](https://github.com/JustSeanC/AeroScience/releases/tag/v0.6.0).

The goal is simple: **give any MSFS 2024 flight a scientific purpose, whether you're researching on your own or flying as part of a larger team.**

### 🛩️ Do science on your own — anywhere in the world

You won't need an AeroScience account, an organization, a specific airport, or an internet connection to conduct supported local research. Pick an experiment, select **Fly Now** for a guided setup or **Customize** to work in the existing Mission Planner, and use the aircraft you have in MSFS when its capabilities fit the experiment.

The planned research activities include:

- **Scientific Observation Flight** — document what you encounter, with positions, times, and supporting observations where available.
- **Vertical Atmospheric Profile** — study how supported atmospheric measurements change as you climb or descend.
- **Atmospheric Transect** — investigate changes in atmospheric conditions across a route or region.
- **Terrain & Temperature Survey** — plan a survey using selectable sources such as DEM-derived terrain elevation, historical Landsat surface temperature, or simulator-reported air temperature, clearly distinguished from one another.
- **Flight-Regime Validation** — test supported speed, altitude, and other recorded flight conditions.
- **Experimental Sensor Checkout** — evaluate configurable, explicitly *modeled* instrument or system behavior against the inputs AeroScience can actually support.

You'll be able to adapt existing templates or create custom personal test cards, save your studies, and compare compatible results across multiple flights. Portable, **data-only** template sharing and proposals for organization use are part of the v0.7.0 target; sharing a template will not share your flight history or scientific data.

### 🧪 Create experimental flight-test programs

Go beyond a basic mission checklist. Build flight-test cards with measurable conditions, sustained holds, ordered test points, repetitions, and documented pilot observations. A single flight can contain multiple compatible active cards; you can switch between incompatible tests without ending the flight.

AeroScience will preserve individual manual and automatically detected attempts, including **passes, failures, interruptions, and insufficient evidence**. It will distinguish conditions actually reported by MSFS from modeled responses and pilot-entered observations. Programs can be mostly aircraft-agnostic or restrict eligible aircraft/configurations when a specific test genuinely requires it.

These are **simulated research and development workflows**, not real aircraft certification or airworthiness approvals.

### 🗺️ Operate your mission from one place

The planned **Mission Operations** experience brings the current flight, active research questions and test cards, scientific instruments, collection sessions, map, readiness warnings, and next actions together.

Use the AeroScience dashboard and compact MSFS toolbar to manage flight research. Flight recording and scientific collection remain separate: pausing a science session or switching test cards won't silently stop the flight recorder.

A synchronized **flight-and-evidence timeline** will connect original test attempts, measurements, collection sessions, pilot observations, and mission events with the matching points on maps, charts, and replay. Provisional in-flight progress will remain clearly different from final postflight evaluation.

### 👥 Join a research organization — completely optional

Organizations will be able to run their own AeroScience server and browser portal to coordinate shared airborne-science campaigns and fictional aircraft/system test programs. The goal is beginner-friendly managed hosting, with an advanced self-hosted option. Hosting may involve charges from the provider chosen by the organization; AeroScience itself does not require a central account for solo research.

Organization features are planned to include:

- **Programs and campaigns:** publish scientific questions, objectives, experiment definitions, and research opportunities.
- **Teams and assignments:** invite pilots, manage membership, assign test cards, or let eligible members claim available research.
- **Collaborative flights:** prepare a mission from an assignment, fly locally, and retain the exact card and publication revision associated with the resulting evidence.
- **Review and feedback:** examine submitted evidence by requirement, ask for specific missing observations, and approve or reject results with an audit history.
- **Research progress:** see approved contributions from distinct pilots, scientific observation locations, altitude bands, measurement types, dates, and remaining gaps.
- **Test-program management:** configure optional progression phases, record holds and authorized exceptions, and keep original measured failures separate from organizational waivers.

Ordinary scientific campaigns won't have to use advanced experimental-aircraft test phases.

### 🔒 You choose what gets shared

**Your flight recordings, mission plans, and scientific library remain local by default.** Joining an organization doesn't upload your flights.

After a research flight, you will be able to select the exact eligible evidence to share, see its destination and size, and explicitly approve a submission. Approved packages can be queued for delivery when an organization server becomes reachable. Drafts aren't upload permission, and a queued package cannot silently replace deleted or changed original evidence.

Only the submitted, authorized evidence is available for organizational review. Other members see approved summaries and permitted aggregates—not your private flight archive. Sharing the same flight with a second organization requires a separate choice and approval.

### 📊 Understand the science behind the result

Study maps, measurements, scientific source dates, quality information, flight replay, and comparisons—not just a score. The intended campaign tools will show **what was actually observed**, at which locations and altitudes, and which objectives still lack sufficient accepted evidence.

A recorded flight track does **not** prove a sensor observed everything underneath it. Satellite imagery, DEMs, and other historical datasets retain their original provenance and dates; simulated sensor outputs are not presented as real instrument readings. Unsupported or missing sources must be shown as unavailable rather than made-up science.

### 🚦 What v0.7.0 will *not* include

- No required global AeroScience cloud account or automatic upload of complete flights.
- No live organization-wide aircraft tracking, remote flight control, or real-time Mission Control; those ideas are deferred beyond v0.7.0.
- No claim of real-world aircraft certification, authentic instrument calibration, or validated scientific measurements beyond the evidence available.
- Hurricane reconnaissance, dropsondes, wildfire/ocean research suites, and airborne radar are **future v0.9.0 plans**, not v0.7.0 features.
- Full scientific-product support depends on validated source formats and data availability; some raster, QA, modeled-response, and NASA EMIT-original validation work remains a separate release gate.

**Development progress:** Follow the [v0.7.0 development tracker](docs/roadmap/v0.7.0-development-tracker.md) and [v0.7.0 product decisions](docs/roadmap/v0.7.0-product-decisions.md). Completing automated checkpoints does not by itself mean the Windows/MSFS experience, production organization hosting, or this release has passed final acceptance.

---

## 🌎 What You Can Do Today (v0.6.0)

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

In v0.6.0, explore FAA airport, runway, navigation and supported airspace references alongside your research route. Retained references work offline; published TFR boundaries and advisory mission screening show source freshness and coverage limitations. Official chart catalogs and downloads remain available, while in-app chart rendering is deferred. NASA EMIT import and spectral exploration are experimental, with genuine-original scientific validation still pending. See the [aviation guide](docs/v0.6.0-aviation-reference.md) and [EMIT guide](docs/research/v0.6.0-emit-integration.md) for supported capabilities and limitations.

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

**Latest release: AeroScience v0.6.0 — Aviation Mapping & Scientific Data Expansion**

**In development: AeroScience v0.7.0 — Personal Research, Experimental Test Cards & Organizations**

Find the Windows installer and release notes on the [v0.6.0 release page](https://github.com/JustSeanC/AeroScience/releases/tag/v0.6.0). The preview above describes the planned v0.7.0 release; it is not yet a downloadable stable release.

AeroScience is actively developed and continues to expand its scientific capabilities, mission planning tools, aircraft integration, and research workflows.

The project is designed for flight simulation, education, and recreational scientific exploration. It is not a replacement for certified scientific instruments or real-world operational research systems.

## 💬 Community & Support

Have questions, found a bug, or have an idea for a new scientific instrument or feature?

Join the AeroScience community!

**[Join our Discord](https://discord.gg/mpYyazCqvJ)**

You can also submit bug reports and suggestions through [GitHub Issues](https://github.com/JustSeanC/AeroScience/issues).

---

**AeroScience** — Bringing airborne science to virtual aviation.

Free • Open Source • Built for Microsoft Flight Simulator 2024

[Releases](https://github.com/JustSeanC/AeroScience/releases) | [Changelog](CHANGELOG.md) | [Documentation](docs/README.md) | [Discord](https://discord.gg/mpYyazCqvJ)
