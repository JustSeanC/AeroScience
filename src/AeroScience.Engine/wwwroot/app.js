const $ = (id) => document.getElementById(id);
const number = (v, digits = 0) =>
  Number.isFinite(v)
    ? v.toLocaleString("en-US", {
        maximumFractionDigits: digits,
        minimumFractionDigits: digits,
      })
    : "—";
const duration = (seconds) => {
  const s = Math.max(0, Math.floor(seconds));
  return [Math.floor(s / 3600), Math.floor(s / 60) % 60, s % 60]
    .map((n) => String(n).padStart(2, "0"))
    .join(":");
};
const date = (value) =>
  new Date(value).toLocaleString(undefined, {
    dateStyle: "medium",
    timeStyle: "short",
    timeZone: "UTC",
  }) + " UTC";
const modeLabel = (mode) =>
  mode === "realistic" ? "Realistic Mode" : "Universal Mode";
const modeHelp = (mode) =>
  mode === "realistic"
    ? "Realistic Mode · Advanced science limits and procedures are coming in a future release. All compatible aircraft can record missions."
    : "Universal Mode";
function el(tag, text, className) {
  const node = document.createElement(tag);
  if (text !== undefined) node.textContent = text;
  if (className) node.className = className;
  return node;
}
let csrf = "",
  current = null,
  stream = null,
  chart = [],
  lastTimestamp = "",
  historyPage = 0,
  historyRequest = 0;
let authorized = false,
  streamAlive = false;
function notice(message = "") {
  $("notice").textContent = message;
  $("notice").hidden = !message;
}
async function api(path, body) {
  const response = await fetch(
    path,
    body === undefined
      ? {}
      : {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
            "X-AeroScience-CSRF": csrf,
          },
          body: JSON.stringify(body),
        },
  );
  if (!response.ok) {
    const data = await response.json().catch(() => ({}));
    if (response.status === 401) {
      authorized = false;
      stream?.close();
      $("pairing").hidden = false;
      $("workspace").hidden = true;
    }
    throw new Error(
      data.error ||
        {
          401: "Pairing failed or your session expired. Reconnect this display.",
          403: "Request protection rejected the command. Reload this display.",
          429: "Too many pairing attempts. Wait one minute and try again.",
        }[response.status] ||
        `Request failed (${response.status}).`,
    );
  }
  return response.json();
}
async function connect(code = null) {
  const session = await api("/api/session", { code });
  csrf = session.csrf;
  authorized = true;
  $("pairing").hidden = true;
  $("workspace").hidden = false;
  notice();
  stream?.close();
  stream = new EventSource("/api/events");
  stream.addEventListener("state", (event) => {
    streamAlive = true;
    $("engine-link").textContent = "Engine online";
    $("engine-dot").classList.add("live");
    try {
      render(JSON.parse(event.data));
    } catch {
      notice("The engine returned an unexpected state. Reload this display.");
    }
  });
  stream.onerror = async () => {
    streamAlive = false;
    $("engine-link").textContent = "Reconnecting";
    $("engine-dot").classList.remove("live");
    notice(
      "Display connection interrupted. The engine continues recording; this display will reconnect automatically.",
    );
    clearTelemetry();
    // A restarted engine has new session keys. Network interruptions retain the old session.
    try {
      const response = await fetch("/api/state");
      if (response.status === 401) {
        stream.close();
        authorized = false;
        const access = await api("/api/access");
        if (!access.requiresPairing) await connect();
        else {
          $("pairing").hidden = false;
          $("workspace").hidden = true;
          notice(
            "The engine restarted or your session expired. Pair this device again.",
          );
        }
      }
    } catch {
      /* EventSource retries a transient network failure without creating another stream. */
    }
  };
  await navigate();
}
function clearTelemetry() {
  for (const id of [
    "altitude",
    "ias",
    "groundspeed",
    "vertical-speed",
    "pitch",
    "bank",
    "latitude",
    "longitude",
    "heading",
    "mach",
  ])
    $(id).textContent = "—";
  $("tas").textContent = "True airspeed —";
  $("track").textContent = "Track —";
  $("agl").textContent = "Above ground —";
  $("horizon").setAttribute("transform", "translate(0 0)");
  $("connection-dot").classList.remove("live");
}
function render(state) {
  const wasActive = current?.activeMission?.id;
  current = state;
  const t = state.telemetry;
  const fresh = t && Date.now() - Date.parse(t.timestampUtc) < 6000;
  const simulated = state.connection.source === "simulated";
  const connected = fresh && state.connection.state === "connected";
  $("source-badge").textContent = simulated ? "SIMULATED TELEMETRY" : connected ? "SIMCONNECT · LIVE AIRCRAFT" : "LIVE TELEMETRY UNAVAILABLE";
  $("source-badge").className = "badge " + (connected && !simulated ? "green" : "amber");
  $("source-note").textContent = simulated ? "Test fixture data" : "Heading and ground track use true north. Mission History is available without MSFS.";
  $("connection-text").textContent = state.connection.message;
  $("aircraft").textContent = fresh
    ? t.aircraft
    : "Awaiting fresh aircraft data";
  $("sampling-label").textContent =
    `Target ${number(1000 / state.samplingIntervalMs, 2)} Hz · sample cadence ${number(state.sampling?.achievedHz, 2)} Hz · mission average ${number(state.sampling?.missionAverageHz, 2)} Hz · ${number(state.sampling?.missingIntervals ?? 0)} missing intervals`;
  $("connection-dot").classList.toggle(
    "live",
    state.connection.state === "connected" && !!fresh,
  );
  $("sample-time").textContent = fresh
    ? new Date(t.timestampUtc).toISOString().slice(11, 19) + " UTC"
    : "NO FRESH DATA";
  if (fresh) {
    $("altitude").textContent = number(t.altitudeMslFeet);
    $("ias").textContent = number(t.indicatedAirspeedKnots);
    $("mach").textContent = `M ${number(t.machNumber, 2)}`;
    $("groundspeed").textContent = number(t.groundspeedKnots);
    $("vertical-speed").textContent =
      (t.verticalSpeedFeetPerMinute > 0 ? "+" : "") +
      number(t.verticalSpeedFeetPerMinute);
    $("tas").textContent = `True airspeed ${number(t.trueAirspeedKnots)} kts`;
    $("track").textContent = `Track ${number(t.groundTrackDegrees, 1)}° T`;
    $("agl").textContent = `Above ground ${number(t.altitudeAglFeet)} ft`;
    $("pitch").textContent = `${number(t.pitchDegrees, 1)}°`;
    $("bank").textContent = `${number(t.bankDegrees, 1)}°`;
    $("latitude").textContent =
      `${number(Math.abs(t.latitude), 5)}° ${t.latitude >= 0 ? "N" : "S"}`;
    $("longitude").textContent =
      `${number(Math.abs(t.longitude), 5)}° ${t.longitude >= 0 ? "E" : "W"}`;
    $("heading").textContent = `${number(t.headingDegrees, 1)}° T`;
    $("horizon").setAttribute(
      "transform",
      `rotate(${-t.bankDegrees} 110 50) translate(0 ${Math.max(-45, Math.min(45, t.pitchDegrees * 2))})`,
    );
    if (t.timestampUtc !== lastTimestamp) {
      chart.push(t.altitudeMslFeet);
      chart = chart.slice(-60);
      lastTimestamp = t.timestampUtc;
      const min = Math.floor(Math.min(...chart) / 100) * 100 - 100,
        max = Math.ceil(Math.max(...chart) / 100) * 100 + 100;
      $("chart-max").textContent = number(max);
      $("chart-min").textContent = number(min);
      $("altitude-line").setAttribute(
        "d",
        chart
          .map(
            (v, i) =>
              `${i ? "L" : "M"}${(i * 600) / 59},${120 - ((v - min) / (max - min)) * 110}`,
          )
          .join(" "),
      );
    }
  } else clearTelemetry();
  $("start-button").disabled = !fresh || state.connection.state !== "connected" || !!t?.isPaused;
  const mission = state.activeMission;
  $("mission-idle").hidden = !!mission;
  $("mission-active").hidden = !mission;
  $("record-status").textContent = mission ? "● RECORDING" : "STANDBY";
  $("record-status").className = "badge" + (mission ? " green" : "");
  if (mission) {
    $("active-name").textContent = mission.name;
    $("active-samples").textContent = number(mission.sampleCount);
    $("active-mode").textContent = modeHelp(mission.mode);
    $("active-hint").textContent = fresh
      ? "Mission recording"
      : "Mission active · waiting for fresh telemetry";
    renderDuration();
  }
  if (
    streamAlive &&
    $("notice").textContent.startsWith("Display connection interrupted.")
  )
    notice();
  if (wasActive !== mission?.id && location.hash === "#history")
    loadHistory().catch((e) => notice(e.message));
}
function renderDuration() {
  if (current?.activeMission)
    $("active-duration").textContent = duration(
      (Date.now() - Date.parse(current.activeMission.startedUtc)) / 1000,
    );
}
async function command(button, operation) {
  button.disabled = true;
  try {
    notice();
    await operation();
  } catch (e) {
    notice(e.message);
  } finally {
    button.disabled = false;
  }
}
$("start-form").addEventListener("submit", (event) => {
  event.preventDefault();
  command($("start-button"), async () => {
    await api("/api/missions", {
      name: $("mission-name").value,
      mode: $("mission-mode").value,
    });
    $("mission-name").value = "";
    $("mission-mode").value = "universal";
    $("mode-help").textContent = modeHelp("universal");
    render(await api("/api/state"));
  });
});
$("mission-mode").addEventListener("change", () => {
  $("mode-help").textContent = modeHelp($("mission-mode").value);
});
$("end-button").addEventListener("click", () =>
  command($("end-button"), async () => {
    if (!current?.activeMission) return;
    await api(`/api/missions/${current.activeMission.id}/end`, {});
    render(await api("/api/state"));
  }),
);
$("pair-form").addEventListener("submit", (event) => {
  event.preventDefault();
  command(event.submitter, () => connect($("pair-code").value));
});
async function navigate() {
  const allowed = ["control", "history", "instruments", "settings"];
  const page = allowed.includes(location.hash.slice(1))
    ? location.hash.slice(1)
    : "control";
  for (const id of allowed) $(id).hidden = id !== page;
  for (const link of document.querySelectorAll(".nav-link")) {
    const selected = link.hash === "#" + page;
    link.classList.toggle("selected", selected);
    if (selected) link.setAttribute("aria-current", "page");
    else link.removeAttribute("aria-current");
  }
  $("breadcrumb").textContent = {
    control: "MISSION CONTROL",
    history: "MISSION HISTORY",
    instruments: "INSTRUMENTS",
    settings: "SETTINGS",
  }[page];
  document.title = `AeroScience · ${page === "control" ? "Mission Control" : page[0].toUpperCase() + page.slice(1)}`;
  if (!authorized) return;
  if (page === "history") await loadHistory();
  if (page === "settings") await loadSettings();
}
async function loadHistory() {
  const request = ++historyRequest;
  const missions = await api(
    `/api/missions?limit=20&offset=${historyPage * 20}`,
  );
  if (request !== historyRequest) return;
  const list = $("history-list");
  list.replaceChildren();
  if (!missions.length)
    list.append(
      el(
        "div",
        "No missions on this page. Start a research mission from Mission Control.",
        "panel empty-state",
      ),
    );
  for (const m of missions) {
    const card = el("article", undefined, "panel history-card");
    const info = el("div");
    info.append(
      el(
        "span",
        m.status.toUpperCase(),
        "badge" + (m.status === "recording" ? " green" : ""),
      ),
      el("h2", m.name),
      el(
        "p",
        `${m.aircraft || "No aircraft data"} · ${date(m.startedUtc)} · ${modeLabel(m.mode)} · ${m.source === "simulated" ? "SIMULATED" : m.source === "simulator" ? "SIMULATOR" : "NO TELEMETRY"}`,
      ),
    );
    const meta = el("div", undefined, "history-meta");
    meta.append(
      el(
        "span",
        `${duration(m.durationSeconds)} / ${number(m.sampleCount)} samples`,
        "mono",
      ),
    );
    const open = el("button", "View mission", "secondary");
    open.addEventListener("click", () => command(open, () => showDetail(m.id)));
    meta.append(open);
    card.append(info, meta);
    list.append(card);
  }
  $("history-prev").disabled = historyPage === 0;
  $("history-next").disabled = missions.length < 20;
  $("history-page").textContent = `Page ${historyPage + 1}`;
}
async function showDetail(id) {
  const detail = await api(`/api/missions/${id}`),
    m = detail.mission;
  const body = $("detail-content");
  body.replaceChildren(el("h3", m.name));
  const rows = el("dl");
  const coordinates = (v) =>
    v
      ? `${v.latitude.toFixed(5)}, ${v.longitude.toFixed(5)}`
      : "No recorded position";
  for (const [key, value] of [
    ["Status", m.status],
    ["Science mode", modeLabel(m.mode)],
    ["Aircraft", m.aircraft || "Not available"],
    ["Data source", m.source || "No samples"],
    ["Started", date(m.startedUtc)],
    ["Ended", m.endedUtc ? date(m.endedUtc) : "Recording"],
    ["Duration", duration(m.durationSeconds)],
    ["Samples", number(m.sampleCount)],
    ["Departure", coordinates(m.departure)],
    ["Last recorded position", coordinates(m.arrival)],
    [
      "MSL range",
      `${number(m.minimumAltitudeFeet)} – ${number(m.maximumAltitudeFeet)} ft`,
    ],
  ]) {
    const row = el("div");
    row.append(el("dt", key), el("dd", value));
    rows.append(row);
  }
  body.append(
    rows,
    el(
      "p",
      "Event timeline (up to 1,000 events). Interrupted missions preserve committed data; gaps are not filled with invented samples.",
    ),
  );
  const events = el("ol", undefined, "event-list");
  for (const event of detail.events)
    events.append(el("li", `${date(event.timestampUtc)} · ${event.message}`));
  body.append(events);
  const download = el(
    "a",
    "Inspect telemetry JSON (first 5,000 samples)",
    "secondary",
  );
  download.href = `/api/missions/${id}/telemetry?limit=5000`;
  download.target = "_blank";
  download.rel = "noopener";
  body.append(download);
  if (m.status !== "recording") {
    const complete = el("a", "Export complete mission (JSON)", "secondary");
    complete.href = `/api/missions/${id}/export`;
    complete.download = `AeroScience-mission-${id}.json`;
    body.append(complete);
  }
  $("mission-detail").showModal();
}
async function loadSettings() {
  const s = await api("/api/settings"),
    container = $("settings-content");
  container.replaceChildren();
  const rows = el("dl");
  for (const [name, value] of [
    ["Application", `AeroScience ${s.version}`],
    ["Telemetry source", s.telemetry],
    ["Sampling interval", `${s.samplingIntervalMs} ms`],
    ["SimConnect", s.simConnect],
    ["LAN access", s.enableLan ? s.lanUrl : "Disabled — localhost only"],
    ["User data", s.dataDirectory || "Stored on the Windows engine"],
    ...(s.pairingCode ? [["Pairing code", s.pairingCode]] : []),
  ]) {
    const row = el("div");
    row.append(el("dt", name), el("dd", value));
    rows.append(row);
  }
  container.append(rows);
  const controls = el("div", undefined, "input-row");
  for (const [label, action] of [["Application settings", "settings"], ["Storage", "storage"], ["Collect Feedback", "feedback"]]) {
    const button = el("button", label, "secondary");
    button.disabled = !s.desktopAvailable;
    button.addEventListener("click", () => command(button, () => api(`/api/desktop/${action}`, {})));
    controls.append(button);
  }
  container.append(controls);
  for (const [title, storage] of [["Mission Database", s.missionStorage], ["Scientific Data Library", s.scienceStorage]]) {
    const section = el("section"); section.append(el("h2", title));
    if (storage) {
      section.append(el("p", storage.directory), el("p", storage.available
        ? `${number(storage.usedBytes / 1048576, 1)} MiB used · ${number(storage.freeBytes / 1073741824, 1)} GiB available`
        : storage.error));
    } else section.append(el("p", "Manage local storage in the AeroScience Windows application."));
    container.append(section);
  }
  container.append(el("p", "Mission History and exports work offline while the selected archive is available. LAN access is optional."));
}
$("close-detail").addEventListener("click", () => $("mission-detail").close());
$("refresh-history").addEventListener("click", (event) =>
  command(event.target, loadHistory),
);
$("history-prev").addEventListener("click", () => {
  historyPage = Math.max(0, historyPage - 1);
  loadHistory().catch((e) => notice(e.message));
});
$("history-next").addEventListener("click", () => {
  historyPage++;
  loadHistory().catch((e) => notice(e.message));
});
window.addEventListener("hashchange", () =>
  navigate().catch((e) => notice(e.message)),
);
setInterval(() => {
  $("utc-clock").textContent = new Date().toISOString().slice(11, 19) + " UTC";
  renderDuration();
  if (
    current?.telemetry &&
    Date.now() - Date.parse(current.telemetry.timestampUtc) > 6000
  )
    clearTelemetry();
}, 1000);
try {
  const access = await api("/api/access");
  if (access.requiresPairing) {
    $("pairing").hidden = false;
    await navigate();
  } else await connect();
} catch (e) {
  notice(e.message);
  $("pairing").hidden = false;
}
