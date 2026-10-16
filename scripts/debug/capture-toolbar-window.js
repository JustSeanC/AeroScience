/* Paste into the AeroScience view's Coherent GT console, not PowerShell.
 * Opt-in, bounded, passive observation. Not loaded by either toolbar page.
 * No mission text/values, CommBus payloads, native source, network or files.
 * Usage and interpretation: docs/toolbar-interactions.md. */
(function () {
  "use strict";
  const frame = document.getElementById("aeroscience-frame");
  if (!frame) throw new Error("Select the AeroScience panel view before running this capture.");
  if (window.AeroScienceWindowDebug) window.AeroScienceWindowDebug.stop();
  const entries = [], timers = new Set();
  let stopped = false;
  const limit = 120;
  const round = n => Math.round(n * 10) / 10;
  const identity = node => node && node.nodeType === 1
    ? node.tagName.toLowerCase() + (node.id ? "#" + node.id : "") +
      (node.getAttribute("class") ? "." + node.getAttribute("class").trim().split(/\s+/).slice(0, 8).join(".") : "")
    : null;
  const rect = node => {
    if (!node || !node.getBoundingClientRect) return null;
    const r = node.getBoundingClientRect();
    return { x: round(r.left), y: round(r.top), width: round(r.width), height: round(r.height) };
  };
  const record = entry => {
    if (stopped) return;
    entries.push(Object.assign({ utc: new Date().toISOString() }, entry));
    if (entries.length > limit) entries.shift();
  };
  const inspect = node => {
    const style = getComputedStyle(node), computed = {};
    for (const key of ["display", "position", "width", "height", "minWidth", "minHeight",
      "overflowX", "overflowY", "pointerEvents", "cursor", "zIndex", "transform",
      "boxSizing", "flexDirection", "flexGrow", "flexShrink", "flexBasis"])
      computed[key] = style[key];
    const attributes = {};
    for (const key of ["panel-id", "resize", "content-fit", "min-width", "min-height", "auto-inside", "data-input-group", "style"])
      if (node.hasAttribute(key)) attributes[key] = node.getAttribute(key);
    return { element: identity(node), parent: identity(node.parentElement), rect: rect(node),
      clientWidth: node.clientWidth, clientHeight: node.clientHeight,
      scrollWidth: node.scrollWidth, scrollHeight: node.scrollHeight, scrollTop: node.scrollTop,
      computed, attributes };
  };
  const snapshot = label => {
    const r = frame.getBoundingClientRect(), nodes = [document.documentElement, document.body];
    if (frame.parentElement) nodes.push(frame.parentElement);
    nodes.push(frame);
    // Include actual generated native children: do not assume a resize-handle selector.
    nodes.push.apply(nodes, Array.from(frame.querySelectorAll("*")).slice(0, 200));
    const samples = [
      ["top-centre", r.left + r.width / 2, r.top + 8],
      ["left-inside", r.left + 2, r.top + r.height / 2],
      ["left-outside", r.left - 2, r.top + r.height / 2],
      ["right-inside", r.right - 2, r.top + r.height / 2],
      ["bottom-centre", r.left + r.width / 2, r.bottom - 2],
      ["bottom-left", r.left + 2, r.bottom - 2],
      ["bottom-right", r.right - 2, r.bottom - 2]
    ].map(point => ({ point: point[0], x: round(point[1]), y: round(point[2]),
      hit: identity(document.elementFromPoint(point[1], point[2])) }));
    const entry = { kind: "snapshot", label: String(label || "manual").slice(0, 100),
      elements: nodes.map(inspect), hitSamples: samples,
      nativeMemberNames: Object.getOwnPropertyNames(frame).filter(name => /drag|resize|header|wrapper/i.test(name)),
      nativePrototypeNames: Object.getOwnPropertyNames(Object.getPrototypeOf(frame)) };
    record(entry);
    return entry;
  };
  const path = event => {
    if (typeof event.composedPath === "function") return event.composedPath().slice(0, 12).map(identity).filter(Boolean);
    const result = [];
    for (let node = event.target; node && result.length < 12; node = node.parentElement)
      if (identity(node)) result.push(identity(node));
    return result;
  };
  const onInput = event => {
    const scroll = document.getElementById("as-scroll") || document.getElementById("window-probe-scroll");
    const entry = { kind: "input", type: event.type, target: identity(event.target), path: path(event),
      targetGeometry: event.target && event.target.nodeType === 1 ? inspect(event.target) : null,
      x: event.clientX, y: event.clientY, buttons: event.buttons,
      deltaY: event.deltaY, hit: identity(document.elementFromPoint(event.clientX, event.clientY)),
      defaultPreventedAtCapture: event.defaultPrevented, frame: rect(frame), scrollTop: scroll ? scroll.scrollTop : null };
    record(entry);
    // Observe after native/bubbling handlers and a wheel scroll have had time to run.
    const timer = setTimeout(() => {
      timers.delete(timer);
      entry.defaultPreventedAfterDispatch = event.defaultPrevented;
      record({ kind: "after-input", type: event.type, frame: rect(frame), scrollTop: scroll ? scroll.scrollTop : null });
    }, 100);
    timers.add(timer);
  };
  const onError = event => record({ kind: "error", message: String(event.message || "Script error").slice(0, 500),
    file: event.filename, line: event.lineno, column: event.colno });
  const types = ["mousedown", "mouseup", "pointerdown", "pointerup", "wheel"];
  for (const type of types) document.addEventListener(type, onInput, { capture: true, passive: true });
  window.addEventListener("error", onError);
  window.AeroScienceWindowDebug = {
    snapshot,
    report: () => JSON.stringify({ captureVersion: 1, nativeSuccess: "not established by this capture",
      page: location.pathname, viewport: { width: innerWidth, height: innerHeight, devicePixelRatio },
      stopped, limit, entries }, null, 2),
    stop: () => {
      for (const type of types) document.removeEventListener(type, onInput, true);
      window.removeEventListener("error", onError);
      for (const timer of timers) clearTimeout(timer);
      timers.clear(); stopped = true;
    }
  };
  snapshot("capture-start");
  return "AeroScience passive capture ready. Reproduce title-bar, left-edge and wheel input; snapshot, stop, then report.";
})();
