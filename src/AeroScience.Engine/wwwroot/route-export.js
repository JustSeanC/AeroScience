const el = (tag, text) => {
  const e = document.createElement(tag);
  if (text !== undefined) e.textContent = text;
  return e;
};
export function routeExportButton(parent, getPlan, api) {
  const open = el("button", "Export / Share Route");
  open.type = "button";
  open.className = "secondary";
  parent.append(open);
  open.onclick = () => {
    const dialog = el("dialog"),
      content = el("div");
    content.className = "route-export";
    dialog.append(content);
    document.body.append(dialog);
    content.append(el("h2", "Export / Share Planned Route"));
    const format = el("select");
    format.setAttribute("aria-label", "Export format");
    for (const [id, name] of [
      ["pln", "Download MSFS 2024 EFB Flight Plan (.pln)"],
      ["route", "Copy Route String"],
      ["coordinates", "Copy Waypoint Coordinates"],
      ["gpx", "Download Planned Route GPX"],
      ["kml", "Download Planned Route KML"],
      ["csv", "Download Waypoint List CSV"],
    ]) {
      const o = el("option", name);
      o.value = id;
      format.append(o);
    }
    content.append(format);
    const coordinate = el("select");
    coordinate.setAttribute("aria-label", "Coordinate format");
    for (const [id, name] of [
      ["decimal", "Decimal degrees — 8 decimals"],
      ["ddm", "Degrees and decimal minutes"],
      ["dms", "Degrees, minutes and seconds"],
      ["icao", "ICAO Item 15 degrees/minutes — rounded"],
      ["simbrief", "SimBrief documented degrees/minutes/seconds"],
    ]) {
      const o = el("option", name);
      o.value = id;
      coordinate.append(o);
    }
    content.append(coordinate);
    const style = el("select");
    style.setAttribute("aria-label", "Route string style");
    for (const [id, name] of [
      ["coordinates", "Coordinate waypoint sequence"],
      ["named", "Published navigation identifiers"],
      ["detailed", "Human-readable full science route"],
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
      " Export every waypoint unchanged. I understand long-route compatibility is unverified.",
    );
    ackLabel.prepend(ack);
    content.append(ackLabel);
    const note = el(
        "p",
        "MSFS EFB export requires a cruising altitude in the plan. Airports are optional. Named routes require explicitly entered published identifiers; science names are not assumed to be navigation fixes. No flight plan is uploaded or filed.",
      ),
      preview = el("button", "Preview export"),
      summary = el("p"),
      warnings = el("p"),
      output = el("textarea"),
      action = el("button", "Download"),
      close = el("button", "Close");
    output.readOnly = true;
    output.rows = 10;
    output.setAttribute("aria-label", "Export text preview");
    action.disabled = true;
    preview.type = action.type = close.type = "button";
    let result = null;
    const invalidate = () => {
      result = null;
      action.disabled = true;
      output.value = "";
      summary.textContent = "Preview again after changing export options.";
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
        summary.textContent = `${p.name} · ${p.departure || "First en-route waypoint"} → ${p.destination || "Last en-route waypoint"} · ${p.waypoints} waypoints · ${(p.distanceMetres / 1852).toFixed(2)} NM · ${p.patterns.join(", ") || "Custom route"} · ${p.format}`;
        warnings.textContent = p.warnings.join(" ");
        output.value = result.content;
        action.textContent = ["route", "coordinates"].includes(format.value)
          ? "Copy"
          : "Download";
        action.disabled = false;
      } catch (e) {
        warnings.textContent = e.message;
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
              throw Error("Select the preview text and copy it manually.");
          }
          warnings.textContent =
            "Copied the complete preview. Target application input limits and compatibility still apply.";
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
        warnings.textContent = e.message;
      }
    };
    close.onclick = () => dialog.close();
    dialog.onclose = () => dialog.remove();
    content.append(note, preview, summary, warnings, output, action, close);
    dialog.showModal();
  };
}
