import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {dialogHeader,requestDialogClose,markDialogSaved} from './dialog-ui.js';
import { planningDistance, planningText } from "./science-display.js";
const el = (tag, text) => {
  const e = document.createElement(tag);
  if (text !== undefined) setPresentationText(e,text);
  return e;
};
export function routeExportButton(parent, getPlan, api) {
  const open = el("button", localizedCopy("ui.routeExport.messageExportShareRoute"));
  open.type = "button";
  open.className = "secondary";
  parent.append(open);
  open.onclick = () => {
    const dialog = el("dialog"),
      content = el("div");
    content.className = "route-export";
    dialog.append(content);
    document.body.append(dialog);
    content.append(el("h2", localizedCopy("ui.routeExport.messageExportSharePlannedRoute")));
    const format = el("select");
    setPresentationText(format,localizedCopy("ui.routeExport.messageExportFormat"),"aria-label");
    for (const [id, name] of [
      ["pln", textForUI("ui.routeExport.messageDownloadMSFS2024EFBFlightPlanPln")],
      ["route", textForUI("ui.routeExport.messageCopyRouteString")],
      ["coordinates", textForUI("ui.routeExport.messageCopyWaypointCoordinates")],
      ["gpx", textForUI("ui.routeExport.messageDownloadPlannedRouteGPX")],
      ["kml", textForUI("ui.routeExport.messageDownloadPlannedRouteKML")],
      ["csv", textForUI("ui.routeExport.messageDownloadWaypointListCSV")],
    ]) {
      const o = el("option", name);
      o.value = id;
      format.append(o);
    }
    const plan = getPlan();
    const orbitCount=(plan.patterns||[]).filter(d=>d.kind==="orbit").length;
    content.append(el("p",localizedCopy("ui.routeExport.messageNavigationWaypointsWillBeExportedUnchanged",{value1:(String(plan.waypoints?.length||0)),value2:(String(orbitCount?` ${orbitCount} orbit(s) use a 5° point-to-point approximation; one revolution normally has 73 points. This is not a native EFB orbit command. Check target route limits.`:""))})));
    content.append(format);
    const coordinate = el("select");
    setPresentationText(coordinate,localizedCopy("ui.routeExport.messageCoordinateFormat"),"aria-label");
    for (const [id, name] of [
      ["decimal", textForUI("ui.routeExport.messageDecimalDegrees8Decimals")],
      ["ddm", textForUI("ui.routeExport.messageDegreesAndDecimalMinutes")],
      ["dms", textForUI("ui.routeExport.messageDegreesMinutesAndSeconds")],
      ["icao", textForUI("ui.routeExport.messageICAOItem15DegreesMinutesRounded")],
      ["simbrief", "SimBrief documented degrees/minutes/seconds"],
    ]) {
      const o = el("option", name);
      o.value = id;
      coordinate.append(o);
    }
    content.append(coordinate);
    const style = el("select");
    setPresentationText(style,localizedCopy("ui.routeExport.messageRouteStringStyle"),"aria-label");
    for (const [id, name] of [
      ["coordinates", textForUI("ui.routeExport.messageCoordinateWaypointSequence")],
      ["named", textForUI("ui.routeExport.messagePublishedNavigationIdentifiers")],
      ["detailed", textForUI("ui.routeExport.messageHumanReadableFullScienceRoute")],
    ]) {
      const o = el("option", name);
      o.value = id;
      style.append(o);
    }
    content.append(style);
    const ack = el("input");
    ack.type = "checkbox";
    const ackLabel = el(
      "label",
      localizedCopy("ui.routeExport.messageExportEveryWaypointUnchangedIUnderstand"),
    );
    ackLabel.prepend(ack);
    content.append(ackLabel);
    const note = el(
        "p",
        localizedCopy("ui.routeExport.messageMSFSEFBExportRequiresACruisingAltitude"),
      ),
      preview = el("button", localizedCopy("ui.routeExport.messagePreviewExport")),
      summary = el("p"),
      warnings = el("p"),
      output = el("textarea"),
      action = el("button", localizedCopy("ui.routeExport.messageDownload")),
      close = el("button", localizedCopy("ui.aviationReference.messageClose"));
    output.readOnly = true;
    output.rows = 10;
    setPresentationText(output,localizedCopy("ui.routeExport.messageExportTextPreview"),"aria-label");
    action.disabled = true;
    preview.type = action.type = close.type = "button";
    let result = null;
    const invalidate = () => {
      result = null;
      action.disabled = true;
      output.value = "";
      setPresentationText(summary,localizedCopy("ui.routeExport.messagePreviewAgainAfterChangingExportOptions"));
    };
    for (const c of [format, coordinate, style, ack])
      c.onchange = () => {
        if (
          format.value === "route" &&
          style.value === "coordinates" &&
          !["icao", "simbrief"].includes(coordinate.value)
        )
          coordinate.value = "simbrief";
        invalidate();
      };
    preview.onclick = async () => {
      preview.disabled = true;
      try {
        result = await api("/api/routes/export", {
          plan: getPlan(),
          format: format.value,
          coordinateFormat: coordinate.value,
          routeStyle: style.value,
          acknowledgeLongRoute: ack.checked,
        });
        const p = result.preview;
        planningText(summary,()=>textForUI("ui.routeExport.messageWaypoints",{value1:(String(p.name)),value2:(String(p.departure || "First en-route waypoint")),value3:(String(p.destination || "Last en-route waypoint")),value4:(String(p.waypoints)),value5:(String(planningDistance(p.distanceMetres))),value6:(String(p.patterns.join(", ") || "Custom route")),value7:(String(p.format))}));
        setPresentationText(warnings,p.warnings.join(" "));
        output.value = result.content;
        setPresentationText(action,["route", "coordinates"].includes(format.value)
          ? localizedCopy("ui.routeExport.messageCopy")
          : localizedCopy("ui.routeExport.messageDownload"));
        action.disabled = false;
      } catch (e) {
        setPresentationText(warnings,e.message);
        result = null;
        action.disabled = true;
      } finally {
        preview.disabled = false;
      }
    };
    action.onclick = async () => {
      if (!result) return;
      try {
        if (["route", "coordinates"].includes(format.value)) {
          if (navigator.clipboard?.writeText)
            await navigator.clipboard.writeText(result.content);
          else {
            output.focus();
            output.select();
            if (!document.execCommand("copy"))
              throw Error(textForUI("ui.routeExport.messageSelectThePreviewTextAndCopyIt"));
          }
          setPresentationText(warnings,localizedCopy("ui.routeExport.messageCopiedTheCompletePreviewTargetApplicationInput"));
        } else {
          const url = URL.createObjectURL(
              new Blob([result.content], { type: result.contentType }),
            ),
            a = el("a");
          a.href = url;
          a.download = result.fileName;
          document.body.append(a);
          a.click();
          a.remove();
          setTimeout(() => URL.revokeObjectURL(url), 1000);
        }
      } catch (e) {
        setPresentationText(warnings,e.message);
      }
    };
    close.onclick = () => requestDialogClose(dialog);
    dialog.onclose = () => dialog.remove();
    content.append(note, preview, summary, warnings, output, action, close);
    dialogHeader(dialog,{title:localizedCopy("ui.routeExport.messageExportSharePlannedRoute")});dialog.showModal();
  };
}
