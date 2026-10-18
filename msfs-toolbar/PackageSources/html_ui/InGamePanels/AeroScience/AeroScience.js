/* Simulator-native UI. SDK globals are provided by MSFS, not a normal web browser.
 * See docs/toolbar.md for SDK sources, provenance, and outstanding simulator checks. */
class IngamePanelAeroScience extends TemplateElement {
  connectedCallback() {
    super.connectedCallback();
    if (document.readyState === "loading")
      document.addEventListener("DOMContentLoaded", () => this.initialize(), {
        once: true,
      });
    else this.initialize();
  }
  initialize() {
    if (this.initialized || !this.isConnected) return;
    this.initialized = true;
    this.pending = new Map();
    this.listener = null;
    this.state = null;
    this.lastReply = 0;
    this.byId = (id) => this.querySelector("#" + id);
    this.onReply = (data) => this.receive(data);
    this.byId("as-start").onclick = () => {
      if (!this.byId("as-start").hasAttribute("disabled"))
        this.send("start", {
          name: this.byId("as-name").value,
          mode: this.byId("as-mode").value,
          planId: this.mapPlan || null,
        });
    };
    this.byId("as-end").onclick = () => {
      if (
        !this.byId("as-end").hasAttribute("disabled") &&
        this.state &&
        this.state.activeMission
      )
        this.send("end", { missionId: this.state.activeMission.id });
    };
    this.byId("as-marker").onclick = () => {
      if (
        this.state &&
        this.state.activeMission &&
        !this.byId("as-marker").hasAttribute("disabled")
      )
        this.send("marker", {
          missionId: this.state.activeMission.id,
          category: "observation",
        });
    };
    this.byId("as-mode").onchange = () => {
      this.byId("as-mode-help").textContent =
        this.byId("as-mode").value === "realistic"
          ? "Advanced science limits and procedures are coming in a future release. Any compatible aircraft can record."
          : "All compatible aircraft can record missions.";
    };
    this.byId("as-mission-page").onclick = () => this.page(false);
    this.byId("as-payload-page").onclick = () => this.page(true);
    this.byId("as-map-page").onclick = () => this.page("map");
    try {
      this.mapRenderer = new window.AeroScienceMovingMap(
        this.byId("as-map-canvas"),
        this.byId("as-map-waypoint-info"),
      );
    } catch (error) {
      this.mapRenderer = null;
      if (typeof console !== "undefined" && console.warn)
        console.warn(
          "AeroScience map initialization failed: " +
            String((error && error.message) || error),
        );
      this.byId("as-map-status").textContent =
        "The map renderer is unavailable in this UI context. Mission and Payload remain available; report the simulator version and Coherent console error.";
      for (const control of this.byId("as-map-view").querySelectorAll(
        "button,select",
      ))
        control.disabled = true;
    }
    this.mapVisible = !this.byId("as-map-view").hidden;
    this.lastMapAt = 0;
    this.routeItems = [];
    this.routeRevision = null;
    this.loadingRevision = null;
    this.mapCursor = null;
    this.planOffset = 0;
    this.byId("as-map-follow").onclick = () => {
      this.mapRenderer.follow = true;
      if (this.lastMapState) this.mapRenderer.update(this.lastMapState);
    };
    this.byId("as-map-free").onclick = () => {
      this.mapRenderer.follow = false;
      this.byId("as-map-status").textContent =
        "Free Pan — drag the map or use arrow keys. Aircraft follow is off.";
    };
    this.byId("as-map-fit").onclick = () => this.mapRenderer.fit();
    this.byId("as-map-in").onclick = () => this.mapRenderer.zoomBy(1.4);
    this.byId("as-map-out").onclick = () => this.mapRenderer.zoomBy(1 / 1.4);
    this.byId("as-map-waypoint").onchange = () => {
      this.mapRenderer.selected = Number(this.byId("as-map-waypoint").value);
      this.mapRenderer.draw();
      this.mapRenderer.describe();
      this.requestMapState();
    };
    this.byId("as-map-plan").onchange = () => {
      const select = this.byId("as-map-plan"),
        option = select.options[select.selectedIndex];
      this.mapPlan = select.value || null;
      this.mapPlanName = this.mapPlan ? option.textContent : null;
      this.mapPlanMode = this.mapPlan ? option.getAttribute("data-mode") : null;
      this.byId("as-start-plan").textContent = this.mapPlan
        ? "Selected research plan: " +
          this.mapPlanName +
          ". Recording keeps its route, instruments and science mode."
        : "No saved plan selected. This will be an unplanned recording.";
      this.byId("as-mode").disabled = !!this.mapPlan;
      this.byId("as-mode").value = this.mapPlanMode || "universal";
      this.byId("as-mode").onchange();
      if (this.mapPlan) this.byId("as-name").value = this.mapPlanName;
      this.routeRevision = null;
      this.loadingRevision = null;
      this.mapCursor = null;
      this.requestMapState();
    };
    this.byId("as-map-plans").onclick = () => {
      this.planOffset = 0;
      this.send("map-plans", { offset: 0 });
    };
    this.byId("as-map-more-plans").onclick = () => {
      this.planOffset += 10;
      this.send("map-plans", { offset: this.planOffset });
    };
    this.timer = setInterval(() => this.tick(), 1000);
    try {
      Include.addScript("/JS/Services/CommBus.js", () => this.registerBus());
    } catch (error) {
      this.status(
        "Unable to load the SDK CommBus service. Check the MSFS 2024 SDK/runtime version.",
      );
    }
    // Loading order differs between simulator UI contexts. Bound the fallback wait; never create sockets.
    let attempts = 0;
    this.loadTimer = setInterval(() => {
      if (this.listener || attempts++ >= 50) {
        clearInterval(this.loadTimer);
        if (!this.listener)
          this.status("CommBus unavailable. MSFS 2024 SU5+ is required.");
      } else this.registerBus();
    }, 100);
  }
  requestMapState() {
    if (
      !this.mapVisible ||
      !this.mapRenderer ||
      Array.from(this.pending.values()).some((p) => p.op === "map-state")
    )
      return;
    if (Date.now() - this.lastMapAt > 5000) {
      this.mapCursor = null;
      this.mapRenderer.track = [];
    }
    this.send("map-state", {
      planId: this.mapPlan || null,
      after: this.mapCursor,
      waypointIndex:
        this.byId("as-map-waypoint").value === ""
          ? null
          : Number(this.byId("as-map-waypoint").value),
    });
  }
  requestMapChunk() {
    if (
      !this.isConnected ||
      !this.mapRenderer ||
      !this.mapVisible ||
      !this.loadingRevision ||
      Array.from(this.pending.values()).some((p) => p.op === "map-chunk")
    )
      return;
    this.send("map-chunk", {
      planId: this.mapPlan || null,
      revision: this.loadingRevision,
      offset: this.routeItems.length,
    });
  }
  receiveMap(op, map) {
    if (op === "map-plans") {
      const select = this.byId("as-map-plan");
      select.replaceChildren();
      const empty = document.createElement("option");
      empty.value = "";
      empty.textContent = "Active mission / no saved plan";
      select.appendChild(empty);
      for (const plan of map) {
        const option = document.createElement("option");
        option.value = plan.id;
        option.textContent = plan.name;
        option.setAttribute("data-mode", plan.mode);
        select.appendChild(option);
      }
      if (this.mapPlan && !map.some((p) => p.id === this.mapPlan)) {
        const keep = document.createElement("option");
        keep.value = this.mapPlan;
        keep.textContent = this.mapPlanName || "Selected research plan";
        keep.setAttribute("data-mode", this.mapPlanMode || "universal");
        select.appendChild(keep);
      }
      select.value = this.mapPlan || "";
      this.byId("as-map-status").textContent =
        "Saved plans loaded. An active mission always uses its recorded plan snapshot.";
      return;
    }
    if (op === "map-state") {
      this.lastMapAt = Date.now();
      this.byId("as-map-plan").disabled = !!map.missionId;
      const changed =
        map.revision !== this.routeRevision &&
        map.revision !== this.loadingRevision;
      if (changed) {
        this.loadingRevision = map.revision;
        this.routeItems = [];
        this.mapCursor = null;
        this.mapRenderer.route(map.revision, []);
        this.byId("as-map-waypoint").replaceChildren();
        if (map.items === 0) {
          this.routeRevision = map.revision;
          this.loadingRevision = null;
        } else this.requestMapChunk();
      }
      this.mapCursor = changed || map.hasMore ? null : map.nextSequence;
      if (map.hasMore) {
        this.mapRenderer.track = [];
        map.track = [];
      }
      this.lastMapState = map;
      this.mapRenderer.update(map);
      this.byId("as-map-status").textContent =
        (map.name ||
          "No active planned mission — select a saved plan or record one from the companion.") +
        " · " +
        (this.loadingRevision
          ? "Loading route " + this.routeItems.length + " / " + map.items
          : "Route synchronized · " +
            map.waypoints +
            " waypoints · next observed target " +
            (map.nextWaypoint < map.waypoints ? map.nextWaypoint + 1 : "end")) +
        ".";
      this.byId("as-map-progress").textContent = map.progressExplanation;
      return;
    }
    if (op === "map-chunk") {
      if (
        map.revision !== this.loadingRevision ||
        map.offset !== this.routeItems.length ||
        map.items.length > 40 ||
        map.nextOffset !== map.offset + map.items.length
      )
        return;
      if (this.routeItems.length + map.items.length > 2000) return;
      this.routeItems.push(...map.items);
      if (map.complete) {
        this.routeRevision = map.revision;
        this.loadingRevision = null;
        this.mapRenderer.route(map.revision, this.routeItems);
        if (this.lastMapState) this.mapRenderer.update(this.lastMapState);
        const select = this.byId("as-map-waypoint");
        select.replaceChildren();
        for (const p of this.routeItems.filter((p) => p.kind === "waypoint")) {
          const o = document.createElement("option");
          o.value = p.index;
          o.textContent = p.index + 1 + ". " + p.name;
          select.appendChild(o);
        }
        this.byId("as-map-status").textContent =
          "Route synchronized: " +
          select.options.length +
          " ordered waypoints. Offline geography; no online tile requests.";
      } else setTimeout(() => this.requestMapChunk(), 100);
    }
  }
  registerBus() {
    if (
      this.listener ||
      typeof RegisterCommBusListener !== "function" ||
      !this.isConnected
    )
      return;
    try {
      this.listener = RegisterCommBusListener(() =>
        this.status("Simulator channel ready; waiting for the Windows engine."),
      );
      this.listener.on("AeroScience.Reply.v1", this.onReply);
      this.tick();
    } catch (error) {
      this.status("Unable to register the SDK CommBus listener.");
    }
  }
  page(payload) {
    this.mapVisible = payload === "map";
    this.byId("as-mission-view").hidden = payload !== false;
    this.byId("as-payload-view").hidden = payload !== true;
    this.byId("as-map-view").hidden = !this.mapVisible;
    if (this.mapVisible && this.mapRenderer) {
      this.mapRenderer.resize();
      this.requestMapState();
    }
  }
  status(text) {
    this.byId("as-status").textContent = text;
  }
  requestId() {
    if (
      typeof crypto !== "undefined" &&
      typeof crypto.randomUUID === "function"
    )
      return crypto.randomUUID();
    return "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g, (c) => {
      const r = Math.floor(Math.random() * 16);
      return (c === "x" ? r : (r & 3) | 8).toString(16);
    });
  }
  send(op, fields) {
    if (!this.listener || this.pending.size >= 8) return;
    const requestId = this.requestId();
    this.pending.set(requestId, {
      op: op,
      sent: Date.now(),
      planId: (fields && fields.planId) || null,
    });
    if (op !== "state" && !op.startsWith("map-")) {
      this.setControls(false);
      this.status("Sending mission command…");
    }
    try {
      const result = this.listener.callSimConnect(
        "AeroScience.Command.v1",
        JSON.stringify(
          Object.assign(
            { protocol: 1, requestId: requestId, op: op },
            fields || {},
          ),
        ),
      );
      if (result && typeof result.catch === "function")
        result.catch(() => {
          this.pending.delete(requestId);
          this.status(
            "Simulator command delivery failed. Check the engine connection.",
          );
        });
    } catch (error) {
      this.pending.delete(requestId);
      this.status("Simulator command delivery failed.");
    }
  }
  tick() {
    if (
      !this.listener ||
      document.hidden ||
      this.byId("aeroscience-frame").classList.contains("panelInvisible")
    )
      return;
    for (const [id, request] of this.pending)
      if (Date.now() - request.sent > 5000) {
        this.pending.delete(id);
        if (request.op.startsWith("map-"))
          this.byId("as-map-status").textContent =
            "Map reply timed out; retrying with the cached route retained.";
        else if (request.op !== "state")
          this.status(
            "No command confirmation. Check mission state before retrying.",
          );
      }
    if (Date.now() - this.lastReply > 6000) {
      this.setControls(false);
      this.status(
        "Windows engine unavailable. Launch AeroScience and check the SimConnect runtime.",
      );
      for (const id of [
        "as-altitude",
        "as-airspeed",
        "as-heading",
        "as-vspeed",
      ])
        this.byId(id).textContent = "—";
      this.byId("as-mach").textContent = "Mach —";
      this.renderResearch(null, false);
      if (this.mapRenderer) {
        this.mapRenderer.update({ aircraft: null, track: [], markers: [] });
        this.byId("as-map-status").textContent =
          "Companion disconnected — cached route retained; live aircraft unavailable.";
      }
    }
    this.send("state");
    if (this.mapVisible) {
      this.requestMapState();
      this.requestMapChunk();
    }
  }
  setControls(enabled) {
    for (const id of ["as-start", "as-end", "as-marker"]) {
      if (enabled) this.byId(id).removeAttribute("disabled");
      else this.byId(id).setAttribute("disabled", "");
    }
  }
  receive(data) {
    try {
      if (typeof data !== "string" || data.length > 8192) return;
      const reply = JSON.parse(data);
      const pendingRequest = this.pending.get(reply.requestId);
      if (
        reply.protocol === 1 &&
        pendingRequest &&
        pendingRequest.op.startsWith("map-")
      ) {
        this.pending.delete(reply.requestId);
        if (
          pendingRequest.op !== "map-plans" &&
          pendingRequest.planId !== (this.mapPlan || null)
        ) {
          this.requestMapState();
          return;
        }
        if (reply.ok && reply.map)
          this.receiveMap(pendingRequest.op, reply.map);
        else {
          this.byId("as-map-status").textContent =
            reply.error || "Map synchronization failed; waiting to retry.";
          this.loadingRevision = null;
        }
        return;
      }
      if (
        reply.protocol !== 1 ||
        !this.pending.has(reply.requestId) ||
        !reply.state
      )
        return;
      this.pending.delete(reply.requestId);
      this.lastReply = Date.now();
      this.state = reply.state;
      const mutationPending = Array.from(this.pending.values()).some(
        (x) => x.op !== "state" && !x.op.startsWith("map-"),
      );
      this.setControls(!mutationPending);
      this.render(reply.state);
      if (!reply.ok)
        this.status(reply.error || "Mission command was rejected.");
    } catch (error) {
      this.status(
        "Invalid engine reply. Check companion and toolbar versions.",
      );
    }
  }
  render(state) {
    const t = state.telemetry,
      mission = state.activeMission;
    const fresh = t && Date.now() - Date.parse(t.timestampUtc) < 6000;
    this.byId("as-source").textContent =
      state.connection.source === "simulated"
        ? "SIMULATED TEST DATA"
        : fresh && state.connection.state === "connected"
          ? "SIMCONNECT TELEMETRY"
          : "MSFS DISCONNECTED";
    this.status(state.recordingStop ? "RECORDING STOPPED · " + state.recordingStop.code + ": " + state.recordingStop.message : state.connection.message);
    this.byId("as-aircraft").textContent = fresh
      ? t.aircraft
      : "Awaiting initialized aircraft telemetry";
    const fmt = (v) =>
      Number.isFinite(v) ? Math.round(v).toLocaleString("en-US") : "—";
    this.byId("as-altitude").textContent = fresh ? fmt(t.altitudeMslFeet) : "—";
    this.byId("as-airspeed").textContent = fresh
      ? fmt(t.indicatedAirspeedKnots)
      : "—";
    this.byId("as-mach").textContent =
      "Mach " +
      (fresh && Number.isFinite(t.machNumber) ? t.machNumber.toFixed(2) : "—");
    this.byId("as-heading").textContent = fresh ? fmt(t.headingDegrees) : "—";
    this.byId("as-vspeed").textContent = fresh
      ? fmt(t.verticalSpeedFeetPerMinute)
      : "—";
    this.renderResearch(state.research, fresh);
    if (!mission) this.byId("as-marker").setAttribute("disabled", "");
    this.byId("as-idle").hidden = !!mission;
    this.byId("as-recording").hidden = !mission;
    if (mission) {
      // Prepare the next mission with the safe default, independently of the active mission's mode.
      this.byId("as-mode").value = "universal";
      this.byId("as-mode-help").textContent =
        "All compatible aircraft can record missions.";
      this.byId("as-name").value = "";
      this.byId("as-active-name").textContent = mission.name;
      this.byId("as-active-mode").textContent =
        mission.mode === "realistic"
          ? "Realistic Mode · advanced science features coming in a future release"
          : "Universal Mode · Fly Anything";
      this.byId("as-samples").textContent = mission.sampleCount + " samples";
      const seconds = Math.max(
        0,
        Math.floor((Date.now() - Date.parse(mission.startedUtc)) / 1000),
      );
      this.byId("as-duration").textContent = [
        Math.floor(seconds / 3600),
        Math.floor(seconds / 60) % 60,
        seconds % 60,
      ]
        .map((x) => ("0" + x).slice(-2))
        .join(":");
    }
  }
  renderResearch(research, fresh) {
    const g = research && research.guidance;
    this.byId("as-guidance").textContent =
      !fresh || !g || g.quality !== "valid"
        ? "Flight Guidance unavailable"
        : "AP: " +
          g.autopilot +
          " | " +
          [g.lateral, g.vertical, g.speed]
            .map(
              (m) =>
                (m.mode || "Unavailable") +
                (Number.isFinite(m.target)
                  ? " " +
                    m.target.toFixed(m.unit === "mach" ? 2 : 0) +
                    " " +
                    m.unit
                  : "") +
                (m.armed ? " (Armed: " + m.armed + ")" : ""),
            )
            .join(" | ");
    const control = this.byId("as-science-pause");
    if (control) {
      control.textContent =
        research && research.intentionallyPaused
          ? "Resume science collection"
          : "Pause science collection";
      control.disabled = !(this.state && this.state.activeMission);
      control.onclick = () => {
        if (!this.state || !this.state.activeMission) return;
        this.send(
          research && research.intentionallyPaused
            ? "science-resume"
            : "science-pause",
          { missionId: this.state.activeMission.id },
        );
      };
    }
    const collection = this.byId("as-science-state");
    if (collection)
      collection.textContent = research
        ? "Collection: " + research.collectionStatus
        : "Science unavailable";
    const holder = this.byId("as-instruments");
    if (!research) {
      holder.textContent = "Scientific source unavailable";
      return;
    }
    if (!this.instrumentNodes) this.instrumentNodes = {};
    if (!holder.querySelector(".as-instrument")) holder.textContent = "";
    for (const instrument of research.instruments || []) {
      let nodes = this.instrumentNodes[instrument.instrumentId];
      if (!nodes || !nodes.row.parentNode) {
        const row = document.createElement("div");
        row.className = "as-instrument";
        const title = document.createElement("strong");
        title.textContent =
          {
            "ambient-temperature": "Ambient temperature",
            "ambient-pressure": "Ambient pressure",
            "ambient-wind": "Ambient wind",
            "ambient-density": "Air density",
            "thermal-imager": "Thermal Imager",
            "multispectral-imager": "Multispectral Imager",
            "hyperspectral-imager": "Hyperspectral Imager",
            "terrain-scanner": "Terrain Scanner (LiDAR / Elevation)",
          }[instrument.instrumentId] || "Science instrument";
        const stateLabel = document.createElement("span");
        stateLabel.className = "as-science-state";
        const toggle = document.createElement("new-push-button");
        const interval = document.createElement("select");
        for (const seconds of [1, 2, 5, 10, 30, 60]) {
          const option = document.createElement("option");
          option.value = seconds * 1000;
          option.textContent = seconds + " s";
          interval.appendChild(option);
        }
        const values = document.createElement("div"),
          status = document.createElement("p");
        values.className = "as-science-values";
        const details = document.createElement("details"),
          summary = document.createElement("summary");
        summary.textContent = "Diagnostics";
        details.appendChild(summary);
        details.appendChild(status);
        const intervalLabel = document.createElement("label");
        intervalLabel.textContent = "Collection interval ";
        intervalLabel.appendChild(interval);
        row.appendChild(title);
        row.appendChild(stateLabel);
        row.appendChild(toggle);
        row.appendChild(intervalLabel);
        row.appendChild(values);
        row.appendChild(details);
        holder.appendChild(row);
        nodes = {
          row: row,
          state: stateLabel,
          toggle: toggle,
          interval: interval,
          values: values,
          status: status,
        };
        this.instrumentNodes[instrument.instrumentId] = nodes;
        toggle.onclick = () => {
          const current = this.state.research.instruments.find(
            (i) => i.instrumentId === instrument.instrumentId,
          );
          this.send("instrument", {
            instrumentId: instrument.instrumentId,
            enabled: !current.enabled,
          });
        };
        interval.onchange = () =>
          this.send("instrument", {
            instrumentId: instrument.instrumentId,
            intervalMs: Number(interval.value),
          });
      }
      nodes.toggle.textContent = instrument.enabled ? "On" : "Off";
      nodes.toggle.setAttribute(
        "aria-label",
        instrument.enabled ? "Disable instrument" : "Enable instrument",
      );
      if (document.activeElement !== nodes.interval)
        nodes.interval.value = instrument.intervalMs;
      nodes.values.textContent = "";
      for (const v of instrument.measurements || []) {
        const value = document.createElement("div");
        value.className = "as-science-reading";
        const label =
          {
            temperature: "Temperature",
            pressure: "Pressure",
            "wind-speed": "Wind speed",
            "wind-direction": "Wind direction",
            density: "Density",
          }[v.measurementId] || "Measurement";
        const display = Number.isFinite(v.displayValue)
          ? v.displayValue
          : v.value;
        value.textContent =
          label +
          ": " +
          (fresh &&
          instrument.enabled &&
          instrument.quality === "valid" &&
          v.quality === "valid" &&
          Number.isFinite(display)
            ? display.toFixed(
                v.measurementId === "density" && v.displayUnit === "slug/ft³"
                  ? 6
                  : 2,
              )
            : "Unavailable") +
          " " +
          (v.displayUnit || v.unit);
        nodes.values.appendChild(value);
      }
      nodes.state.textContent = instrument.enabled
        ? instrument.quality
        : "Disabled";
      nodes.row.setAttribute(
        "data-state",
        instrument.enabled ? instrument.quality : "disabled",
      );
      nodes.status.textContent =
        (instrument.enabled ? instrument.quality : "disabled") +
        " · " +
        instrument.acquisitionSets +
        " acquisition sets · " +
        instrument.observationCount +
        (instrument.instrumentId.indexOf("imager") >= 0 || instrument.instrumentId === "terrain-scanner" ? " saved products · " : " measurement values · ") +
        instrument.rejectedObservations +
        " rejected · " +
        instrument.missingIntervals +
        " unexpected missing · actual " +
        (Number.isFinite(instrument.actualAverageIntervalSeconds)
          ? instrument.actualAverageIntervalSeconds.toFixed(2) + " s"
          : "unavailable") +
        " · last " +
        (instrument.lastSuccessfulUtc || "none");
    }
  }
  disconnectedCallback() {
    if (this.mapRenderer) this.mapRenderer.destroy();
    clearInterval(this.timer);
    clearInterval(this.loadTimer);
    if (this.listener) {
      this.listener.off("AeroScience.Reply.v1", this.onReply);
      this.listener.unregister();
    }
    this.listener = null;
    this.initialized = false;
    if (super.disconnectedCallback) super.disconnectedCallback();
  }
}
window.customElements.define("ingamepanel-aeroscience", IngamePanelAeroScience);
checkAutoload();
