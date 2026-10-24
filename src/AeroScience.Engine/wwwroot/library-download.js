import {libraryLocation} from './library-location.js';
import {renderTransferProgress} from './transfer-progress.js';
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
    query = null,
    budgetInput = null,
    userBudget = false,
    userDates = false,
    localSources = [],
    regionEpoch = 0;
  const memoryKey = "aeroscience.external-discovery.v1";
  let remembered;
  try { remembered = JSON.parse(localStorage.getItem(memoryKey)); } catch {}
  const scienceTypes = [
    ["terrain", "Bare-earth elevation / LiDAR-derived terrain", "usgs-3dep", "USGS 3DEP supplies numeric bare-earth elevation, sometimes LiDAR-derived. Raw LAS/LAZ point-cloud download is not supported."],
    ["thermal", "Thermal imaging / surface temperature", "landsat", "Landsat Collection 2 Level-2 Surface Temperature: historical satellite surface temperature, not live airborne thermal camera footage."],
    ["surface", "Surface elevation / buildings / vegetation", "copernicus", "Copernicus GLO-30 is a digital surface model containing vegetation and buildings, not bare-earth terrain or LiDAR point clouds."],
    ["multispectral", "Multispectral imagery", "sentinel-2", "Sentinel-2 Level-2A blue, green, red and near-infrared reflectance, with the required scene-classification quality mask. Historical observations; cloudy or missing pixels stay unavailable."],
    ["hyperspectral", "Hyperspectral imagery · AVIRIS", "nasa-classic", "NASA AVIRIS-Classic / AVIRIS-NG cover historical flightlines, not the whole Earth. Supported original reflectance pairs use authorized Earthdata Download & Import; original full flightlines, not map subsets."],
    ["emit", "EMIT · discovery only", "nasa-emit", "EMIT discovery and official access only. Native NetCDF-4 import is deferred to 0.6.0."],
  ];
  const cleanup = () => {
    stopped = true;
    clearTimeout(timer);
    map?.destroy();
  };
  const heading = el("div", undefined, "page-heading");
  heading.append(
    el("p", "EXTERNAL DATA MANAGER", "eyebrow"),
    el("h1", "External Data Manager"),
    el("h2", "Find & Download Data"),
    el(
      "p",
      "Quick Download: choose an area and the science you need, find available datasets, then review and approve Download & Import. These are historical external datasets for future missions, not flight recordings. Nothing downloads during discovery.",
    ),
  );
  parent.append(heading);
  await libraryLocation(parent,api,{stopped:()=>stopped||isDisposed()});if(stopped||isDisposed())return cleanup;
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
  const requestedScience = new URLSearchParams(location.hash.split("?")[1] || "").get("science");
  const initialScience = scienceTypes.some(t => t[0] === requestedScience) ? requestedScience : scienceTypes.some(t => t[0] === remembered?.science) ? remembered.science : "terrain";
  const scienceChoice = select(form, "What scientific data do you need?", scienceTypes.map(t => [t[0], t[1]]), initialScience);
  const scienceDescription = el("p");form.append(scienceDescription);
  const providerDetails = disclosure(form, "Choose a specific provider");
  const category = select(providerDetails, "Data family", [["all", "Terrain, thermal & multispectral"], ["hyperspectral", "Hyperspectral · NASA"]], "all");
  const provider = select(
    providerDetails,
    "Scientific provider / product",
    providers.map((p) => [p.id, `${p.name} · ${p.product}`]),
    "usgs-3dep",
  );
  const details = el("p");
  providerDetails.append(details);
  const ordering = select(form, "Find data ordered by", [["latest", "Latest suitable"], ["oldest", "Oldest first"], ["cloud", "Lowest cloud cover"], ["browse", "Browse all matching results"]], "latest");
  const dateScope = select(form, "Observation dates", [["recent", "Recent 12 months"], ["previous", "Previous 12 months"], ["archive", "All observation dates · NASA archive"], ["custom", "Custom dates (advanced)"]], "recent");
  const dateSummary = el("p");form.append(dateSummary);
  const earthdataBox = el("section");
  earthdataBox.setAttribute("aria-label", "NASA Earthdata connection");
  const earthdataText = el("p");
  earthdataBox.append(el("h2", "NASA Earthdata"), earthdataText);
  form.append(earthdataBox);
  const connectEarthdata = button(earthdataBox, "Connect Earthdata on this PC", () => api("/api/desktop/earthdata-connect", {}));
  const earthdataManagement = disclosure(earthdataBox, "Manage Earthdata connection");
  button(earthdataManagement, "Refresh Earthdata connection", refreshEarthdata);
  const disconnectEarthdata = button(earthdataManagement, "Disconnect Earthdata on this PC", async () => { await api("/api/science/earthdata/disconnect", {}); await refreshEarthdata(); });
  async function refreshEarthdata() {
    const connection = await api("/api/science/earthdata");
    if (stopped || isDisposed()) return;
    earthdataText.textContent = `${connection.connected ? "Credential saved" : "Not connected"}. ${connection.message} On paired devices, complete connection management on the companion PC. NASA originals can require gigabytes; review the exact full-file scope before approval.`;
    connectEarthdata.hidden=connection.connected;disconnectEarthdata.hidden=!connection.connected;
  }
  const advancedFilters = disclosure(form, "Advanced discovery filters");
  const filters = el("div", undefined, "workspace-controls");
  advancedFilters.append(filters);
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
  const dateHint = el("p", "NASA defaults to all observation dates. Leave both dates empty for the full archive, or enter both to search a range of at most ten years.");
  advancedFilters.append(dateHint);
  // Once edited, dates belong to the pilot, including an explicitly cleared range.
  from.oninput = to.oninput = () => { userDates = true; };
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
  selection.append(el("p", "Choose the area you want to study. Search a city or airport on the map, draw a rectangle, select a small area around a map click, or choose an official named boundary. Selected outlines appear in gold; they are not proof of data availability."));
  const advancedArea = disclosure(selection, "Advanced area selection");
  const coords = el("div", undefined, "workspace-controls");
  advancedArea.append(coords);
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
  button(advancedArea, "Use bounding box", () => {
    polygon = regionGeometry = null;
    areas = [bounds()];
    changed();
    fitSelection();
  });
  button(advancedArea, "Add bounding box", () => {
    polygon = regionGeometry = null;
    if (areas.length >= 256)
      throw Error("Use no more than 256 explicitly selected boxes.");
    areas.push(bounds());
    changed();
    fitSelection();
  });
  button(actions, "Search this map area", () => {
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
    return search();
  });
  button(actions, "Clear selection", () => {
    areas = [];
    polygon = regionGeometry = null;
    changed();
  });
  const mode = select(
    advancedArea,
    "Map selection tool",
    [
      ["pan", "Pan / inspect"],
      ["cell", "Select one small area around a map click"],
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
  button(actions, "Select a small area", () => {mode.value="cell";mode.onchange();selectionText.textContent="Click the map to select one small area. This does not search or download.";});
  const L = map.L,
    layers = {};
  for (const name of ["retained", "remote", "selected", "jobs", "parts"])
    layers[name] = L.layerGroup().addTo(map.map);
  for(const [key,name,description] of [
    ['retained','Library retained sources','Verified retained-source outlines. Original acquisition dates and per-pixel quality remain separate; these are not in-flight observations.'],
    ['remote','Library provider catalog','Actually discovered provider footprints, with search as-of dates in the catalog. Outlines do not guarantee valid scientific pixels.'],
    ['selected','Library geographic selection','Requested source/preparation windows; hiding these does not change the approved selection.'],
    ['jobs','Library approved jobs','Persisted queued/download work. Incomplete staging is not usable source data.'],
    ['parts','Library bounded subdivisions','Reviewable geographic work subdivisions, not satellite pixels or verified coverage.']
  ]) map.layers.addOverlay({id:'library-'+key,name,layer:layers[key],group:'Science',description});
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
    advancedArea,
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
    selectionText.textContent="Draw a rectangle — drag across the map to choose an area. Discovery and downloading remain separate actions.";
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
  const regional = disclosure(selection, "Choose a state or county");
  regional.append(
    el(
      "p",
      "Choose official Census names; county options depend on the selected state. Generalized ACS2024 1:20M cartographic outlines are not legal survey boundaries and do not guarantee provider coverage.",
    ),
  );
  const namesStatus = status(regional, "Load official names with scientific online consent, or use previously cached names offline.");
  const namedState = select(regional, "State name", [["", "Select a state"]], "");
  const namedCounty = select(regional, "County name", [["", "Entire selected state"]], "");
  button(regional, "Load state names", async () => {
    const own=++regionEpoch;const r=await api("/api/science/library/regions/names",{cachedOnly:cached.checked});
    if(stopped||isDisposed()||own!==regionEpoch)return;
    namedState.replaceChildren(new Option("Select a state", ""),...r.items.map(i=>new Option(i.name,i.code)));
    namesStatus.textContent=`Official Census states · ${r.cached?"Cached":"Retrieved"} ${dateTime(r.asOfUtc)}. Select a state to load its counties.`;
  });
  namedState.onchange=async()=>{
    const own=++regionEpoch;state.value=namedState.value;county.value="";namedCounty.replaceChildren(new Option("Entire selected state", ""));
    if(!state.value)return;
    try{const r=await api("/api/science/library/regions/names",{stateFips:state.value,cachedOnly:cached.checked});
      if(stopped||isDisposed()||own!==regionEpoch)return;
      namedCounty.append(...r.items.map(i=>new Option(i.name,i.code)));
      namesStatus.textContent=`${namedState.selectedOptions[0].textContent} · ${r.items.length} counties · ${r.cached?"Cached":"Retrieved"} ${dateTime(r.asOfUtc)}. Review the name, then apply its boundary.`;
    }catch(error){if(own===regionEpoch)namesStatus.textContent=error.message;}
  };
  namedCounty.onchange=()=>{county.value=namedCounty.value;};
  const fips = disclosure(regional, "Advanced Census identifiers");
  const state = input(fips, "State FIPS (2 digits)", ""),
    county = input(fips, "County FIPS (optional, 3 digits)", "");
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
    const count=Math.ceil(a.east-a.west)*Math.ceil(a.north-a.south);
    if(count>256)throw Error("This selection is too large to divide safely here. Draw a smaller study area; no work has been approved.");
    chunks.replaceChildren(
      el(
        "p",
        `${count} proposed parts, at most 1° per side and up to 2,601 bounded work windows per scene. Transfer size is Unknown until exact source preview. Choose and inspect one part; nothing is approved or queued automatically.`,
      ),
    );
    const parts=[];
    for (let y = a.south; y < a.north; y += 1)
      for (let x = a.west; x < a.east; x += 1) {
        const part = {
          west: x,
          south: y,
          east: Math.min(a.east, x + 1),
          north: Math.min(a.north, y + 1),
        };
        parts.push(part);
      }
    const pick=select(chunks,"Proposed region part",parts.map((_,i)=>[String(i),`Part ${i+1} of ${parts.length}`]),"0");
    const inspect=status(chunks);
    pick.onchange=()=>{const part=parts[Number(pick.value)];layers.parts.clearLayers();shape(part,"#ffffff",layers.parts,"Proposed part — not approved");inspect.textContent=`Proposed part ${Number(pick.value)+1}: ${part.south.toFixed(3)}–${part.north.toFixed(3)}° latitude, ${part.west.toFixed(3)}–${part.east.toFixed(3)}° longitude. Search and review exact work before approval.`;};
    pick.onchange();
    button(chunks,"Use selected part",()=>{const part=parts[Number(pick.value)];areas=[part];setBounds(part);polygon=regionGeometry=null;layers.parts.clearLayers();changed();fitSelection();});
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
    results=[];chosen.clear();layers.remote.clearLayers();
    catalogList.replaceChildren(el("h2", "Provider catalog — not searched"), el("p", "Area changed. Find available data for this selection."));
    feedback.textContent="Not searched — the selected outline is a request area, not scientific data availability.";
    preview = null;
    previewBox.replaceChildren(
      el("h2", "Download preview"),
      el(
        "p",
        "Selection changed. Preview and explicit approval are required before downloading.",
      ),
    );
    selectionText.textContent = `${areas.length} selected ${areas.length===1?'area':'areas'}${polygon ? " · polygon clipping" : ""}${regionGeometry ? " · authoritative region" : ""}. Numeric source cells are bounded geographic windows, not native pixels.`;
    layers.selected.clearLayers();
    for (const a of areas)
      shape(a, "#ffd166", layers.selected, "Selected numeric window");
    if (polygon) map.draft(polygon, true);
    else map.draft([]);
    selection.dataset.bounds=JSON.stringify(areas);
    remember();
  }
  function remember() {
    // Store one explicit rectangle only. Never restore a polygon as its broader box,
    // nor persist approvals, credentials, source IDs or download intent in the browser.
    try {localStorage.setItem(memoryKey,JSON.stringify({science:scienceChoice.value,area:!polygon&&!regionGeometry&&areas.length===1?areas[0]:null}));}catch{}
  }
  function selectedBounds() {
    if(!areas.length)throw Error("Choose an area first: draw a rectangle, select a small map area, choose a named boundary, or apply coordinates in Advanced area selection.");
    const a={west:Math.min(...areas.map(a=>a.west)),south:Math.min(...areas.map(a=>a.south)),east:Math.max(...areas.map(a=>a.east)),north:Math.max(...areas.map(a=>a.north))};
    if(a.east-a.west>15||a.north-a.south>15)throw Error("This area is too large for one safe catalog search. Divide it into bounded regions and choose one part; nothing is downloaded automatically.");
    return a;
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
    const nasa = p.id.startsWith("nasa-");
    const type = scienceTypes.find(t=>t[2]===p.id)||(p.id==="nasa-ng"?scienceTypes.find(t=>t[0]==="hyperspectral"):null);
    if(type){scienceChoice.value=type[0];scienceDescription.textContent=type[3];}
    if (!userDates) {
      // CMR supports no temporal filter. A recent default hides historical AVIRIS
      // flightlines; inventing a long dated range would exceed the API's range cap.
      from.value = nasa ? "" : new Date(Date.now()-365*86400000).toISOString().slice(0,10);
      to.value = nasa ? "" : new Date().toISOString().slice(0, 10);
    }
    dateHint.hidden = !nasa;
    dateScope.parentElement.hidden=!p.sceneDates;
    dateScope.value=nasa&&!from.value&&!to.value?"archive":userDates?"custom":"recent";
    dateSummary.textContent=!p.sceneDates?"Composite / undated source: original observation date is unknown, not inferred from a publication or update.":from.value&&to.value?`Searching observations ${from.value} through ${to.value}. Earlier years remain available below.`:"All original observation dates in the selected area; bounded pages, not a coverage guarantee.";
    for(const option of dateScope.options)if(option.value==="archive")option.hidden=!nasa;
    ordering.disabled=!p.sceneDates;
    ordering.querySelector('[value="cloud"]').hidden=p.id!=="landsat"&&p.id!=="sentinel-2";
    if(ordering.value==="cloud"&&ordering.querySelector('[value="cloud"]').hidden)ordering.value="latest";
    remember();
    earthdataBox.hidden = !p.id.startsWith("nasa-");
    if (!earthdataBox.hidden) refreshEarthdata().catch(error => { earthdataText.textContent = error.message; });
    if (budgetInput && p.id.startsWith("nasa-") && !userBudget) {
      const revision = epoch;
      api("/api/science/access").then(({ configuration }) => {
        if (stopped || isDisposed() || revision !== epoch || userBudget) return;
        budgetInput.value = Math.min(configuration.preferences.nasaOriginalMaximumBytes ?? 1073741824, configuration.preferences.preflightTransferBytes ?? 21474836480) / 1048576;
      }).catch(error => { feedback.textContent = error.message; });
    }
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
  provider.onchange = cloud.onchange = ordering.onchange = filtersChanged;
  from.onchange = to.onchange = () => { userDates = true; filtersChanged(); };
  scienceChoice.onchange=()=>{
    provider.value=scienceTypes.find(t=>t[0]===scienceChoice.value)[2];
    category.value=provider.value.startsWith("nasa-")?"hyperspectral":"all";
    for(const option of provider.options)option.hidden=option.value.startsWith("nasa-")!==(category.value==="hyperspectral");
    filtersChanged();
  };
  dateScope.onchange=()=>{
    const scope=dateScope.value;
    if(scope==="custom"){advancedFilters.open=true;from.focus();return;}
    userDates=true;
    if(scope==="archive")from.value=to.value="";
    else if(scope.startsWith("year-")){const year=scope.slice(5);from.value=year+"-01-01";to.value=year+"-12-31";}
    else{const end=scope==="previous"?Date.now()-365*86400000:Date.now();from.value=new Date(end-365*86400000).toISOString().slice(0,10);to.value=new Date(end).toISOString().slice(0,10);}
    filtersChanged();dateScope.value=scope;
  };
  scienceChoice.onchange();
  if(remembered?.area){const a=remembered.area;
    if([a.west,a.south,a.east,a.north].every(Number.isFinite)&&a.west>=-180&&a.east<=180&&a.south>=-80&&a.north<=84&&a.east>a.west&&a.north>a.south&&a.east-a.west<=15&&a.north-a.south<=15){setBounds(a);areas=[a];changed();fitSelection();}
  }
  filtersChanged();
  async function search(cursor = null) {
    if (!from.disabled && !!from.value !== !!to.value)
      throw new Error("Enter both scene dates, or clear both dates to use the provider's default date scope.");
    preview = null;
    const own = ++epoch;
    feedback.textContent = cached.checked
      ? "Reading previously cached catalog metadata…"
      : "Searching the actual provider catalog…";
    query = cursor
      ? { ...query, cursor }
      : {
          provider: provider.value,
          area: selectedBounds(),
          fromUtc: from.disabled || !from.value
            ? null
            : new Date(from.value + "T00:00:00Z").toISOString(),
          toUtc: to.disabled || !to.value
            ? null
            : new Date(to.value + "T23:59:59Z").toISOString(),
          maximumCloudPercent: cloud.disabled ? 100 : Number(cloud.value),
          sceneFilter: sceneFilter.disabled ? null : sceneFilter.value.trim() || null,
          pageSize: 10,
          cachedOnly: cached.checked,
          refresh: !cached.checked,
          sort: ordering.disabled?"browse":ordering.value,
        };
    const page = await api("/api/science/library/catalog", query);
    if (stopped || isDisposed() || own !== epoch) return;
    if (!cursor) {
      results = [];
      chosen.clear();
      layers.remote.clearLayers();
    }
    results.push(...page.items.filter(item=>!results.some(old=>old.provider===item.provider&&old.scene===item.scene)));
    feedback.textContent = `${page.status==="Available"?"Matching catalog scenes found":page.status==="No match"?"No matching scenes in this searched scope":page.status==="Unavailable"?"Provider unavailable — availability was not established":page.status}${page.cached ? " · cached, not a new live search" : ""} · as of ${dateTime(page.asOfUtc)}; expires ${dateTime(page.expiresUtc)}. ${page.error || ""}`;
    catalogList.replaceChildren(el("h2", "Provider catalog results"));
    catalogList.append(el("p", `${results.length} matching catalog entries in the loaded search pages. ${page.ordering||"Provider ordering not established."}`));
    const dates=results.map(i=>i.observationUtc?.slice(0,10)).filter(Boolean).sort();
    if(dates.length){catalogList.append(el("p",`Matching scenes found from ${dates[0]} to ${dates.at(-1)} in this search; not the entire archive.`));
      for(const option of [...dateScope.options])if(option.value.startsWith("year-"))option.remove();
      for(const year of [...new Set(dates.map(d=>d.slice(0,4)))].sort().reverse())dateScope.append(new Option(`Discovered observations in ${year}`,`year-${year}`));
    }
    if(!results.length){catalogList.append(el("p",`Applied area: ${query.area.south}–${query.area.north}° latitude, ${query.area.west}–${query.area.east}° longitude. Dates: ${query.fromUtc?.slice(0,10)||"unrestricted"} to ${query.toUtc?.slice(0,10)||"unrestricted"}. Maximum reported scene cloud: ${cloud.disabled?"not applicable":query.maximumCloudPercent+"%"}. ${page.status==="Unavailable"?"This is a provider failure, not evidence that no data exists.":"Try earlier years, adjust the cloud filter, or explicitly choose a nearby supported area. No filters or products are widened automatically."}`));}
    if(provider.value==="landsat"||provider.value==="sentinel-2")button(catalogList,"Search earlier years",async()=>{
      const end=from.value?new Date(from.value+"T00:00:00Z").getTime()-86400000:Date.now()-365*86400000;userDates=true;
      from.value=new Date(end-364*86400000).toISOString().slice(0,10);to.value=new Date(end).toISOString().slice(0,10);filtersChanged();return search();
    });
    for (const [index,item] of results.entries()) {
      const row = el("article", undefined, "library-region"),
        label = el("label"),
        check = el("input");
      check.type = "checkbox";
      check.setAttribute("aria-label", `Select scene ${item.scene}`);
      check.checked = chosen.has(item.provider + "/" + item.scene);
      check.disabled = !!item.nasa;
      label.append(check, el("strong", item.nasa?.flightline||item.product));
      const retained=localSources.some(s=>s.filesAvailable&&(s.manifest.sceneId===item.scene||s.manifest.acquisition?.sceneId===item.scene));
      row.append(el("h3", `${scienceTypes.find(t=>t[2]===item.provider)?.[1]||scienceChoice.selectedOptions[0].textContent} · ${providers.find(p=>p.id===item.provider)?.name||item.provider}`));
      if(index===0&&page.serverOrdered&&query.sort==="latest"&&item.observationUtc){row.append(el("p","Newest suitable match in this search — first compatible numeric catalog result in provider observation-date order; source masks and pixel validity still require verification.","library-recommendation"));}
      row.append(
        label,
        el(
          "p",
          `${item.product} · ${item.observationUtc?"original observation "+dateTime(item.observationUtc):"Composite / original observation date unknown"} · ${item.resolution}`,
        ),
        el("p", item.quality),
        el("p",`${retained?"Retained and verified candidate — exact reuse checked at preview":"Discovered, not verified as retained"} · ${item.cloudPercent==null?"Scene cloud information unknown / not applicable":`Reported scene cloud ${item.cloudPercent}% (not a valid-pixel percentage)`}`),
        el("p",`Catalog footprint intersects the selected search bounds: ${Math.max(item.bounds.west,query.area.west).toFixed(3)}–${Math.min(item.bounds.east,query.area.east).toFixed(3)}° longitude, ${Math.max(item.bounds.south,query.area.south).toFixed(3)}–${Math.min(item.bounds.north,query.area.north).toFixed(3)}° latitude. The map shows original outlines; intersections do not establish full-area or QA-valid coverage.`),
      );
      if(!item.nasa)button(row,"Select this dataset",()=>{check.checked=true;check.onchange();});
      const more = disclosure(row, "Technical details");
      more.append(el("p",`Bands: ${item.bands.map(b=>`${b.id} (${b.units}, ×${b.scale} + ${b.offset})`).join("; ")} · ${item.verticalDatum}`));
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
        more.append(el("p", `NASA metadata updated: ${dateTime(n.updatedUtc)} (not the observation or publication time). Collection revision: ${n.collectionRevision}. Granule metadata: ${n.metadataUrl}`));
        for (const asset of n.assets) { const a = link(more, `${asset.role}: ${asset.name}`, asset.url); a.target="_blank"; a.rel="noopener noreferrer"; }
        const official = link(row, "Open official NASA access / Earthdata", item.reference); official.target="_blank"; official.rel="noopener noreferrer";
        if(item.provider === "nasa-ng") button(row,"Find matching header / binary granules",async()=>{
          const original = n.assets.find(a=>a.role === "Original numeric binary" || a.role === "ENVI header");
          if(!original) throw Error("No verified original companion name is available. Inspect the official product.");
          sceneFilter.value = original.name.replace(/\.(bin|hdr)$/i, ""); filtersChanged(); await search();
        });
        more.append(el("p", "Manual/browser fallback: NASA handles login, terms and browser downloading. Keep the original reflectance header and matching binary together, then verify and import on the companion PC. Browser progress is external; canceling login/download leaves the Library unchanged."));
        if (item.nasa.capability.startsWith("Guided import")) button(row, "Verify downloaded AVIRIS files on this PC", async () => {
          await api("/api/science/hyperspectral/nasa", {provider:item.provider, granuleId:item.scene, revision:item.revision});
          openHyperspectralImport(api);
        });
        if (item.provider !== "nasa-emit" && item.nasa.capability.startsWith("Guided import")) button(row, "Preview NASA Download & Import", async () => {
          const own = ++epoch;
          const job = await api("/api/science/library/nasa/preview", { selection: { provider: item.provider, granuleId: item.scene, revision: item.revision }, maximumTransferBytes: Number(budgetInput.value) * 1048576 });
          await displayPreview(job, own);
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
  button(searchTools, "Find Available Data", () => search());
  const budget = input(
    previewBox,
    "Maximum job transfer (MiB)",
    "64",
    "number",
  );
  budget.min = 1;
  budget.max = 20480;
  // Keep the budget field separate from the replaceable preview result.
  advancedFilters.append(budget.parentElement);
  budgetInput = budget;
  budget.oninput = () => {
    userBudget = true;
    epoch++;
    preview = null;
  };
  filtersChanged(); // Apply the correct NASA allowance after its control exists.
  const requestSelection = () => {
    if (!areas.length) throw Error("Select a geographic area first.");
    if (provider.value.startsWith("nasa-")) throw Error("Choose the exact NASA scene's Preview NASA Download & Import action. Browser/manual fallback remains available; EMIT is discovery only.");
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
  button(searchTools, "Review & Download", async () => {
    const own = ++epoch;
    const job = await api("/api/science/library/preview", requestSelection());
    await displayPreview(job, own);
  });
  async function displayPreview(job, own) {
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
    renderTransferProgress(previewBox,job.progress,job.state);
    for (const text of p.warnings) previewBox.append(el("p", text, "footnote"));
    if (p.nasa) table(previewBox, "Exact original NASA files", ["Original file", "Role", "Verified header length (bytes)", "Safe range resume"], p.nasa.files.map(file => [file.name, file.role, file.bytes, file.etag && file.ranges ? "Supported with identity verification" : "Restart safely if interrupted"]));
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
    const approve = button(previewBox, "Download & Import", async () => {
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
  }
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
      if (!earthdataBox.hidden) await refreshEarthdata();
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
          live.setAttribute("aria-label", "Download progress");renderTransferProgress(box,job.progress,job.state);
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
      localSources=local;
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
