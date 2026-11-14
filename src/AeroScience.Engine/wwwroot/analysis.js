import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import { displayReading, planningField, planningDistance } from "./science-display.js";
const el = (tag, value) => {
  const e = document.createElement(tag);
  if (value !== undefined) setPresentationText(e,value);
  return e;
};
export async function renderAnalysis(parent, id, plan, api) {
  const panel = el("details");
  panel.className = "panel research-panel";
  panel.append(el("summary", localizedCopy("ui.analysis.messageMissionAnalysisPlannedVsRecordedFlight")));
  parent.append(panel);
  if (!plan) {
    panel.append(
      el(
        "p",
        localizedCopy("ui.analysis.messageThisMissionHasNoSavedPlanSnapshot"),
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
      localizedCopy("ui.analysis.messageWaypointToleranceM"),
      data.settings.waypointToleranceMetres,
    ),
    gap = input(
      localizedCopy("ui.analysis.messageMaximumConnectedTelemetryGapS"),
      data.settings.maximumGapSeconds,
    );
  planningField(tolerance,"distance",data.settings.waypointToleranceMetres,localizedCopy("ui.analysis.messageWaypointTolerance"));
  gap.min = 1;
  const refresh = el("button", localizedCopy("ui.analysis.messageRecomputeComparison"));
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
      localizedCopy("ui.analysis.messagePlannedRouteRecordedConnectedTrack",{value1:(String(distance(data.plannedDistanceMetres))),value2:(String(distance(data.recordedDistanceMetres))),value3:(String(data.excludedGaps))}),
    ),
  );
  const stats = data.deviationStatistics;
  if (stats)
    panel.append(
      el(
        "p",
        localizedCopy("ui.analysis.messageLateralRouteDeviationMeanMaximum",{value1:(String(stats.meanMetres == null ? "Unavailable" : distance(stats.meanMetres))),value2:(String(stats.maximumMetres == null ? "Unavailable" : distance(stats.maximumMetres))),value3:(String(stats.samples))}),
      ),
    );
  for (const segment of data.segments || [])
    panel.append(
      el(
        "p",
        localizedCopy("ui.analysis.messageSegmentPositions",{value1:(String(segment.index + 1)),value2:(String(segment.from)),value3:(String(segment.to)),value4:(String(segment.observedWithinTolerance ? "Recorded path observed within tolerance" : "Completion not established")),value5:(String(segment.samples)),value6:(String(segment.excludedGaps)),value7:(String(segment.maximumDeviationMetres == null ? "" : " · max deviation " + distance(segment.maximumDeviationMetres)))}),
      ),
    );
  const table = el("table"),
    head = el("tr");
  for (const h of [
    textForUI("ui.analysis.messageWaypoint"),
    textForUI("ui.analysis.messageOrderedVisit"),
    textForUI("ui.analysis.messageClosestObservedProximity"),
    textForUI("ui.analysis.messageReachedUTC"),
    textForUI("ui.analysis.messageAltitudeError"),
  ])
    head.append(el("th", h));
  table.append(head);
  for (const w of data.waypoints) {
    const row = el("tr");
    for (const value of [
      w.name,
      w.reached ? textForUI("ui.analysis.messageObservedInSequence") : textForUI("ui.analysis.messageNotObservedInSequence"),
      w.closestDistanceMetres == null
        ? textForUI("localization.unavailableValue")
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
        localizedCopy("ui.analysis.messageObservedPositionsInsideRecorded",{value1:(String(area.name)),value2:(String(area.samplesInside)),value3:(String(distance(area.traversedMetres))),value4:(String(area.coverageExplanation))}),
      ),
    );
  const diagnostic = el("details");
  diagnostic.append(
    el("summary", localizedCopy("ui.analysis.messageMethodsLimitationsAndDeviationObservations")),
  );
  for (const text of data.limitations) diagnostic.append(el("p", text));
  for (const row of data.deviations.filter(
    (_, i) => i % Math.max(1, Math.ceil(data.deviations.length / 100)) === 0,
  ))
    diagnostic.append(
      el(
        "p",
        localizedCopy("ui.analysis.messageSegmentRouteDeviation",{value1:(String(row.timestampUtc)),value2:(String(row.segment + 1)),value3:(String(distance(row.distanceMetres)))}),
      ),
    );
  panel.append(diagnostic);
  const download = el("button", localizedCopy("ui.analysis.messageExportComparisonJSON"));
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
