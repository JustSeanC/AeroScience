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
    this.byId("as-mission-view").hidden = payload;
    this.byId("as-payload-view").hidden = !payload;
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
    this.pending.set(requestId, { op: op, sent: Date.now() });
    if (op !== "state") {
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
        if (request.op !== "state")
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
    }
    this.send("state");
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
        (x) => x.op !== "state",
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
    this.status(state.connection.message);
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
        " measurement values · " +
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
