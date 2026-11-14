import {message as localizedCopy,setPresentationText} from './i18n.js';
import {contextualHelp} from './contextual-help.js';
import {openEmitImport} from './emit-library.js';
import {libraryLocation} from './library-location.js';
import {openOnlineAccess} from './online-access.js';
import {t as textForUI,t} from './i18n.js';
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
  errorMessage,
} from "./workstation-ui.js";

// Every catalog/preview/control operates on the shared backend; UI state never
// pretends a download has completed. Geographic map drawing is optional.
export async function libraryDownload(parent, api, isDisposed = () => false, options = {}) {
  if(options.area){const a=options.area;if(![a.west,a.south,a.east,a.north].every(Number.isFinite)||a.west< -180||a.east>180||a.south< -80||a.north>84||a.east<=a.west||a.north<=a.south||a.east-a.west>15||a.north-a.south>15)throw Error(t('terrain.unsupportedSearchRegion'));}
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
    regionEpoch = 0, localEpoch = 0, localAfter = 0, localThrough = null,
    consentWarning = null, privacyDialog = null;
  const memoryKey = "aeroscience.external-discovery.v1";
  let remembered;
  try { remembered = JSON.parse(localStorage.getItem(memoryKey)); } catch {}
  const scienceTypes = [
    ["terrain", textForUI("ui.libraryDownload.messageBareEarthElevationLiDARDerivedTerrain"), "usgs-3dep", textForUI("ui.libraryDownload.messageUSGS3DEPSuppliesNumericBareEarthElevationSometimes")],
    ["thermal", textForUI("ui.libraryDownload.messageThermalImagingSurfaceTemperature"), "landsat", textForUI("ui.libraryDownload.messageLandsatCollection2Level2SurfaceTemperatureHistorical")],
    ["surface", textForUI("ui.libraryDownload.messageSurfaceElevationBuildingsVegetation"), "copernicus", textForUI("ui.libraryDownload.messageCopernicusGLO30IsADigitalSurfaceModel")],
    ["multispectral", textForUI("ui.libraryDownload.messageMultispectralImagery"), "sentinel-2", textForUI("ui.libraryDownload.messageSentinel2Level2ABlueGreenRedAndNearInfrared")],
    ["hyperspectral", textForUI("ui.libraryDownload.messageHyperspectralImageryAVIRIS"), "nasa-classic", textForUI("ui.libraryDownload.messageNASAAVIRISClassicAVIRISNGCoverHistoricalFlightlines")],
    ["emit", textForUI("ui.libraryDownload.messageEMITDevelopmentValidation"), "nasa-emit", textForUI("ui.libraryDownload.messageEMITRFL002Uncertainty002AndMASK003OriginalImport")],
  ];
  const cleanup = () => {
    stopped = true;
    clearTimeout(timer);
    privacyDialog?.close();
    map?.destroy();
  };
  const heading = el("div", undefined, "page-heading");
  heading.append(
    el("p", localizedCopy("ui.libraryDownload.messageEXTERNALDATAMANAGER"), "eyebrow"),
    el("h1", localizedCopy("ui.libraryDownload.messageExternalDataManager")),
    el("h2", localizedCopy("ui.libraryDownload.messageFindDownloadData")),
    el(
      "p",
      localizedCopy("ui.libraryDownload.messageQuickDownloadChooseAnAreaAndThe"),
    ),
  );
  parent.append(heading);contextualHelp(parent,'sources');
  await libraryLocation(parent,api,{stopped:()=>stopped||isDisposed()});if(stopped||isDisposed())return cleanup;
  link(heading, localizedCopy("ui.libraryDownload.messageRetainedCollections"), "#library");
  link(heading, localizedCopy("ui.libraryDownload.messageScientificAccessPrivacy"), "#settings/science");
  const feedback = status(
    parent,
    localizedCopy("ui.libraryDownload.messageNotSearchedSelectAProviderAnd"),
  );
  const form = el("section", undefined, "panel");
  setPresentationText(form,localizedCopy("ui.libraryDownload.messageScientificCatalogControls"),"aria-label");
  parent.append(form);
  const providers = await api("/api/science/library/providers");
  if (stopped || isDisposed()) return cleanup;
  const requestedScience = options.science || new URLSearchParams(location.hash.split("?")[1] || "").get("science");
  const initialScience = scienceTypes.some(t => t[0] === requestedScience) ? requestedScience : scienceTypes.some(t => t[0] === remembered?.science) ? remembered.science : "terrain";
  const scienceChoice = select(form, localizedCopy("ui.libraryDownload.messageWhatScientificDataDoYouNeed"), scienceTypes.map(t => [t[0], t[1]]), initialScience);
  const scienceDescription = el("p");form.append(scienceDescription);
  const providerDetails = disclosure(form, localizedCopy("ui.libraryDownload.messageChooseASpecificProvider"));
  const category = select(providerDetails, localizedCopy("ui.libraryDownload.messageDataFamily"), [["all", localizedCopy("ui.libraryDownload.messageTerrainThermalMultispectral")], ["hyperspectral", localizedCopy("ui.libraryDownload.messageHyperspectralNASA")]], "all");
  const provider = select(
    providerDetails,
    localizedCopy("ui.libraryDownload.messageScientificProviderProduct"),
    providers.map((p) => [p.id, `${p.name} · ${p.product}`]),
    "usgs-3dep",
  );
  const details = el("p");
  providerDetails.append(details);
  const ordering = select(form, localizedCopy("ui.libraryDownload.messageFindDataOrderedBy"), [["latest", localizedCopy("ui.libraryDownload.messageLatestSuitable")], ["oldest", localizedCopy("ui.libraryDownload.messageOldestFirst")], ["cloud", localizedCopy("ui.libraryDownload.messageLowestCloudCover")], ["browse", localizedCopy("ui.libraryDownload.messageBrowseAllMatchingResults")]], "latest");
  const dateScope = select(form, localizedCopy("ui.libraryDownload.messageObservationDates"), [["recent", localizedCopy("ui.libraryDownload.messageRecent12Months")], ["previous", localizedCopy("ui.libraryDownload.messagePrevious12Months")], ["archive", localizedCopy("ui.libraryDownload.messageAllObservationDatesNASAArchive")], ["custom", localizedCopy("ui.libraryDownload.messageCustomDatesAdvanced")]], "recent");
  const dateSummary = el("p");form.append(dateSummary);
  const earthdataBox = el("section");
  setPresentationText(earthdataBox,localizedCopy("ui.libraryDownload.messageNASAEarthdataConnection"),"aria-label");
  const earthdataText = el("p");
  earthdataBox.append(el("h2", localizedCopy("ui.libraryDownload.messageNASAEarthdata")), earthdataText);
  form.append(earthdataBox);
  const connectEarthdata = button(earthdataBox, localizedCopy("ui.libraryDownload.messageConnectEarthdataOnThisPC"), () => api("/api/desktop/earthdata-connect", {}));
  const earthdataManagement = disclosure(earthdataBox, localizedCopy("ui.libraryDownload.messageManageEarthdataConnection"));
  button(earthdataManagement, localizedCopy("ui.libraryDownload.messageRefreshEarthdataConnection"), refreshEarthdata);
  const disconnectEarthdata = button(earthdataManagement, localizedCopy("ui.libraryDownload.messageDisconnectEarthdataOnThisPC"), async () => { await api("/api/science/earthdata/disconnect", {}); await refreshEarthdata(); });
  async function refreshEarthdata() {
    const connection = await api("/api/science/earthdata");
    if (stopped || isDisposed()) return;
    setPresentationText(earthdataText,localizedCopy("ui.libraryDownload.messageOnPairedDevicesCompleteConnection",{value1:(String(connection.connected ? "Credential saved" : "Not connected")),value2:(String(connection.message))}));
    connectEarthdata.hidden=connection.connected;disconnectEarthdata.hidden=!connection.connected;
  }
  const advancedFilters = disclosure(form, localizedCopy("ui.libraryDownload.messageAdvancedDiscoveryFilters"));
  const filters = el("div", undefined, "workspace-controls");
  advancedFilters.append(filters);
  function input(parent, label, value, type = "text") {
    const wrap = el("label", label),
      i = el("input");
    i.type = type;
    i.value = value;
    setPresentationText(i,label,"aria-label");
    wrap.append(i);
    parent.append(wrap);
    return i;
  }
  const from = input(filters, localizedCopy("ui.libraryDownload.messageSceneDateFromUTC"), "2024-01-01", "date"),
    to = input(
      filters,
      localizedCopy("ui.libraryDownload.messageSceneDateToUTC"),
      new Date().toISOString().slice(0, 10),
      "date",
    ),
    cloud = input(filters, localizedCopy("ui.libraryDownload.messageMaximumSceneCloud"), "30", "number");
  const sceneFilter = input(filters, localizedCopy("ui.libraryDownload.messageSceneFlightlineIdentifierContains"), "");
  const dateHint = el("p", localizedCopy("ui.libraryDownload.messageNASADefaultsToAllObservationDatesLeave"));
  advancedFilters.append(dateHint);
  // Once edited, dates belong to the pilot, including an explicitly cleared range.
  from.oninput = to.oninput = () => { userDates = true; };
  cloud.min = 0;
  cloud.max = 100;
  const cached = input(
    filters,
    localizedCopy("ui.libraryDownload.messageUseCachedCatalogBoundariesOnly"),
    "",
    "checkbox",
  );
  cached.checked = !navigator.onLine;
  const measurements = el("p");
  form.append(measurements);
  const selection = el("section", undefined, "panel");
  setPresentationText(selection,localizedCopy("ui.libraryDownload.messageGeographicSelection"),"aria-label");
  selection.append(el("h2", localizedCopy("ui.libraryDownload.messageSelectAGeographicArea")));
  parent.append(selection);
  selection.append(el("p", localizedCopy("ui.libraryDownload.messageChooseTheAreaYouWantToStudy")));
  const advancedArea = disclosure(selection, localizedCopy("ui.libraryDownload.messageAdvancedAreaSelection"));
  const coords = el("div", undefined, "workspace-controls");
  advancedArea.append(coords);
  const west = input(coords, localizedCopy("ui.libraryDownload.messageWestLongitude"), "-75.01", "number"),
    south = input(coords, localizedCopy("ui.libraryDownload.messageSouthLatitude"), "39.99", "number"),
    east = input(coords, localizedCopy("ui.libraryDownload.messageEastLongitude"), "-74.99", "number"),
    north = input(coords, localizedCopy("ui.libraryDownload.messageNorthLatitude"), "40.01", "number");
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
        textForUI("ui.libraryDownload.messageEnterOrderedWGS84BoundsWithin180W180EAnd"),
      );
    return a;
  }
  function setBounds(a) {
    west.value = a.west;
    south.value = a.south;
    east.value = a.east;
    north.value = a.north;
  }
  const selectionText = status(selection, localizedCopy("ui.libraryDownload.messageNoDownloadAreaSelected"));
  const actions = el("div", undefined, "action-row");
  selection.append(actions);
  button(advancedArea, localizedCopy("ui.libraryDownload.messageUseBoundingBox"), () => {
    polygon = regionGeometry = null;
    areas = [bounds()];
    changed();
    fitSelection();
  });
  button(advancedArea, localizedCopy("ui.libraryDownload.messageAddBoundingBox"), () => {
    polygon = regionGeometry = null;
    if (areas.length >= 256)
      throw Error(textForUI("ui.libraryDownload.messageUseNoMoreThan256ExplicitlySelected"));
    areas.push(bounds());
    changed();
    fitSelection();
  });
  button(actions, localizedCopy("ui.libraryDownload.messageSearchThisMapArea"), () => {
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
  button(actions, localizedCopy("ui.libraryDownload.messageClearSelection"), () => {
    areas = [];
    polygon = regionGeometry = null;
    changed();
  });
  const mode = select(
    advancedArea,
    localizedCopy("ui.libraryDownload.messageMapSelectionTool"),
    [
      ["pan", localizedCopy("ui.libraryDownload.messagePanInspect")],
      ["cell", localizedCopy("ui.libraryDownload.messageSelectOneSmallAreaAroundAMap")],
      ["cells", localizedCopy("ui.libraryDownload.messageAddIndividualSourceCells")],
      ["polygon", localizedCopy("ui.libraryDownload.messageDrawPolygonVertices")],
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
      setPresentationText(selectionText,localizedCopy("ui.libraryDownload.messagePolygonVerticesChooseFinishPolygonTo",{value1:(String(polygon.length))}));
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
  button(actions, localizedCopy("ui.libraryDownload.messageSelectASmallArea"), () => {mode.value="cell";mode.onchange();setPresentationText(selectionText,localizedCopy("ui.libraryDownload.messageClickTheMapToSelectOneSmall"));});
  const L = map.L,
    layers = {};
  for (const name of ["retained", "remote", "selected", "jobs", "parts"])
    layers[name] = L.layerGroup().addTo(map.map);
  for(const [key,name,description] of [
    ['retained',textForUI("ui.libraryDownload.messageLibraryRetainedSources"),textForUI("ui.libraryDownload.messageVerifiedRetainedSourceOutlinesOriginalAcquisitionDatesAnd")],
    ['remote',textForUI("ui.libraryDownload.messageLibraryProviderCatalog"),textForUI("ui.libraryDownload.messageActuallyDiscoveredProviderFootprintsWithSearchAsOf")],
    ['selected',textForUI("ui.libraryDownload.messageLibraryGeographicSelection"),textForUI("ui.libraryDownload.messageRequestedSourcePreparationWindowsHidingTheseDoesNot")],
    ['jobs',textForUI("ui.libraryDownload.messageLibraryApprovedJobs"),textForUI("ui.libraryDownload.messagePersistedQueuedDownloadWorkIncompleteStagingIsNot")],
    ['parts',textForUI("ui.libraryDownload.messageLibraryBoundedSubdivisions"),textForUI("ui.libraryDownload.messageReviewableGeographicWorkSubdivisionsNotSatellitePixels")]
  ]) map.layers.addOverlay({id:'library-'+key,name,layer:layers[key],group:"Science",description});
  const legend = el("ul", undefined, "library-legend");
  setPresentationText(legend,localizedCopy("ui.libraryDownload.messageScientificCoverageLegend"),"aria-label");
  for (const [name, text, color] of [
    [
      "retained",
      textForUI("ui.libraryDownload.messageDownloadedLocallyVerifiedRetainedNumericFootprint"),
      "#45d6bf",
    ],
    [
      "remote",
      textForUI("ui.libraryDownload.messageAvailableFromProviderCatalogOutlineQANot"),
      "#72aaff",
    ],
    ["selected", textForUI("ui.libraryDownload.messageSelectedRequestedNumericWindows"), "#ffd166"],
    [
      "jobs",
      textForUI("ui.libraryDownload.messageQueuedDownloadingApprovedWorkFailedWork"),
      "#c09bff",
    ],
    [
      "unsearched",
      textForUI("ui.libraryDownload.messageNotSearchedNoProviderAvailabilityEstablished"),
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
    localizedCopy("ui.libraryDownload.messageAccessiblePolygonCoordinates"),
  );
  const vertices = el("textarea");
  setPresentationText(vertices,localizedCopy("ui.libraryDownload.messagePolygonVerticesLatitudeLongitudePerLine"),"aria-label");
  vertices.rows = 5;
  polygonDetails.append(vertices);
  function finishPolygon() {
    if (!polygon || polygon.length < 3)
      throw Error(textForUI("ui.libraryDownload.messageAddAtLeastThreePolygonVertices"));
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
  button(polygonDetails, localizedCopy("ui.libraryDownload.messageApplyPolygonCoordinates"), () => {
    polygon = vertices.value
      .trim()
      .split("\n")
      .map((line) => {
        const v = line.split(",").map(Number);
        if (v.length !== 2 || v.some((n) => !Number.isFinite(n)))
          throw Error(textForUI("ui.libraryDownload.messageUseOneFiniteLatitudeLongitudePairPer"));
        return { latitude: v[0], longitude: v[1] };
      });
    regionGeometry = null;
    finishPolygon();
  });
  button(actions, localizedCopy("ui.libraryDownload.messageFinishPolygon"), finishPolygon);
  button(actions, localizedCopy("ui.libraryDownload.messageDrawRectangle"), () => {
    const draw = new L.Draw.Rectangle(map.map);
    draw.enable();
    setPresentationText(selectionText,localizedCopy("ui.libraryDownload.messageDrawARectangleDragAcrossThe"));
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
  const regional = disclosure(selection, localizedCopy("ui.libraryDownload.messageChooseAStateOrCounty"));
  regional.append(
    el(
      "p",
      localizedCopy("ui.libraryDownload.messageChooseOfficialCensusNamesCountyOptionsDepend"),
    ),
  );
  const namesStatus = status(regional, localizedCopy("ui.libraryDownload.messageLoadOfficialNamesWithScientificOnlineConsent"));
  const namedState = select(regional, localizedCopy("ui.libraryDownload.messageStateName"), [["", localizedCopy("ui.libraryDownload.messageSelectAState")]], "");
  const namedCounty = select(regional, localizedCopy("ui.libraryDownload.messageCountyName"), [["", localizedCopy("ui.libraryDownload.messageEntireSelectedState")]], "");
  button(regional, localizedCopy("ui.libraryDownload.messageLoadStateNames"), async () => {
    const own=++regionEpoch;const r=await api("/api/science/library/regions/names",{cachedOnly:cached.checked});
    if(stopped||isDisposed()||own!==regionEpoch)return;
    namedState.replaceChildren(new Option(textForUI("ui.libraryDownload.messageSelectAState"), ""),...r.items.map(i=>new Option(i.name,i.code)));
    setPresentationText(namesStatus,localizedCopy("ui.libraryDownload.messageOfficialCensusStatesSelectA",{value1:(String(r.cached?"Cached":"Retrieved")),value2:(String(dateTime(r.asOfUtc)))}));
  });
  namedState.onchange=async()=>{
    const own=++regionEpoch;state.value=namedState.value;county.value="";namedCounty.replaceChildren(new Option(textForUI("ui.libraryDownload.messageEntireSelectedState"), ""));
    if(!state.value)return;
    try{const r=await api("/api/science/library/regions/names",{stateFips:state.value,cachedOnly:cached.checked});
      if(stopped||isDisposed()||own!==regionEpoch)return;
      namedCounty.append(...r.items.map(i=>new Option(i.name,i.code)));
      setPresentationText(namesStatus,localizedCopy("ui.libraryDownload.messageCountiesReviewThe",{value1:(String(namedState.selectedOptions[0].textContent)),value2:(String(r.items.length)),value3:(String(r.cached?"Cached":"Retrieved")),value4:(String(dateTime(r.asOfUtc)))}));
    }catch(error){if(own===regionEpoch)setPresentationText(namesStatus,error.message);}
  };
  namedCounty.onchange=()=>{county.value=namedCounty.value;};
  const fips = disclosure(regional, localizedCopy("ui.libraryDownload.messageAdvancedCensusIdentifiers"));
  const state = input(fips, localizedCopy("ui.libraryDownload.messageStateFIPS2Digits"), ""),
    county = input(fips, localizedCopy("ui.libraryDownload.messageCountyFIPSOptional3Digits"), "");
  button(regional, localizedCopy("ui.libraryDownload.messageLoadAuthoritativeBoundary"), async () => {
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
    setPresentationText(selectionText,localizedCopy("ui.libraryDownload.messagePreviewAssessesThe",{value1:(String(r.name)),value2:(String(r.resolution)),value3:(String(r.cached ? "Cached" : "Retrieved")),value4:(String(dateTime(r.asOfUtc)))}));
  });
  const chunks = el("div", undefined, "library-chunks");
  selection.append(chunks);
  button(actions, localizedCopy("ui.libraryDownload.messageDivideIntoBoundedRegions"), () => {
    const a = bounds();
    const count=Math.ceil(a.east-a.west)*Math.ceil(a.north-a.south);
    if(count>256)throw Error(textForUI("ui.libraryDownload.messageThisSelectionIsTooLargeToDivide"));
    chunks.replaceChildren(
      el(
        "p",
        localizedCopy("ui.libraryDownload.messageProposedPartsAtMost1Per",{value1:(String(count))}),
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
    const pick=select(chunks,localizedCopy("ui.libraryDownload.messageProposedRegionPart"),parts.map((_,i)=>[String(i),textForUI("ui.libraryDownload.messagePartOf",{value1:(String(i+1)),value2:(String(parts.length))})]),"0");
    const inspect=status(chunks);
    pick.onchange=()=>{const part=parts[Number(pick.value)];layers.parts.clearLayers();shape(part,"#ffffff",layers.parts,localizedCopy("ui.libraryDownload.messageProposedPartNotApproved"));setPresentationText(inspect,localizedCopy("ui.libraryDownload.messageProposedPartLatitude",{value1:(String(Number(pick.value)+1)),value2:(String(part.south.toFixed(3))),value3:(String(part.north.toFixed(3))),value4:(String(part.west.toFixed(3))),value5:(String(part.east.toFixed(3)))}));};
    pick.onchange();
    button(chunks,localizedCopy("ui.libraryDownload.messageUseSelectedPart"),()=>{const part=parts[Number(pick.value)];areas=[part];setBounds(part);polygon=regionGeometry=null;layers.parts.clearLayers();changed();fitSelection();});
  });
  const searchTools = el("div", undefined, "action-row");
  form.append(searchTools);
  const catalogList = el("section", undefined, "panel library-catalog-results");
  setPresentationText(catalogList,localizedCopy("ui.libraryDownload.messageProviderCatalogResults"),"aria-label");
  catalogList.append(el("h2", localizedCopy("ui.libraryDownload.messageProviderCatalogNotSearched")));
  parent.append(catalogList);
  const retainedPanel=el("section",undefined,"panel");setPresentationText(retainedPanel,localizedCopy("ui.libraryDownload.messageRetainedCoverageInventory"),"aria-label");
  retainedPanel.append(el("h2",localizedCopy("ui.libraryDownload.messageExistingDataInYourSelectedArea")));const localStatus=status(retainedPanel,localizedCopy("ui.libraryDownload.messageSelectAnAreaToInspectRetainedCandidates"));
  const localNavigation=el("div",undefined,"action-row");retainedPanel.append(localNavigation);parent.append(retainedPanel);
  button(localNavigation,localizedCopy("ui.libraryDownload.messageRefreshRetainedCoverage"),()=>loadLocal(0,null));
  const firstLocal=button(localNavigation,localizedCopy("ui.libraryDownload.messageFirstRetainedPage"),()=>loadLocal(0,null));
  const nextLocal=button(localNavigation,localizedCopy("ui.libraryDownload.messageMoreRetainedCoverage"),()=>loadLocal(Number(nextLocal.dataset.next),localThrough));firstLocal.disabled=nextLocal.disabled=true;
  async function loadLocal(after=localAfter,through=after?localThrough:null){
    const own=++localEpoch;localAfter=after;localThrough=through;
    if(!areas.length){localSources=[];layers.retained.clearLayers();setPresentationText(localStatus,localizedCopy("ui.libraryDownload.messageSelectAnAreaToInspectRetainedCandidatesdd15c8de"));firstLocal.disabled=nextLocal.disabled=true;return;}
    let area;try{area=selectedBounds();}catch(error){setPresentationText(localStatus,error.message);return;}
    const params=new URLSearchParams({...area,after,limit:50});if(through!=null)params.set('through',through);
    const page=await api('/api/science/library/local/page?'+params);
    if(stopped||isDisposed()||own!==localEpoch)return;
    localThrough=page.through;localSources=page.items;
    firstLocal.disabled=after===0;nextLocal.disabled=page.next==null;nextLocal.dataset.next=page.next??'';
    setPresentationText(localStatus,localizedCopy("ui.libraryDownload.messageRetainedCandidatesOnThisPage",{value1:(String(page.items.length)),value2:(String(page.total)),value3:(String(polygon||regionGeometry?'Search bounds enclose the selected shape; some candidates lie outside it. ':''))}));
    const key=JSON.stringify([area,page.items]);if(key===localSignature)return;localSignature=key;layers.retained.clearLayers();
    for(const source of page.items)for(const ring of source.bounds||[])L.polygon(ring.map(p=>[p.latitude,p.longitude]),{color:source.filesAvailable?'#45d6bf':'#ff8899',fillOpacity:.08}).bindTooltip(el('span',localizedCopy("ui.libraryDownload.messageIntegrityCompatibilityAndPixel",{value1:(String(source.filesAvailable?'Retained candidate':'Unavailable')),value2:(String(source.manifest.sceneId))}))).addTo(layers.retained);
  }

  const previewBox = el("section", undefined, "panel");
  setPresentationText(previewBox,localizedCopy("ui.libraryDownload.messageDownloadPreview"),"aria-label");
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
    catalogList.replaceChildren(el("h2", localizedCopy("ui.libraryDownload.messageProviderCatalogNotSearched")), el("p", localizedCopy("ui.libraryDownload.messageAreaChangedFindAvailableDataForThis")));
    setPresentationText(feedback,localizedCopy("ui.libraryDownload.messageNotSearchedTheSelectedOutlineIs"));
    preview = null;
    previewBox.replaceChildren(
      el("h2", localizedCopy("ui.libraryDownload.messageDownloadPreview")),
      el(
        "p",
        localizedCopy("ui.libraryDownload.messageSelectionChangedPreviewAndExplicitApprovalAre"),
      ),
    );
    setPresentationText(selectionText,localizedCopy("ui.libraryDownload.messageSelectedNumericSourceCellsAre",{value1:(String(areas.length)),value2:(String(areas.length===1?'area':'areas')),value3:(String(polygon ? " · polygon clipping" : "")),value4:(String(regionGeometry ? " · authoritative region" : "")),value5:(String(polygon||regionGeometry?" The provider search uses enclosing bounds; edge windows may include pixels outside the selected shape. Review the exact work windows before approval.":""))}));
    layers.selected.clearLayers();
    for (const a of areas)
      shape(a, "#ffd166", layers.selected, localizedCopy("ui.libraryDownload.messageSelectedNumericWindow"));
    if (polygon) map.draft(polygon, true);
    else map.draft([]);
    selection.dataset.bounds=JSON.stringify(areas);
    remember();
    loadLocal(0,null).catch(error=>{if(!stopped)setPresentationText(localStatus,localizedCopy("ui.libraryDownload.messageRetainedCoverageUnavailable",{value1:(String(error.message))}));});
  }
  function remember() {
    // Store one explicit rectangle only. Never restore a polygon as its broader box,
    // nor persist approvals, credentials, source IDs or download intent in the browser.
    try {localStorage.setItem(memoryKey,JSON.stringify({science:scienceChoice.value,area:!polygon&&!regionGeometry&&areas.length===1?areas[0]:null}));}catch{}
  }
  function selectedBounds() {
    if(!areas.length)throw Error(textForUI("ui.libraryDownload.messageChooseAnAreaFirstDrawARectangle"));
    const a={west:Math.min(...areas.map(a=>a.west)),south:Math.min(...areas.map(a=>a.south)),east:Math.max(...areas.map(a=>a.east)),north:Math.max(...areas.map(a=>a.north))};
    if(a.east-a.west>15||a.north-a.south>15)throw Error(textForUI("ui.libraryDownload.messageThisAreaIsTooLargeForOne"));
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
      el("h2", localizedCopy("ui.libraryDownload.messageProviderCatalogNotSearched")),
      el("p", localizedCopy("ui.libraryDownload.messageFiltersChangedSearchAgain")),
    );
    previewBox.replaceChildren();
    const p = providers.find((p) => p.id === provider.value);
    const nasa = p.id.startsWith("nasa-");
    const type = scienceTypes.find(t=>t[2]===p.id)||(p.id==="nasa-ng"?scienceTypes.find(t=>t[0]==="hyperspectral"):null);
    if(type){scienceChoice.value=type[0];setPresentationText(scienceDescription,type[3]);}
    if (!userDates) {
      // CMR supports no temporal filter. A recent default hides historical AVIRIS
      // flightlines; inventing a long dated range would exceed the API's range cap.
      from.value = nasa ? "" : new Date(Date.now()-365*86400000).toISOString().slice(0,10);
      to.value = nasa ? "" : new Date().toISOString().slice(0, 10);
    }
    dateHint.hidden = !nasa;
    dateScope.parentElement.hidden=!p.sceneDates;
    dateScope.value=nasa&&!from.value&&!to.value?"archive":userDates?"custom":"recent";
    setPresentationText(dateSummary,!p.sceneDates?localizedCopy("ui.libraryDownload.messageCompositeUndatedSourceOriginalObservationDate"):from.value&&to.value?localizedCopy("ui.libraryDownload.messageSearchingObservationsThroughEarlierYearsRemain",{value1:(String(from.value)),value2:(String(to.value))}):localizedCopy("ui.libraryDownload.messageAllOriginalObservationDatesInTheSelected"));
    for(const option of dateScope.options)if(option.value==="archive")option.hidden=!nasa;
    ordering.disabled=!p.sceneDates;
    ordering.querySelector('[value="cloud"]').hidden=p.id!=="landsat"&&p.id!=="sentinel-2";
    if(ordering.value==="cloud"&&ordering.querySelector('[value="cloud"]').hidden)ordering.value="latest";
    remember();
    earthdataBox.hidden = !p.id.startsWith("nasa-");
    if (!earthdataBox.hidden) refreshEarthdata().catch(error => { setPresentationText(earthdataText,error.message); });
    if (budgetInput && p.id.startsWith("nasa-") && !userBudget) {
      const revision = epoch;
      api("/api/science/access").then(({ configuration }) => {
        if (stopped || isDisposed() || revision !== epoch || userBudget) return;
        budgetInput.value = Math.min(configuration.preferences.nasaOriginalMaximumBytes ?? 1073741824, configuration.preferences.preflightTransferBytes ?? 21474836480) / 1048576;
      }).catch(error => { setPresentationText(feedback,error.message); });
    }
    setPresentationText(details,p.description);
    setPresentationText(measurements,localizedCopy("ui.libraryDownload.messageSupportedCompatibleNumericRecipeRequiredQuality",{value1:(String(p.requiredBands.join(", ")))}));
    from.disabled = to.disabled = !p.sceneDates;
    cloud.disabled = !p.sceneDates || !!p.id.startsWith("nasa-");
    sceneFilter.disabled = !p.id.startsWith("nasa-");
    setPresentationText(measurements,p.kind === "hyperspectral" ? p.description : measurements.textContent);
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
  if(options.area||remembered?.area){const a=options.area||remembered.area;
    if([a.west,a.south,a.east,a.north].every(Number.isFinite)&&a.west>=-180&&a.east<=180&&a.south>=-80&&a.north<=84&&a.east>a.west&&a.north>a.south&&a.east-a.west<=15&&a.north-a.south<=15){setBounds(a);areas=[a];changed();fitSelection();}
  }
  filtersChanged();
  async function search(cursor = null) {
    consentWarning?.remove();consentWarning=null;
    if (!from.disabled && !!from.value !== !!to.value)
      throw new Error(textForUI("ui.libraryDownload.messageEnterBothSceneDatesOrClearBoth"));
    preview = null;
    const own = ++epoch;
    setPresentationText(feedback,cached.checked
      ? localizedCopy("ui.libraryDownload.messageReadingPreviouslyCachedCatalogMetadata")
      : localizedCopy("ui.libraryDownload.messageSearchingTheActualProviderCatalog"));
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
    let page;
    try{page=await api("/api/science/library/catalog", query);}
    catch(error){
      // Use the server conflict and current saved permission, never message matching.
      // Cached/offline searches and unrelated auth/provider errors keep their behavior.
      if(error.status===409&&!query.cachedOnly){
        const access=await api('/api/science/access').catch(()=>null);
        if(stopped||isDisposed()||own!==epoch)return;
        const p=access?.configuration.preferences;
        if(p&&(!p.onlineEnabled||!p.privacyAccepted)){
          consentWarning=errorMessage(searchTools,error);
          const warning=consentWarning;
          setPresentationText(consentWarning.firstElementChild,localizedCopy("ui.libraryDownload.messageReviewScientificDataPrivacyBeforeSearchingOnline"));
          const review=button(consentWarning,localizedCopy("ui.libraryDownload.messageReviewPrivacyConsent"),()=>{
            privacyDialog=openOnlineAccess(api,{opener:review,onAllowed:state=>{
              const preferences=state.science.configuration.preferences;
              if(stopped||isDisposed()||own!==epoch||consentWarning!==warning||!consentWarning?.isConnected)return;
              consentWarning.classList.remove('action-error');consentWarning.classList.add('panel');consentWarning.setAttribute('role','status');
              setPresentationText(consentWarning.firstElementChild,localizedCopy("ui.libraryDownload.messageScientificAccessChoicesSaved"));
              setPresentationText(consentWarning.querySelector('p'),preferences.onlineEnabled&&preferences.privacyAccepted?localizedCopy("ui.libraryDownload.messageSelectFindAvailableDataToSearchWith"):localizedCopy("ui.libraryDownload.messageOnlineSearchesRemainDisabledCachedResultsAnd"));
              setPresentationText(feedback,consentWarning.querySelector('p').textContent);
            }});
          });
          setPresentationText(feedback,localizedCopy("ui.libraryDownload.messageOnlineScientificDiscoveryNeedsYourExplicitAccess"));
          return;
        }
      }
      throw error;
    }
    if (stopped || isDisposed() || own !== epoch) return;
    if (!cursor) {
      results = [];
      chosen.clear();
      layers.remote.clearLayers();
    }
    results.push(...page.items.filter(item=>!results.some(old=>old.provider===item.provider&&old.scene===item.scene)));
    setPresentationText(feedback,localizedCopy("ui.libraryDownload.messageAsOfExpires",{value1:(String(page.status==="Available"?"Matching catalog scenes found":page.status==="No match"?"No matching scenes in this searched scope":page.status==="Unavailable"?"Provider unavailable — availability was not established":page.status)),value2:(String(page.cached ? " · cached, not a new live search" : "")),value3:(String(dateTime(page.asOfUtc))),value4:(String(dateTime(page.expiresUtc))),value5:(String(page.error || ""))}));
    catalogList.replaceChildren(el("h2", localizedCopy("ui.libraryDownload.messageProviderCatalogResults")));
    catalogList.append(el("p", localizedCopy("ui.libraryDownload.messageMatchingCatalogEntriesInTheLoaded",{value1:(String(results.length)),value2:(String(page.ordering||"Provider ordering not established."))})));
    const dates=results.map(i=>i.observationUtc?.slice(0,10)).filter(Boolean).sort();
    if(dates.length){catalogList.append(el("p",localizedCopy("ui.libraryDownload.messageMatchingScenesFoundFromToInThis",{value1:(String(dates[0])),value2:(String(dates.at(-1)))})));
      for(const option of [...dateScope.options])if(option.value.startsWith("year-"))option.remove();
      for(const year of [...new Set(dates.map(d=>d.slice(0,4)))].sort().reverse())dateScope.append(new Option(textForUI("ui.libraryDownload.messageDiscoveredObservationsIn",{value1:(String(year))}),`year-${year}`));
    }
    if(!results.length){catalogList.append(el("p",localizedCopy("ui.libraryDownload.messageAppliedAreaLatitude",{value1:(String(query.area.south)),value2:(String(query.area.north)),value3:(String(query.area.west)),value4:(String(query.area.east)),value5:(String(query.fromUtc?.slice(0,10)||"unrestricted")),value6:(String(query.toUtc?.slice(0,10)||"unrestricted")),value7:(String(cloud.disabled?"not applicable":query.maximumCloudPercent+"%")),value8:(String(page.status==="Unavailable"?"This is a provider failure, not evidence that no data exists.":"Try earlier years, adjust the cloud filter, or explicitly choose a nearby supported area. No filters or products are widened automatically."))})));}
    if(provider.value==="landsat"||provider.value==="sentinel-2")button(catalogList,localizedCopy("ui.libraryDownload.messageSearchEarlierYears"),async()=>{
      const end=from.value?new Date(from.value+"T00:00:00Z").getTime()-86400000:Date.now()-365*86400000;userDates=true;
      from.value=new Date(end-364*86400000).toISOString().slice(0,10);to.value=new Date(end).toISOString().slice(0,10);filtersChanged();return search();
    });
    for (const [index,item] of results.entries()) {
      const row = el("article", undefined, "library-region"),
        label = el("label"),
        check = el("input");
      check.type = "checkbox";
      setPresentationText(check,localizedCopy("ui.libraryDownload.messageSelectScene",{value1:(String(item.scene))}),"aria-label");
      check.checked = chosen.has(item.provider + "/" + item.scene);
      check.disabled = !!item.nasa;
      label.append(check, el("strong", item.nasa?.flightline||item.product));
      const retained=localSources.some(s=>s.filesAvailable&&(s.manifest.sceneId===item.scene||s.manifest.acquisition?.sceneId===item.scene));
      row.append(el("h3", `${scienceTypes.find(t=>t[2]===item.provider)?.[1]||scienceChoice.selectedOptions[0].textContent} · ${providers.find(p=>p.id===item.provider)?.name||item.provider}`));
      if(index===0&&page.serverOrdered&&query.sort==="latest"&&item.observationUtc){row.append(el("p",localizedCopy("ui.libraryDownload.messageNewestSuitableMatchInThisSearch"),"library-recommendation"));}
      row.append(
        label,
        el(
          "p",
          `${item.product} · ${item.observationUtc?"original observation "+dateTime(item.observationUtc):"Composite / original observation date unknown"} · ${item.resolution}`,
        ),
        el("p", item.quality),
        el("p",`${retained?"Retained candidate on the loaded inventory page — integrity and exact reuse checked at preview":"Discovered — not confirmed in the loaded local page; full indexed reuse is checked at preview"} · ${item.cloudPercent==null?"Scene cloud information unknown / not applicable":`Reported scene cloud ${item.cloudPercent}% (not a valid-pixel percentage)`}`),
        el("p",localizedCopy("ui.libraryDownload.messageCatalogFootprintIntersectsTheSelectedSearchBounds",{value1:(String(Math.max(item.bounds.west,query.area.west).toFixed(3))),value2:(String(Math.min(item.bounds.east,query.area.east).toFixed(3))),value3:(String(Math.max(item.bounds.south,query.area.south).toFixed(3))),value4:(String(Math.min(item.bounds.north,query.area.north).toFixed(3)))})),
      );
      if(!item.nasa)button(row,localizedCopy("ui.libraryDownload.messageSelectThisDataset"),()=>{check.checked=true;check.onchange();});
      const more = disclosure(row, localizedCopy("ui.libraryDownload.messageTechnicalDetails"));
      more.append(el("p",localizedCopy("ui.libraryDownload.messageBands",{value1:(String(item.bands.map(b=>`${b.id} (${b.units}, ×${b.scale} + ${b.offset})`).join("; "))),value2:(String(item.verticalDatum))})));
      more.append(
        el(
          "p",
          localizedCopy("ui.libraryDownload.messageCRSVerticalDatumSourceRevision",{value1:(String(item.crs || "Unknown")),value2:(String(item.verticalDatum)),value3:(String(item.revision))}),
        ),
        el(
          "p",
          localizedCopy("ui.libraryDownload.messageOriginalAssetBytesSubsetTransferSizes",{value1:(String(item.bands.map((b) => b.id + " " + bytes(b.originalAssetBytes)).join("; ")))}),
        ),
        el("p", item.license),
        el("p", item.limitations.join(" ")),
      );
      if (item.nasa) {
        const n = item.nasa;
        row.append(el("p", `${n.family} · ${n.capability}`), el("p", localizedCopy("ui.libraryDownload.messageFlightlineCollectionReflectance",{value1:(String(n.flightline)),value2:(String(n.collectionId)),value3:(String(n.collectionVersion)),value4:(String(n.processingLevel)),value5:(String(n.format))})), el("p", localizedCopy("ui.libraryDownload.messageOriginalDistributionWavelengths",{value1:(String(n.authorization)),value2:(String(bytes(n.originalBytes))),value3:(String(n.bandCount ?? "Unknown until original-file verification"))})));
        more.append(el("p", localizedCopy("ui.libraryDownload.messageNASAMetadataUpdatedNotTheObservationOr",{value1:(String(dateTime(n.updatedUtc))),value2:(String(n.collectionRevision)),value3:(String(n.metadataUrl))})));
        for (const asset of n.assets) { const a = link(more, `${asset.role}: ${asset.name}`, asset.url); a.target="_blank"; a.rel="noopener noreferrer"; }
        const official = link(row, localizedCopy("ui.libraryDownload.messageOpenOfficialNASAAccessEarthdata"), item.reference); official.target="_blank"; official.rel="noopener noreferrer";
        if(item.provider === "nasa-ng") button(row,localizedCopy("ui.libraryDownload.messageFindMatchingHeaderBinaryGranules"),async()=>{
          const original = n.assets.find(a=>a.role === "Original numeric binary" || a.role === "ENVI header");
          if(!original) throw Error(textForUI("ui.libraryDownload.messageNoVerifiedOriginalCompanionNameIsAvailable"));
          sceneFilter.value = original.name.replace(/\.(bin|hdr)$/i, ""); filtersChanged(); await search();
        });
        more.append(el("p", localizedCopy("ui.libraryDownload.messageManualBrowserFallbackNASAHandlesLoginTermsAnd")));
        if (item.nasa.capability.startsWith("Guided import")) button(row, localizedCopy("ui.libraryDownload.messageVerifyDownloadedAVIRISFilesOnThisPC"), async () => {
          await api("/api/science/hyperspectral/nasa", {provider:item.provider, granuleId:item.scene, revision:item.revision});
          openHyperspectralImport(api);
        });
        if (item.provider === "nasa-emit") button(row,localizedCopy("ui.libraryDownload.messageImportEMITOriginalsOnThisPC"),()=>openEmitImport(api));
        if (item.provider === "nasa-emit" || item.nasa.capability.startsWith("Guided import")) button(row, localizedCopy("ui.libraryDownload.messagePreviewNASADownloadImport"), async () => {
          let acknowledged=false;
          if(item.provider==="nasa-emit"){
            const {acknowledgeEmit}=await import('./emit-review.js');acknowledged=await acknowledgeEmit();if(!acknowledged)return;
          }
          const own = ++epoch;
          const job = await api("/api/science/library/nasa/preview", { selection: { provider: item.provider, granuleId: item.scene, revision: item.revision }, maximumTransferBytes: Number(budgetInput.value) * 1048576,validationAcknowledged:acknowledged });
          await displayPreview(job, own);
        });
      }
      const reference = link(more, localizedCopy("ui.libraryDownload.messageAuthoritativeSource"), item.reference);
      reference.target = "_blank";
      reference.rel = "noopener noreferrer";
      check.onchange = () => {
        epoch++;
        const key = item.provider + "/" + item.scene;
        if (check.checked) chosen.set(key, item);
        else chosen.delete(key);
        preview = null;
        previewBox.replaceChildren(
          el("p", localizedCopy("ui.libraryDownload.messageExactSceneSelectionChangedPreviewAgain")),
        );
      };
      catalogList.append(row);
      if (item.geometry)
        L.geoJSON(item.geometry, {
          style: { color: "#72aaff", fillOpacity: 0.02 },
        })
          .bindTooltip(
            el("span", localizedCopy("ui.libraryDownload.messageProviderOutlineNotQAValidPixels",{value1:(String(item.scene))})),
          )
          .addTo(layers.remote);
      else
        shape(
          item.bounds,
          "#72aaff",
          layers.remote,
          localizedCopy("ui.libraryDownload.messageCompositeServiceCapabilityPixelAvailabilityUnverified"),
        );
    }
    for (const text of page.limitations)
      catalogList.append(el("p", text, "footnote"));
    if (page.nextCursor)
      button(catalogList, localizedCopy("ui.libraryDownload.messageMoreProviderScenes"), () =>
        search(page.nextCursor),
      );
  }
  button(searchTools, localizedCopy("ui.libraryDownload.messageFindAvailableData"), () => search());
  const budget = input(
    previewBox,
    localizedCopy("ui.libraryDownload.messageMaximumJobTransferMiB"),
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
    if (!areas.length) throw Error(textForUI("ui.libraryDownload.messageSelectAGeographicAreaFirst"));
    if (provider.value.startsWith("nasa-")) throw Error(textForUI("ui.libraryDownload.messageChooseTheExactNASASceneSPreviewNASA"));
    if (!chosen.size)
      throw Error(textForUI("ui.libraryDownload.messageChooseAtLeastOneExactProviderSceneProduct"));
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
  button(searchTools, localizedCopy("ui.libraryDownload.messageReviewDownload"), async () => {
    const own = ++epoch;
    const job = await api("/api/science/library/preview", requestSelection());
    await displayPreview(job, own);
  });
  async function displayPreview(job, own) {
    if (stopped || isDisposed() || own !== epoch) return;
    preview = job;
    const p = job.preview;
    previewBox.replaceChildren(
      el("h2", localizedCopy("ui.libraryDownload.messageReviewApproveDownload")),
      el(
        "p",
        localizedCopy("ui.libraryDownload.messageBoundedSourceWorkWindowSUncovered",{value1:(String(p.work.length)),value2:(String(p.uncoveredCells)),value3:(String(p.work.filter((w) => w.reuseSourceId).length))}),
      ),
      el(
        "p",
        localizedCopy("ui.libraryDownload.messageEstimatedTransferRetainedSizeRetained",{value1:(String(bytes(p.estimatedTransferBytes))),value2:(String(bytes(p.estimatedRetainedBytes))),value3:(String(bytes(p.reusableBytes)))}),
      ),
      el(
        "p",
        localizedCopy("ui.libraryDownload.messageApprovedTransferCapAcrossRetriesRestartsPerItemLimit",{value1:(String(bytes(p.maximumTransferBytes))),value2:(String(bytes(p.perItemTransferLimit))),value3:(String(bytes(p.temporaryStorageAllowanceBytes)))}),
      ),
      el(
        "p",
        localizedCopy("ui.libraryDownload.messageDriveFreeLibraryUsedLimit",{value1:(String(bytes(p.availableDriveBytes))),value2:(String(bytes(p.scientificStorageUsedBytes))),value3:(String(p.scientificStorageLimitBytes==null?(p.availableDriveBytes==null?"Unknown":"Unlimited"):bytes(p.scientificStorageLimitBytes))),value4:(String(bytes(p.preparationBudgetRemainingBytes)))}),
      ),
      el(
        "p",
        localizedCopy("ui.libraryDownload.messagePreviewExpiresSelection",{value1:(String(dateTime(p.expiresUtc))),value2:(String(p.selectionHash.slice(0, 16)))}),
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
    if (p.nasa) table(previewBox, localizedCopy("ui.libraryDownload.messageExactOriginalNASAFiles"), [localizedCopy("ui.emitLibrary.messageOriginalFile"), localizedCopy("ui.emitLibrary.messageRole"), localizedCopy("ui.libraryDownload.messageVerifiedHeaderLengthBytes"), localizedCopy("ui.libraryDownload.messageSafeRangeResume")], p.nasa.files.map(file => [file.name, file.role, file.bytes, file.etag && file.ranges ? textForUI("ui.libraryDownload.messageSupportedWithIdentityVerification") : textForUI("ui.libraryDownload.messageRestartSafelyIfInterrupted")]));
    const units = disclosure(
      previewBox,
      localizedCopy("ui.libraryDownload.messageExactWorkWindowsRetainedSourceReuse"),
    );
    table(
      units,
      localizedCopy("ui.libraryDownload.messageApprovedGeographicWorkWindows"),
      [localizedCopy("ui.libraryDownload.messageUnit"), localizedCopy("ui.libraryDownload.messageExactSource"), localizedCopy("ui.libraryDownload.messageSouthWestNorthEast"), localizedCopy("ui.libraryDownload.messageRequiredWork")],
      p.work.map((w) => [
        w.index + 1,
        w.scene.scene,
        `${w.area.south}, ${w.area.west} → ${w.area.north}, ${w.area.east}`,
        w.reuseSourceId ? textForUI("ui.libraryDownload.messageVerifiedLocalReuse") : textForUI("ui.libraryDownload.messageNetworkSubsetRequired"),
      ]),
      { pageSize: 50 },
    );
    const approve = button(previewBox, localizedCopy("ui.libraryDownload.messageDownloadImport"), async () => {
      if (preview !== job) throw Error(textForUI("ui.libraryDownload.messageSelectionChangedPreviewAgain"));
      await api(`/api/science/library/jobs/${job.id}/approve`, {
        selectionHash: p.selectionHash,
      });
      preview = null;
      approve.disabled = true;
      setPresentationText(feedback,localizedCopy("ui.libraryDownload.messageApprovedWorkQueuedTheDownloadManagerBelow"));
      await refresh();
    });
    approve.disabled = p.uncoveredCells > 0;
    if (p.uncoveredCells)
      previewBox.append(
        el(
          "p",
          localizedCopy("ui.libraryDownload.messageRemoveUncoveredCellsOrChooseACovering"),
        ),
      );
  }
  const manager = el("section", undefined, "panel library-job-manager");
  setPresentationText(manager,localizedCopy("ui.libraryDownload.messageLibraryDownloadManager"),"aria-label");
  manager.append(el("h2", localizedCopy("ui.libraryDownload.messageDownloadManager")));
  parent.append(manager);
  const history = select(
    parent,
    localizedCopy("ui.libraryDownload.messageDownloadManagerView"),
    [
      ["current", localizedCopy("ui.libraryDownload.messageCurrentCompletedJobs")],
      ["all", localizedCopy("ui.libraryDownload.messageAllJobsIncludingCancelled")],
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
        manager.replaceChildren(el("h2", localizedCopy("ui.libraryDownload.messageDownloadManager")));
        if (response.warning)
          manager.append(el("p", response.warning, "warning"));
        if (!response.jobs.length)
          manager.append(el("p", localizedCopy("ui.libraryDownload.messageNoSavedLibraryJobs")));
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
            localizedCopy("ui.libraryDownload.messageWorkUnitsVerifiedMeasured",{value1:(String(job.completedItems)),value2:(String(job.items.length)),value3:(String(bytes(job.downloadedBytes))),value4:(String(bytes(job.uncertainBytes))),value5:(String(bytes(job.items.reduce((n, i) => n + i.verifiedBytes, 0))))}),
          );
          setPresentationText(live,localizedCopy("ui.libraryDownload.messageDownloadProgress"),"aria-label");renderTransferProgress(box,job.progress,job.state);
          box.append(
            el(
              "p",
              job.preview.products
                .map(
                  (p) => textForUI("ui.libraryDownload.messageObserved",{value1:(String(p.scene)),value2:(String(dateTime(p.observationUtc)))}),
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
            button(controls, localizedCopy("ui.libraryDownload.messagePauseJob"), control("pause"));
          if (["Paused", "Interrupted"].includes(job.state))
            button(controls, localizedCopy("ui.libraryDownload.messageResumeJob"), control("resume"));
          if (job.state === "Failed")
            button(controls, localizedCopy("ui.libraryDownload.messageRetryFailedWork"), control("retry"));
          if (!["Cancelled", "Completed"].includes(job.state))
            button(controls, localizedCopy("ui.libraryDownload.messageCancelJob"), control("cancel"));
          if (job.state === "Awaiting Approval")
            box.append(
              el(
                "p",
                localizedCopy("ui.libraryDownload.messageAwaitingExplicitApprovalCreateACurrentPreview"),
              ),
            );
          const items = disclosure(box, localizedCopy("ui.libraryDownload.messagePerItemProgressVerification"));
          items.addEventListener("toggle", () => {
            if (items.open && items.children.length === 1)
              table(
                items,
                localizedCopy("ui.libraryDownload.messageStoredDownloadItems"),
                [
                  localizedCopy("ui.libraryDownload.messageUnit"),
                  localizedCopy("ui.emitLibrary.messageState"),
                  localizedCopy("ui.libraryDownload.messageMeasuredTransfer"),
                  localizedCopy("ui.libraryDownload.messageVerifiedRetained"),
                  localizedCopy("ui.libraryDownload.messageAttempts"),
                  localizedCopy("ui.libraryDownload.messageError"),
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
          if(job.preview.work.length>500)box.append(el("p",localizedCopy("ui.libraryDownload.messageMapDisplaysTheFirst500OfWork",{value1:(String(job.preview.work.length))})));
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
        const recent = button(navigation, localizedCopy("ui.libraryDownload.messageMoreRecentJobs"), () => {
          jobOffset = Math.max(0, jobOffset - 64);
          jobsSignature = null;
          refresh();
        });
        recent.disabled = jobOffset === 0;
        const older = button(navigation, localizedCopy("ui.libraryDownload.messageOlderSavedJobs"), () => {
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

      await loadLocal();
    } catch (error) {
      if (!stopped)
        setPresentationText(feedback,localizedCopy("ui.libraryDownload.messageCompanionLibraryUnavailable",{value1:(String(error.message))}));
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
      setPresentationText(feedback,localizedCopy("ui.libraryDownload.messageOfflineRetainedCoverageAndSavedJobs"));
  };
  window.addEventListener("offline", online);
  const release = cleanup;
  await refresh();
  return () => {
    window.removeEventListener("offline", online);
    release();
  };
}
