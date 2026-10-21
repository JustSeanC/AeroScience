import { openHyperspectralImport } from "./hyperspectral-import.js";
import { ResearchMap } from "./map.js";
import {
  node as el,
  bytes,
  dateTime,
  button,
  link,
  select,
  disclosure,
  status,
  table,
} from "./workstation-ui.js";

// Every catalog/preview/control operates on the shared backend; UI state never
// pretends a download has completed. Geographic map drawing is optional.
export async function libraryDownload(parent, api, isDisposed = () => false) {
  let map,
    stopped = false,
    timer,
    epoch = 0,
    areas = [],
    polygon = null,
    regionGeometry = null,
    results = [],
    chosen = new Map(),
    preview = null,
    query = null;
  const cleanup = () => {
    stopped = true;
    clearTimeout(timer);
    map?.destroy();
  };
  const heading = el("div", undefined, "page-heading");
  heading.append(
    el("p", "SCIENTIFIC DATA LIBRARY", "eyebrow"),
    el("h1", "Browse & Download Data"),
    el(
      "p",
      "Discover historical numeric Earth data, approve bounded work and retain verified sources for future flights. No active flight is required.",
    ),
  );
  parent.append(heading);
  link(heading, "Retained collections", "#library");
  link(heading, "Scientific access & privacy", "#settings/science");
  const feedback = status(
    parent,
    "Not searched — select a provider and geographic region. Online science consent is separate from basemaps.",
  );
  const form = el("section", undefined, "panel");
  form.setAttribute("aria-label", "Scientific catalog controls");
  parent.append(form);
  const providers = await api("/api/science/library/providers");
  if (stopped || isDisposed()) return cleanup;
  const category = select(form, "Data family", [["all", "Terrain, thermal & multispectral"], ["hyperspectral", "Hyperspectral · NASA"]], "all");
  const provider = select(
    form,
    "Scientific provider / product",
    providers.map((p) => [p.id, `${p.name} · ${p.product}`]),
    "usgs-3dep",
  );
  const details = el("p");
  form.append(details);
  const filters = el("div", undefined, "workspace-controls");
  form.append(filters);
  function input(parent, label, value, type = "text") {
    const wrap = el("label", label),
      i = el("input");
    i.type = type;
    i.value = value;
    i.setAttribute("aria-label", label);
    wrap.append(i);
    parent.append(wrap);
    return i;
  }
  const from = input(filters, "Scene date from (UTC)", "2024-01-01", "date"),
    to = input(
      filters,
      "Scene date to (UTC)",
      new Date().toISOString().slice(0, 10),
      "date",
    ),
    cloud = input(filters, "Maximum scene cloud (%)", "30", "number");
  const sceneFilter = input(filters, "Scene / flightline identifier contains", "");
  cloud.min = 0;
  cloud.max = 100;
  const cached = input(
    filters,
    "Use cached catalog / boundaries only",
    "",
    "checkbox",
  );
  cached.checked = !navigator.onLine;
  const measurements = el("p");
  form.append(measurements);
  const selection = el("section", undefined, "panel");
  selection.setAttribute("aria-label", "Geographic selection");
  selection.append(el("h2", "Select a geographic area"));
  parent.append(selection);
  const coords = el("div", undefined, "workspace-controls");
  selection.append(coords);
  const west = input(coords, "West longitude", "-75.01", "number"),
    south = input(coords, "South latitude", "39.99", "number"),
    east = input(coords, "East longitude", "-74.99", "number"),
    north = input(coords, "North latitude", "40.01", "number");
  for (const i of [west, south, east, north]) i.step = "any";
  function bounds() {
    const a = {
      west: Number(west.value),
      south: Number(south.value),
      east: Number(east.value),
      north: Number(north.value),
    };
    if (
      [west, south, east, north].some((i) => i.value.trim() === "") ||
      Object.values(a).some((n) => !Number.isFinite(n)) ||
      a.west >= a.east ||
      a.south >= a.north ||
      a.west < -180 ||
      a.east > 180 ||
      a.south < -80 ||
      a.north > 84
    )
      throw Error(
        "Enter ordered WGS84 bounds within 180°W–180°E and 80°S–84°N. Split dateline-crossing selections.",
      );
    return a;
  }
  function setBounds(a) {
    west.value = a.west;
    south.value = a.south;
    east.value = a.east;
    north.value = a.north;
  }
  const selectionText = status(selection, "No download area selected.");
  const actions = el("div", undefined, "action-row");
  selection.append(actions);
  button(actions, "Use bounding box", () => {
    polygon = regionGeometry = null;
    areas = [bounds()];
    changed();
    fitSelection();
  });
  button(actions, "Add bounding box", () => {
    polygon = regionGeometry = null;
    if (areas.length >= 256)
      throw Error("Use no more than 256 explicitly selected boxes.");
    areas.push(bounds());
    changed();
    fitSelection();
  });
  button(actions, "Use map viewport", () => {
    const b = map.map.getBounds();
    setBounds({
      west: b.getWest(),
      south: b.getSouth(),
      east: b.getEast(),
      north: b.getNorth(),
    });
    areas = [bounds()];
    polygon = regionGeometry = null;
    changed();
  });
  button(actions, "Clear selection", () => {
    areas = [];
    polygon = regionGeometry = null;
    changed();
  });
  const mode = select(
    selection,
    "Map selection tool",
    [
      ["pan", "Pan / inspect"],
      ["cell", "Select one 0.02° source cell"],
      ["cells", "Add individual source cells"],
      ["polygon", "Draw polygon vertices"],
    ],
    "pan",
  );
  const holder = el("div", undefined, "library-download-map");
  parent.append(holder);
  map = new ResearchMap(holder, (point) => {
    if (mode.value === "polygon") {
      polygon ??= [];
      polygon.push(point);
      regionGeometry = null;
      map.draft(polygon, true);
      preview = null;
      selectionText.textContent = `${polygon.length} polygon vertices; choose Finish polygon to validate.`;
      return;
    }
    const a = {
      west: Math.floor(point.longitude / 0.02) * 0.02,
      south: Math.floor(point.latitude / 0.02) * 0.02,
    };
    a.east = a.west + 0.02;
    a.north = a.south + 0.02;
    areas = mode.value === "cells" ? [...areas, a] : [a];
    polygon = regionGeometry = null;
    setBounds(a);
    changed();
  });
  map.map.setView([40, -75], 10);
  mode.onchange = () =>
    map.interaction(mode.value === "pan" ? null : mode.value);
  const L = map.L,
    layers = {};
  for (const name of ["retained", "remote", "selected", "jobs"])
    layers[name] = L.layerGroup().addTo(map.map);
  const legend = el("ul", undefined, "library-legend");
  legend.setAttribute("aria-label", "Scientific coverage legend");
  for (const [name, text, color] of [
    [
      "retained",
      "Downloaded locally: verified retained numeric footprint",
      "#45d6bf",
    ],
    [
      "remote",
      "Available from provider: catalog outline, QA not guaranteed",
      "#72aaff",
    ],
    ["selected", "Selected: requested numeric windows", "#ffd166"],
    [
      "jobs",
      "Queued / downloading: approved work; failed work shown red",
      "#c09bff",
    ],
    [
      "unsearched",
      "Not searched: no provider availability established",
      "#8899aa",
    ],
  ]) {
    const li = el("li", text);
    li.style.borderLeft = `5px solid ${color}`;
    li.dataset.coverage = name;
    legend.append(li);
  }
  parent.append(legend);
  const polygonDetails = disclosure(
    selection,
    "Accessible polygon coordinates",
  );
  const vertices = el("textarea");
  vertices.setAttribute(
    "aria-label",
    "Polygon vertices (latitude, longitude per line)",
  );
  vertices.rows = 5;
  polygonDetails.append(vertices);
  function finishPolygon() {
    if (!polygon || polygon.length < 3)
      throw Error("Add at least three polygon vertices.");
    const a = {
      west: Math.min(...polygon.map((p) => p.longitude)),
      south: Math.min(...polygon.map((p) => p.latitude)),
      east: Math.max(...polygon.map((p) => p.longitude)),
      north: Math.max(...polygon.map((p) => p.latitude)),
    };
    setBounds(a);
    areas = [bounds()];
    vertices.value = polygon
      .map((p) => `${p.latitude}, ${p.longitude}`)
      .join("\n");
    changed();
    fitSelection();
  }
  button(polygonDetails, "Apply polygon coordinates", () => {
    polygon = vertices.value
      .trim()
      .split("\n")
      .map((line) => {
        const v = line.split(",").map(Number);
        if (v.length !== 2 || v.some((n) => !Number.isFinite(n)))
          throw Error("Use one finite latitude, longitude pair per line.");
        return { latitude: v[0], longitude: v[1] };
      });
    regionGeometry = null;
    finishPolygon();
  });
  button(actions, "Finish polygon", finishPolygon);
  button(actions, "Draw rectangle", () => {
    const draw = new L.Draw.Rectangle(map.map);
    draw.enable();
  });
  map.map.on("draw:created", (event) => {
    const b = event.layer.getBounds();
    setBounds({
      west: b.getWest(),
      south: b.getSouth(),
      east: b.getEast(),
      north: b.getNorth(),
    });
    areas = [bounds()];
    polygon = regionGeometry = null;
    changed();
  });
  const regional = disclosure(selection, "US Census state / county boundary");
  regional.append(
    el(
      "p",
      "Use official FIPS codes. Generalized ACS2024 1:20M cartographic boundaries are preserved as supplied; not legal boundaries. Large regions require explicit smaller jobs.",
    ),
  );
  const state = input(regional, "State FIPS (2 digits)", "35"),
    county = input(regional, "County FIPS (optional, 3 digits)", "013");
  button(regional, "Load authoritative boundary", async () => {
    const r = await api("/api/science/library/regions", {
      stateFips: state.value.trim(),
      countyFips: county.value.trim() || null,
      cachedOnly: cached.checked,
    });
    if (stopped || isDisposed()) return;
    regionGeometry = r.geometry;
    polygon = null;
    areas = [r.bounds];
    setBounds(r.bounds);
    changed();
    layers.selected.clearLayers();
    L.geoJSON(r.geometry, {
      style: { color: "#ffd166", fillOpacity: 0.12 },
    }).addTo(layers.selected);
    fitSelection();
    selectionText.textContent = `${r.name} · ${r.resolution} · ${r.cached ? "Cached" : "Retrieved"} ${dateTime(r.asOfUtc)}. Preview assesses the actual bounded workload.`;
  });
  const chunks = el("div", undefined, "library-chunks");
  selection.append(chunks);
  button(actions, "Divide into bounded regions", () => {
    const a = bounds();
    if (a.east - a.west > 15 || a.north - a.south > 15)
      throw Error("Select a region no larger than 15° per side first.");
    chunks.replaceChildren(
      el(
        "p",
        "Choose one explicit part (at most 1° per side / 2,500 cells before scene multiplication). Nothing is queued automatically.",
      ),
    );
    for (let y = a.south; y < a.north; y += 1)
      for (let x = a.west; x < a.east; x += 1) {
        const part = {
          west: x,
          south: y,
          east: Math.min(a.east, x + 1),
          north: Math.min(a.north, y + 1),
        };
        button(chunks, `Use part ${y.toFixed(2)}, ${x.toFixed(2)}`, () => {
          areas = [part];
          setBounds(part);
          changed();
          fitSelection();
        });
      }
  });
  const searchTools = el("div", undefined, "action-row");
  form.append(searchTools);
  const catalogList = el("section", undefined, "panel library-catalog-results");
  catalogList.setAttribute("aria-label", "Provider catalog results");
  catalogList.append(el("h2", "Provider catalog — not searched"));
  parent.append(catalogList);
  const previewBox = el("section", undefined, "panel");
  previewBox.setAttribute("aria-label", "Download preview");
  parent.append(previewBox);
  function shape(area, color, group, text) {
    return L.rectangle(
      [
        [area.south, area.west],
        [area.north, area.east],
      ],
      { color, weight: 2, fillOpacity: 0.06 },
    )
      .bindTooltip(el("span", text))
      .addTo(group);
  }
  function changed() {
    epoch++;
    preview = null;
    previewBox.replaceChildren(
      el("h2", "Download preview"),
      el(
        "p",
        "Selection changed. Preview and explicit approval are required before downloading.",
      ),
    );
    selectionText.textContent = `${areas.length} selected area(s)${polygon ? " · polygon clipping" : ""}${regionGeometry ? " · authoritative region" : ""}. Numeric source cells are bounded geographic windows, not native pixels.`;
    layers.selected.clearLayers();
    for (const a of areas)
      shape(a, "#ffd166", layers.selected, "Selected numeric window");
    if (polygon) map.draft(polygon, true);
    else map.draft([]);
  }
  function fitSelection() {
    if (areas.length)
      map.map.fitBounds(
        areas.flatMap((a) => [
          [a.south, a.west],
          [a.north, a.east],
        ]),
        { maxZoom: 12 },
      );
  }
  function filtersChanged() {
    epoch++;
    results = [];
    chosen.clear();
    preview = null;
    layers.remote.clearLayers();
    catalogList.replaceChildren(
      el("h2", "Provider catalog — not searched"),
      el("p", "Filters changed; search again."),
    );
    previewBox.replaceChildren();
    const p = providers.find((p) => p.id === provider.value);
    details.textContent = p.description;
    measurements.textContent =
      "Supported compatible numeric recipe: " +
      p.requiredBands.join(", ") +
      ". Required quality masks are always retained.";
    from.disabled = to.disabled = !p.sceneDates;
    cloud.disabled = !p.sceneDates || !!p.id.startsWith("nasa-");
    sceneFilter.disabled = !p.id.startsWith("nasa-");
    measurements.textContent = p.kind === "hyperspectral" ? p.description : measurements.textContent;
  }
  category.onchange = () => {
    for (const option of provider.options) option.hidden = (option.value.startsWith("nasa-")) !== (category.value === "hyperspectral");
    provider.value = category.value === "hyperspectral" ? "nasa-classic" : "usgs-3dep";
    filtersChanged();
  };
  for (const option of provider.options) option.hidden = option.value.startsWith("nasa-");
  sceneFilter.onchange = filtersChanged;
  provider.onchange =
    from.onchange =
    to.onchange =
    cloud.onchange =
      filtersChanged;
  filtersChanged();
  async function search(cursor = null) {
    preview = null;
    const own = ++epoch;
    feedback.textContent = cached.checked
      ? "Reading previously cached catalog metadata…"
      : "Searching the actual provider catalog…";
    query = cursor
      ? { ...query, cursor }
      : {
          provider: provider.value,
          area: bounds(),
          fromUtc: from.disabled
            ? null
            : new Date(from.value + "T00:00:00Z").toISOString(),
          toUtc: to.disabled
            ? null
            : new Date(to.value + "T23:59:59Z").toISOString(),
          maximumCloudPercent: cloud.disabled ? 100 : Number(cloud.value),
          sceneFilter: sceneFilter.disabled ? null : sceneFilter.value.trim() || null,
          pageSize: 10,
          cachedOnly: cached.checked,
          refresh: !cached.checked,
        };
    const page = await api("/api/science/library/catalog", query);
    if (stopped || isDisposed() || own !== epoch) return;
    if (!cursor) {
      results = [];
      chosen.clear();
      layers.remote.clearLayers();
    }
    results.push(...page.items);
    feedback.textContent = `${page.status}${page.cached ? " · cached, not a new live search" : ""} · as of ${dateTime(page.asOfUtc)}; expires ${dateTime(page.expiresUtc)}. ${page.error || ""}`;
    catalogList.replaceChildren(el("h2", "Provider catalog results"));
    for (const item of results) {
      const row = el("article", undefined, "library-region"),
        label = el("label"),
        check = el("input");
      check.type = "checkbox";
      check.setAttribute("aria-label", `Select scene ${item.scene}`);
      check.checked = chosen.has(item.provider + "/" + item.scene);
      check.disabled = !!item.nasa;
      label.append(check, el("strong", item.scene));
      row.append(
        label,
        el(
          "p",
          `${item.product} · original observation ${dateTime(item.observationUtc)} · ${item.resolution} · ${item.verticalDatum}`,
        ),
        el("p", item.quality),
        el(
          "p",
          `Bands: ${item.bands.map((b) => `${b.id} (${b.units}, ×${b.scale} + ${b.offset})`).join("; ")}`,
        ),
      );
      const more = disclosure(row, "Source metadata, license & limitations");
      more.append(
        el(
          "p",
          `CRS: ${item.crs || "Unknown"} · vertical datum: ${item.verticalDatum} · source revision: ${item.revision}`,
        ),
        el(
          "p",
          `Original asset bytes: ${item.bands.map((b) => b.id + " " + bytes(b.originalAssetBytes)).join("; ")}. Subset transfer sizes may differ.`,
        ),
        el("p", item.license),
        el("p", item.limitations.join(" ")),
      );
      if (item.nasa) {
        const n = item.nasa;
        row.append(el("p", `${n.family} · ${n.capability}`), el("p", `Flightline: ${n.flightline} · collection ${n.collectionId} / ${n.collectionVersion} · ${n.processingLevel} reflectance · ${n.format}`), el("p", `${n.authorization}. Original distribution: ${bytes(n.originalBytes)}. Wavelengths / band count / units: ${n.bandCount ?? "Unknown until original-file verification"}.`));
        more.append(el("p", `Collection revision: ${n.collectionRevision}. Granule metadata: ${n.metadataUrl}`));
        for (const asset of n.assets) { const a = link(more, `${asset.role}: ${asset.name}`, asset.url); a.target="_blank"; a.rel="noopener noreferrer"; }
        const official = link(row, "Open official NASA access / Earthdata", item.reference); official.target="_blank"; official.rel="noopener noreferrer";
        if(item.provider === "nasa-ng") button(row,"Find matching header / binary granules",async()=>{
          const original = n.assets.find(a=>a.role === "Original numeric binary" || a.role === "ENVI header");
          if(!original) throw Error("No verified original companion name is available. Inspect the official product.");
          sceneFilter.value = original.name.replace(/\.(bin|hdr)$/i, ""); filtersChanged(); await search();
        });
        row.append(el("p", "1. Open official access. NASA handles login, terms and download. 2. Keep the original reflectance header and matching binary together. 3. Verify and import on the companion PC. Browser progress is external; canceling login/download leaves the Library unchanged. On a paired device, browse here and finish local-file import on the PC."));
        if (item.nasa.capability.startsWith("Guided import")) button(row, "Verify downloaded AVIRIS files on this PC", async () => {
          await api("/api/science/hyperspectral/nasa", {provider:item.provider, granuleId:item.scene, revision:item.revision});
          openHyperspectralImport(api);
        });
      }
      const reference = link(more, "Authoritative source", item.reference);
      reference.target = "_blank";
      reference.rel = "noopener noreferrer";
      check.onchange = () => {
        epoch++;
        const key = item.provider + "/" + item.scene;
        if (check.checked) chosen.set(key, item);
        else chosen.delete(key);
        preview = null;
        previewBox.replaceChildren(
          el("p", "Exact scene selection changed; preview again."),
        );
      };
      catalogList.append(row);
      if (item.geometry)
        L.geoJSON(item.geometry, {
          style: { color: "#72aaff", fillOpacity: 0.02 },
        })
          .bindTooltip(
            el("span", item.scene + " · provider outline, not QA-valid pixels"),
          )
          .addTo(layers.remote);
      else
        shape(
          item.bounds,
          "#72aaff",
          layers.remote,
          "Composite service capability, pixel availability unverified",
        );
    }
    for (const text of page.limitations)
      catalogList.append(el("p", text, "footnote"));
    if (page.nextCursor)
      button(catalogList, "More provider scenes", () =>
        search(page.nextCursor),
      );
  }
  button(searchTools, "Search provider catalog", () => search());
  const budget = input(
    previewBox,
    "Maximum job transfer (MiB)",
    "64",
    "number",
  );
  budget.min = 1;
  budget.max = 20480;
  // Keep the budget field separate from the replaceable preview result.
  form.append(budget.parentElement);
  const budgetInput = budget;
  budget.oninput = () => {
    epoch++;
    preview = null;
  };
  const requestSelection = () => {
    if (!areas.length) throw Error("Select a geographic area first.");
    if (provider.value.startsWith("nasa-")) throw Error("NASA uses official browser handoff and verified local ENVI import. Select a NASA scene’s access action. EMIT is discovery only.");
    if (!chosen.size)
      throw Error("Choose at least one exact provider scene/product first.");
    return {
      version: 1,
      areas,
      scenes: [...chosen.values()].map((i) => ({
        provider: i.provider,
        scene: i.scene,
        revision: i.revision,
        bands: i.bands.map((b) => b.id),
      })),
      polygon,
      regionGeometry,
      maximumTransferBytes: Number(budgetInput.value) * 1048576,
    };
  };
  button(searchTools, "Preview selected download", async () => {
    const own = ++epoch;
    const job = await api("/api/science/library/preview", requestSelection());
    if (stopped || isDisposed() || own !== epoch) return;
    preview = job;
    const p = job.preview;
    previewBox.replaceChildren(
      el("h2", "Review & approve download"),
      el(
        "p",
        `${p.work.length} bounded work unit(s) · ${p.uncoveredCells} uncovered cells · ${p.work.filter((w) => w.reuseSourceId).length} reusable unit(s).`,
      ),
      el(
        "p",
        `Estimated transfer: ${bytes(p.estimatedTransferBytes)} · retained size: ${bytes(p.estimatedRetainedBytes)} · reusable originals: ${bytes(p.reusableBytes)}.`,
      ),
      el(
        "p",
        `Approved transfer cap: ${bytes(p.maximumTransferBytes)} across retries/restarts. Per-item limit: ${bytes(p.perItemTransferLimit)}. Temporary disk allowance: ${bytes(p.temporaryStorageAllowanceBytes)} (bound, not estimate).`,
      ),
      el(
        "p",
        `Drive free: ${bytes(p.availableDriveBytes)} · Library used: ${bytes(p.scientificStorageUsedBytes)} / limit ${bytes(p.scientificStorageLimitBytes)} · preparation allowance currently remaining: ${bytes(p.preparationBudgetRemainingBytes)}. Storage is rechecked during work.`,
      ),
      el(
        "p",
        `Preview expires ${dateTime(p.expiresUtc)}. Selection ${p.selectionHash.slice(0, 16)}…`,
      ),
    );
    for (const i of p.products)
      previewBox.append(
        el(
          "p",
          `${i.scene} · ${dateTime(i.observationUtc)} · ${i.bands.map((b) => `${b.id} (${b.units}, ×${b.scale} + ${b.offset})`).join(", ")} · ${i.license}`,
        ),
      );
    for (const text of p.warnings) previewBox.append(el("p", text, "footnote"));
    const units = disclosure(
      previewBox,
      "Exact work windows & retained-source reuse",
    );
    table(
      units,
      "Approved geographic work windows",
      ["Unit", "Exact source", "South, West → North, East", "Required work"],
      p.work.map((w) => [
        w.index + 1,
        w.scene.scene,
        `${w.area.south}, ${w.area.west} → ${w.area.north}, ${w.area.east}`,
        w.reuseSourceId ? "Verified local reuse" : "Network subset required",
      ]),
      { pageSize: 50 },
    );
    const approve = button(previewBox, "Approve & queue download", async () => {
      if (preview !== job) throw Error("Selection changed. Preview again.");
      await api(`/api/science/library/jobs/${job.id}/approve`, {
        selectionHash: p.selectionHash,
      });
      preview = null;
      approve.disabled = true;
      feedback.textContent =
        "Approved work queued. The Download Manager below shows durable progress; active flights take priority.";
      await refresh();
    });
    approve.disabled = p.uncoveredCells > 0;
    if (p.uncoveredCells)
      previewBox.append(
        el(
          "p",
          "Remove uncovered cells or choose a covering scene before approval. No subset was silently substituted.",
        ),
      );
  });
  const manager = el("section", undefined, "panel library-job-manager");
  manager.setAttribute("aria-label", "Library Download Manager");
  manager.append(el("h2", "Download Manager"));
  parent.append(manager);
  const history = select(
    parent,
    "Download Manager view",
    [
      ["current", "Current + completed jobs"],
      ["all", "All jobs, including cancelled"],
    ],
    "current",
  );
  let jobOffset = 0;
  history.onchange = () => {
    jobsSignature = null;
    refresh();
  };
  let running = false,
    jobsSignature = null,
    localSignature = null;
  async function refresh() {
    if (stopped || isDisposed() || running) return;
    running = true;
    try {
      const response = await api(
        `/api/science/library/jobs?offset=${jobOffset}`,
      );
      if (stopped || isDisposed()) return;
      const active = document.activeElement;
      const focus = manager.contains(active)
        ? {
            id: active.closest("[data-job-id]")?.dataset.jobId,
            text: active.textContent,
          }
        : null;
      const opened = [
        ...manager.querySelectorAll("[data-job-id] details[open]"),
      ].map((d) => d.closest("[data-job-id]").dataset.jobId);
      const signature = JSON.stringify(response);
      if (signature !== jobsSignature) {
        jobsSignature = signature;
        manager.replaceChildren(el("h2", "Download Manager"));
        if (response.warning)
          manager.append(el("p", response.warning, "warning"));
        if (!response.jobs.length)
          manager.append(el("p", "No saved Library jobs."));
        layers.jobs.clearLayers();
        for (const job of response.jobs.filter(
          (j) => history.value === "all" || j.state !== "Cancelled",
        )) {
          const box = el("article", undefined, "library-region");
          box.dataset.jobId = job.id;
          box.append(
            el(
              "h3",
              `${job.state} · ${job.preview.products.map((p) => p.product).join(", ")}`,
            ),
          );
          const live = status(
            box,
            `${job.completedItems}/${job.items.length} work units verified · measured transfer ${bytes(job.downloadedBytes)} · interrupted-read allowance ${bytes(job.uncertainBytes)} · verified retained ${bytes(job.items.reduce((n, i) => n + i.verifiedBytes, 0))}.`,
          );
          live.setAttribute("aria-label", "Download progress");
          box.append(
            el(
              "p",
              job.preview.products
                .map(
                  (p) => `${p.scene} · observed ${dateTime(p.observationUtc)}`,
                )
                .join(" | "),
            ),
          );
          if (job.error) box.append(el("p", job.error, "warning"));
          const controls = el("div", undefined, "action-row");
          box.append(controls);
          const control = (action) => async () => {
            await api(`/api/science/library/jobs/${job.id}/control`, {
              action,
            });
            await refresh();
          };
          if (
            ["Queued", "Preparing", "Downloading", "Verifying"].includes(
              job.state,
            )
          )
            button(controls, "Pause job", control("pause"));
          if (["Paused", "Interrupted"].includes(job.state))
            button(controls, "Resume job", control("resume"));
          if (job.state === "Failed")
            button(controls, "Retry failed work", control("retry"));
          if (!["Cancelled", "Completed"].includes(job.state))
            button(controls, "Cancel job", control("cancel"));
          if (job.state === "Awaiting Approval")
            box.append(
              el(
                "p",
                "Awaiting explicit approval; create a current preview to approve changed selections.",
              ),
            );
          const items = disclosure(box, "Per-item progress & verification");
          items.addEventListener("toggle", () => {
            if (items.open && items.children.length === 1)
              table(
                items,
                "Stored download items",
                [
                  "Unit",
                  "State",
                  "Measured transfer",
                  "Verified retained",
                  "Attempts",
                  "Error",
                ],
                job.items.map((i) => [
                  i.index + 1,
                  i.state,
                  bytes(i.downloadedBytes),
                  bytes(i.verifiedBytes),
                  i.attempts,
                  i.error || "",
                ]),
                { pageSize: 25 },
              );
          });
          manager.append(box);
          if (job.approvalId && !["Completed", "Cancelled"].includes(job.state))
            for (const work of job.preview.work.slice(0, 500))
              shape(
                work.area,
                job.state === "Failed" ? "#ff8899" : "#c09bff",
                layers.jobs,
                `${job.state} · ${work.scene.scene}`,
              );
        }
        const navigation = el("div", undefined, "action-row");
        manager.append(navigation);
        const recent = button(navigation, "More recent jobs", () => {
          jobOffset = Math.max(0, jobOffset - 64);
          jobsSignature = null;
          refresh();
        });
        recent.disabled = jobOffset === 0;
        const older = button(navigation, "Older saved jobs", () => {
          jobOffset += 64;
          jobsSignature = null;
          refresh();
        });
        older.disabled = response.jobs.length < 64;
        for (const id of opened) {
          const details = manager.querySelector(
            `[data-job-id="${id}"] details`,
          );
          if (details) details.open = true;
        }
        if (focus?.id) {
          const controls = manager.querySelector(`[data-job-id="${focus.id}"]`);
          [...(controls?.querySelectorAll("button") || [])]
            .find((b) => b.textContent === focus.text)
            ?.focus({ preventScroll: true });
        }
      }

      const local = await api("/api/science/library/local");
      if (stopped || isDisposed()) return;
      const localKey = JSON.stringify(local);
      if (localKey !== localSignature) {
        localSignature = localKey;
        layers.retained.clearLayers();
        for (const source of local)
          for (const ring of source.bounds || [])
            L.polygon(
              ring.map((p) => [p.latitude, p.longitude]),
              {
                color: source.filesAvailable ? "#45d6bf" : "#ff8899",
                fillOpacity: 0.08,
              },
            )
              .bindTooltip(
                el(
                  "span",
                  `${source.filesAvailable ? "Retained" : "Unavailable"} · ${source.manifest.sceneId} · source QA still applies`,
                ),
              )
              .addTo(layers.retained);
      }
    } catch (error) {
      if (!stopped)
        feedback.textContent =
          "Companion / Library unavailable: " + error.message;
    } finally {
      running = false;
      if (!stopped && !isDisposed()) {
        clearTimeout(timer);
        timer = setTimeout(refresh, 3000);
      }
    }
  }
  const online = () => {
    if (!navigator.onLine)
      feedback.textContent =
        "Offline — retained coverage and saved jobs remain accessible through the companion. Select cached catalog to inspect earlier searches.";
  };
  window.addEventListener("offline", online);
  const release = cleanup;
  await refresh();
  return () => {
    window.removeEventListener("offline", online);
    release();
  };
}
