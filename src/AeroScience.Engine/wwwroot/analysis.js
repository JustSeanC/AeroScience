import { displayReading, planningField, planningDistance } from "./science-display.js";
const el = (tag, value) => {
  const e = document.createElement(tag);
  if (value !== undefined) e.textContent = value;
  return e;
};
export async function renderAnalysis(parent, id, plan, api) {
  const panel = el("details");
  panel.className = "panel research-panel";
  panel.append(el("summary", "Mission Analysis · Planned vs recorded flight"));
  parent.append(panel);
  if (!plan) {
    panel.append(
      el(
        "p",
        "This mission has no saved plan snapshot. Its original track and science observations remain available below.",
      ),
    );
    return;
  }
  const data = await api(`/api/missions/${id}/analysis`);
  const grid = el("div");
  grid.className = "research-grid";
  panel.append(grid);
  const input = (name, value) => {
    const label = el("label", name),
      e = el("input");
    e.type = "number";
    e.value = value;
    label.append(e);
    grid.append(label);
    return e;
  };
  const tolerance = input(
      "Waypoint tolerance (m)",
      data.settings.waypointToleranceMetres,
    ),
    gap = input(
      "Maximum connected telemetry gap (s)",
      data.settings.maximumGapSeconds,
    );
  planningField(tolerance,"distance",data.settings.waypointToleranceMetres,"Waypoint tolerance");
  gap.min = 1;
  const refresh = el("button", "Recompute comparison");
  refresh.className = "secondary";
  refresh.type = "button";
  grid.append(refresh);
  refresh.onclick = async () => {
    refresh.disabled = true;
    try {
      await api(`/api/missions/${id}/analysis`, {
        ...data.settings,
        waypointToleranceMetres: tolerance.planningUnits.read(),
        maximumGapSeconds: Number(gap.value),
      });
      const holder = el("div");
      await renderAnalysis(holder, id, plan, api);
      panel.replaceWith(holder.firstChild);
      holder.firstChild?.setAttribute("open", "");
    } finally {
      refresh.disabled = false;
    }
  };
  const distance = planningDistance;
  panel.append(
    el(
      "p",
      `Planned route: ${distance(data.plannedDistanceMetres)} · Recorded connected track: ${distance(data.recordedDistanceMetres)} · Excluded gaps: ${data.excludedGaps}`,
    ),
  );
  const stats = data.deviationStatistics;
  if (stats)
    panel.append(
      el(
        "p",
        `Lateral route deviation: mean ${stats.meanMetres == null ? "Unavailable" : distance(stats.meanMetres)} · maximum ${stats.maximumMetres == null ? "Unavailable" : distance(stats.maximumMetres)} · ${stats.samples} observed positions.`,
      ),
    );
  for (const segment of data.segments || [])
    panel.append(
      el(
        "p",
        `Segment ${segment.index + 1}: ${segment.from} → ${segment.to} · ${segment.observedWithinTolerance ? "Recorded path observed within tolerance" : "Completion not established"} · ${segment.samples} positions · ${segment.excludedGaps} excluded gaps${segment.maximumDeviationMetres == null ? "" : " · max deviation " + distance(segment.maximumDeviationMetres)}`,
      ),
    );
  const table = el("table"),
    head = el("tr");
  for (const h of [
    "Waypoint",
    "Ordered visit",
    "Closest observed proximity",
    "Reached UTC",
    "Altitude error",
  ])
    head.append(el("th", h));
  table.append(head);
  for (const w of data.waypoints) {
    const row = el("tr");
    for (const value of [
      w.name,
      w.reached ? "Observed in sequence" : "Not observed in sequence",
      w.closestDistanceMetres == null
        ? "Unavailable"
        : distance(w.closestDistanceMetres),
      w.reachedUtc || "—",
      w.altitudeErrorFeet == null
        ? "—"
        : displayReading("altitude", w.altitudeErrorFeet, "ft").value.toFixed(
            0,
          ) +
          " " +
          displayReading("altitude", 0, "ft").unit.replace(" MSL", ""),
    ])
      row.append(el("td", value));
    table.append(row);
  }
  const scroll = el("div");
  scroll.className = "table-scroll";
  scroll.append(table);
  panel.append(scroll);
  for (const area of data.surveys)
    panel.append(
      el(
        "p",
        `${area.name}: ${area.samplesInside} observed positions inside · ${distance(area.traversedMetres)} recorded path inside. ${area.coverageExplanation}`,
      ),
    );
  const diagnostic = el("details");
  diagnostic.append(
    el("summary", "Methods, limitations and deviation observations"),
  );
  for (const text of data.limitations) diagnostic.append(el("p", text));
  for (const row of data.deviations.filter(
    (_, i) => i % Math.max(1, Math.ceil(data.deviations.length / 100)) === 0,
  ))
    diagnostic.append(
      el(
        "p",
        `${row.timestampUtc} · segment ${row.segment + 1} · ${distance(row.distanceMetres)} route deviation`,
      ),
    );
  panel.append(diagnostic);
  const download = el("button", "Export comparison JSON");
  download.type = "button";
  download.className = "secondary";
  download.onclick = () => {
    const url = URL.createObjectURL(
      new Blob([JSON.stringify(data, null, 2)], { type: "application/json" }),
    );
    const a = el("a");
    a.href = url;
    a.download = `AeroScience-${id}-plan-comparison.json`;
    a.click();
    URL.revokeObjectURL(url);
  };
  panel.append(download);
}
