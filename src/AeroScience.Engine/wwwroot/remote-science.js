import {presentationTextNode} from './i18n.js';
import {presentationTooltip} from './workstation-ui.js';
import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {renderLayerContext,scientificCategory} from './scientific-context.js';
import { openHyperspectralImport, hyperspectralCard } from "./hyperspectral-import.js";
import { scientificAccessSettings } from "./scientific-access.js";
import { ResearchMap } from "./map.js";
import { displayReading, onUnitsChanged } from "./science-display.js";
const e = (tag, text) => {
  const node = document.createElement(tag);
  if (text !== undefined) setPresentationText(node,text);
  return node;
};
const action = (text, run, disabled = () => false) => {
  const b = e("button", text);
  b.type = "button";
  b.className = "secondary";
  b.onclick = async () => {
    b.disabled = true;
    try {
      await run();
    } catch (error) {
      alert(error.message);
    } finally {
      b.disabled = disabled();
    }
  };
  return b;
};
const names = {
  get "thermal-imager"(){return textForUI("ui.remoteScience.messageThermalImager");},
  get "multispectral-imager"(){return textForUI("ui.remoteScience.messageMultispectralImager");},
  get "hyperspectral-imager"(){return textForUI("ui.remoteScience.messageHyperspectralImager");},
  get "terrain-scanner"(){return textForUI("ui.remoteScience.messageTerrainScannerLiDARElevation");},
};
const number = (n) =>
  Number.isFinite(n)
    ? n.toLocaleString(undefined, { maximumFractionDigits: 3 })
    : textForUI("ui.remoteScience.messageNoData");
function link(parent, title, href) {
  const a = e("a", title);
  a.href = href;
  parent.append(a, presentationTextNode(" · "));
}
export { scientificSourcesSettings as sourceSettings } from "./scientific-sources.js";
export async function remoteProducts(parent,api,missionId=null,instrumentId=null){
 const section=e("section");section.className="panel research-panel remote-products";section.append(e("h2",localizedCopy("results.title")));parent.append(section);
 const missionTitle=e("h3"),summary=e("p"),datasetSummary=e("div"),list=e("div"),viewer=e("div"),range=e("p"),mapHolder=e("div");section.append(missionTitle);
 const atmosphericRows=new Map();
 let offset=0,total=0,pageCount=0,requestGeneration=0,viewGeneration=0,timer,signature="",map=null,mapOffset=0,atmosAfter=0,atmosShown=0,mapAreas=[],mapSamples=[];
 if(!missionId){const label=e("label",localizedCopy("ui.remoteScience.messageRecentMission")),missions=e("select");setPresentationText(missions,localizedCopy("ui.remoteScience.messageResultsMission"),"aria-label");const empty=e("option",localizedCopy("ui.remoteScience.messageChooseAMissionAllOlderMissionsRemain"));empty.value="";missions.append(empty);for(const m of await api('/api/missions?limit=100')){const option=e("option",m.name);option.value=m.id;missions.append(option);}label.append(missions);section.append(label);missions.onchange=()=>{missionId=missions.value||null;reset();};}
 const label=e("label",localizedCopy("ui.remoteScience.messageInstrument")),filter=e("select");setPresentationText(filter,localizedCopy("ui.remoteScience.messageProductInstrument"),"aria-label");
 const catalog=await api("/api/instruments"),displayNames=Object.fromEntries(catalog.map(i=>[i.id,i.name]));
 for(const [id,name] of [["",textForUI("ui.downloadActivity.messageAllInstruments")],...Object.entries(displayNames)]){const option=e("option",name);option.value=id;filter.append(option);}filter.value=instrumentId||"";label.append(filter);
 const hyperspectral=e("div");section.append(label,summary,datasetSummary,hyperspectral);
 const previous=action(localizedCopy("ui.remoteScience.messageBrowsePrevious"),async()=>{offset=Math.max(0,offset-30);await load();},()=>offset===0),next=action(localizedCopy("ui.remoteScience.messageViewMoreAcquisitions"),async()=>{offset+=30;await load();},()=>offset+pageCount>=total);
 const mapMore=action(localizedCopy("ui.remoteScience.messageViewMoreAcquisitionLocations"),loadLocations);mapMore.hidden=true;
 section.append(action(localizedCopy("ui.remoteScience.messageRefreshResults"),load),action(localizedCopy("ui.remoteScience.messageViewAcquisitionsOnMap"),async()=>{if(!missionId){setPresentationText(summary,localizedCopy("ui.remoteScience.messageSelectAMissionFirst"));return;}if(!map)map=new ResearchMap(mapHolder,null,{coverageOnly:true});await loadLocations();}),mapHolder,mapMore,range,previous,next,list,viewer);
 function reset(){list.replaceChildren();setPresentationText(range,localizedCopy("ui.remoteScience.messageLoadingSelectedResults"));atmosphericRows.clear();datasetSummary.replaceChildren();offset=0;signature="";requestGeneration++;viewGeneration++;viewer.replaceChildren();map?.destroy();map=null;mapOffset=0;atmosAfter=0;atmosShown=0;mapAreas=[];mapSamples=[];mapHolder.replaceChildren();delete mapHolder.dataset.fitted;mapMore.hidden=true;hyperspectral.replaceChildren();if(filter.value==='hyperspectral-imager')hyperspectralCard(hyperspectral,api);load();}
 filter.onchange=reset;
 async function loadLocations(){
  const own=missionId+"|"+filter.value;if(!map||!missionId)return;let more=false;
  if(!filter.value||names[filter.value]){const page=await api(`/api/missions/${missionId}/scientific-locations?offset=${mapOffset}${filter.value?'&instrumentId='+encodeURIComponent(filter.value):''}`);if(own!==missionId+"|"+filter.value||!section.isConnected)return;for(const item of page.items){mapAreas.push({vertices:item.footprint.boundary,label:textForUI("ui.remoteScience.messageFlightSourceFootprint",{value1:(String(item.instrumentId)),value2:(String(item.flightPacketUtc)),value3:(String(item.sourceObservationUtc||'unknown'))})});mapSamples.push(...item.locations.map(p=>({...p,instrumentId:item.instrumentId,timestampUtc:item.flightPacketUtc})));}mapOffset+=page.items.length;more=page.hasMore;}
  if(!filter.value||!names[filter.value]){const page=await api(`/api/missions/${missionId}/scientific-atmosphere-locations?after=${atmosAfter}${filter.value?'&instrumentId='+encodeURIComponent(filter.value):''}`);if(own!==missionId+"|"+filter.value||!section.isConnected)return;mapSamples.push(...page.items.filter(p=>Number.isFinite(p.latitude)&&Number.isFinite(p.longitude)));atmosAfter=page.next;atmosShown+=page.items.length;more||=page.hasMore;}
  map.set({footprints:mapAreas,samples:mapSamples});if(!mapHolder.dataset.fitted){map.fit();mapHolder.dataset.fitted='true';}mapMore.hidden=!more||mapSamples.length>=60000;setPresentationText(range,localizedCopy("ui.remoteScience.messageMapShowsExternalAcquisitionsAtmosphericValues",{value1:(String(mapOffset)),value2:(String(atmosShown)),value3:(String(mapSamples.length)),value4:(String(mapSamples.length>=60000?' Map memory bound reached; filter by instrument or browse individual acquisitions.':''))}));
 }

 async function load(){const own=++requestGeneration;const query=`${missionId?'&missionId='+missionId:''}${filter.value?'&instrumentId='+encodeURIComponent(filter.value):''}`;
  const page=await api(`/api/science/products/page?offset=${offset}${query}`);if(own!==requestGeneration||!section.isConnected)return;
  if(offset>0&&offset>=page.total){offset=Math.max(0,Math.floor(Math.max(0,page.total-1)/30)*30);return load();}
  total=page.total;pageCount=page.items.length;previous.disabled=offset===0;next.disabled=offset+pageCount>=total;setPresentationText(range,page.total?localizedCopy("ui.remoteScience.messageShowingOfAcquisitions",{value1:(String(offset+1)),value2:(String(offset+page.items.length)),value3:(String(page.total))}):localizedCopy("ui.remoteScience.messageNoCompletedAcquisitionsYet"));
  if(missionId){const overview=await api(`/api/missions/${missionId}/scientific-results`);if(own!==requestGeneration||!section.isConnected)return;setPresentationText(missionTitle,overview.mission.name);setPresentationText(summary,(overview.diagnostics||[]).filter(d=>!filter.value||d.instrumentId===filter.value).map(d=>textForUI("ui.remoteScience.messageSuccessfulAcquisitionsSavedValues",{value1:(String(displayNames[d.instrumentId]||d.instrumentId)),value2:(String(d.acquisitionSets)),value3:(String(d.observationCount)),value4:(String(d.rejectedObservations)),value5:(String(d.missingIntervals))})).join(' · '));if(filter.value&&!names[filter.value])setPresentationText(range,localizedCopy("ui.remoteScience.messageRecordedAtmosphericMeasurementValuesAreListedBelow"));for(const row of atmosphericRows.values())row.box.hidden=true;
    for(const d of overview.datasets.filter(d=>!names[d.instrumentId]&&(!filter.value||filter.value===d.instrumentId))){
      let row=atmosphericRows.get(d.id);
      if(!row){const box=e('details'),title=e('summary'),values=e('pre'),label=e('p');let after=0,hasMore=true;
        const more=action(localizedCopy("ui.remoteScience.messageViewMoreMeasurements"),async()=>{const page=await api(`/api/science/datasets/${d.id}/observations?after=${after}&limit=1000`);after=page.next;hasMore=page.hasMore;setPresentationText(values,page.observations.map(o=>`${o.timestampUtc} ${o.measurementId}: ${o.value} ${o.unit} (${o.provenance})`).join('\n'));setPresentationText(label,localizedCopy("ui.remoteScience.messageRecordedValuesShownSourceLabelsAppear",{value1:(String(page.observations.length))}));},()=>!hasMore);
        box.append(title,more,label,values,e('p',localizedCopy("ui.remoteScience.messageExportTheFullMissionForAllRecorded")));row={box,title};atmosphericRows.set(d.id,row);datasetSummary.append(box);
      }
      row.box.hidden=false;setPresentationText(row.title,localizedCopy("ui.remoteScience.messageAtmosphericMeasurementValues",{value1:(String(displayNames[d.instrumentId]||d.instrumentId)),value2:(String(d.observationCount))}));
    }
  }

  const key=JSON.stringify(page.items.map(p=>[p.id,p.bytes]));if(key===signature)return;signature=key;list.replaceChildren();
  for(const p of page.items){const row=e('article');row.className='plan-row';row.append(e('strong',names[p.instrumentId]||p.instrumentId),e('span',localizedCopy("ui.remoteScience.messageFlightSourceMiB",{value1:(String(p.flightPacketUtc)),value2:(String(p.sourceObservationUtc||'Unknown / composite')),value3:(String(p.validSampleCount==null?'Historical valid sample count not indexed':p.validSampleCount+' valid sample positions')),value4:(String(number(p.bytes/1048576)))})),action(localizedCopy("ui.remoteScience.messageViewData"),async()=>{const current=++viewGeneration;const data=await api(`/api/science/products/${p.id}`);if(current!==viewGeneration||!viewer.isConnected)return;viewer.replaceChildren();await viewProduct(viewer,data,api);}));list.append(row);}
 }
 async function poll(){if(!section.isConnected)return;if(!document.hidden&&!section.closest('[hidden]'))try{await load();}catch(error){setPresentationText(summary,error.message);}timer=setTimeout(poll,5000);}
 await load();timer=setTimeout(poll,5000);return section;
}
export async function viewProduct(parent, data, api = null) {
  const p = data.product,
    s = data.source.manifest;
  parent.append(
    e("h3", `${names[p.instrumentId] || p.instrumentId} · ${s.name}`),
    e(
      "p",
      localizedCopy("ui.remoteScience.messageSourceObservationFlightPacket",{value1:(String(p.sourceObservationUtc || "Unknown")),value2:(String(p.flightPacketUtc))}),
    ),
    e(
      "p",
      localizedCopy("ui.remoteScience.messageSource",{value1:(String(s.provider)),value2:(String(scientificCategory(p.sourceClass).label))}),
    ),
    e('p',`${p.validSampleCount==null?textForUI("ui.remoteScience.messageValidSampledPositionsUnavailable"):textForUI("ui.remoteScience.messageValidSampledPositions",{value1:(p.validSampleCount)})} · ${p.quality}`),
  );
  const sourceContext=e('details');sourceContext.append(e('summary',localizedCopy("ui.remoteScience.messageAboutThisSourceMeasurement")));renderLayerContext(sourceContext,{source:data.source,product:p,sourceClass:p.sourceClass,unit:data.bands[0]?.unit,measurement:data.bands[0]?.id,context:{grid:data.bands[0]?.originalGrid}});parent.append(sourceContext);
  const mapHolder = e("div");
  parent.append(mapHolder);
  const map = new ResearchMap(mapHolder,undefined,{fitMaxZoom:19});
  let chart, pointsLayer, disposed = false, stopUnits = () => {};
  const dispose = () => {
    if(disposed)return;
    disposed=true;
    removed.disconnect();stopUnits();chart?.destroy();pointsLayer?.remove();map.destroy();
  };
  const removed = new MutationObserver(() => {
    if(!mapHolder.isConnected)dispose();
  });
  // Own resources before the first await: navigation may remove this view
  // while its protection request or geography is still in flight.
  removed.observe(document.body,{childList:true,subtree:true});
  map.set({plan:{id:p.id,waypoints:[]},footprints:[{vertices:p.footprint.boundary,label:textForUI("ui.remoteScience.messageDerivedAcquisitionFootprintCloudNodataCellsCanBe")}],markers:[],track:[]});
  map.fit(p.footprint.boundary);
  const select = e("select");
  setPresentationText(select,localizedCopy("ui.remoteScience.messageScientificProductView"),"aria-label");
  for (const v of p.availableViews) {
    const option = e("option", v.replaceAll("-", " "));
    option.value = v;
    select.append(option);
  }
  parent.append(select);
  const output = e("div");
  output.className = "remote-product-view";
  parent.append(output);
  const details = e("details");
  details.append(
    e("summary", localizedCopy("ui.remoteScience.messageProvenanceQualityAndLimitations")),
    e('p',p.provenance),
    e('p',localizedCopy("ui.remoteScience.messageProcessingVerticalDatum",{value1:(String(p.processedUtc)),value2:(String(s.verticalDatum))})),
    e("p", p.quality),
    e("p", s.citation),
    e(
      "p",
      localizedCopy("ui.remoteScience.messageFootprintVerticalReference",{value1:(String(p.footprint.method)),value2:(String(p.footprint.verticalReference))}),
    ),
  );
  for (const warning of [...p.warnings, ...data.processingMethods])
    details.append(e("p", warning));
  for (const b of data.bands)
    details.append(
      e(
        "p",
        localizedCopy("ui.remoteScience.messageNativePixel",{value1:(String(b.id)),value2:(String(b.unit)),value3:(String(b.wavelengthNm ? ` · ${b.wavelengthNm} nm` : "")),value4:(String(b.originalGrid.crs)),value5:(String(Math.abs(b.originalGrid.pixelWidth))),value6:(String(Math.abs(b.originalGrid.pixelHeight))),value7:(String(b.originalGrid.crs === "EPSG:4326" ? "degrees" : "m")),value8:(String(b.samplingMethod))}),
      ),
    );
  parent.append(details);
  const advanced=e('details');advanced.append(e('summary',localizedCopy("ui.operatingAssessments.messageAdvancedDiagnostics")),e('pre',JSON.stringify({product:p,sourceManifestSha256:data.source.manifestSha256,processingMethods:data.processingMethods},null,2)));parent.append(advanced);
  if (api) {
    const protection = await api(
      `/api/science/datasets/${p.datasetId}/protection`,
    );
    if(disposed||map.destroyed||!mapHolder.isConnected){dispose();return;}
    const protect = action(
      protection.isProtected ? localizedCopy("ui.remoteScience.messageRemoveDatasetProtection") : localizedCopy("ui.remoteScience.messageProtectDataset"),
      async () => {
        const result = await api(
          `/api/science/datasets/${p.datasetId}/protection`,
          { protected: !protection.isProtected },
        );
        protection.isProtected = result.isProtected;
        setPresentationText(protect,protection.isProtected
          ? localizedCopy("ui.remoteScience.messageRemoveDatasetProtection")
          : localizedCopy("ui.remoteScience.messageProtectDataset"));
      },
    );
    parent.append(protect);
  }
  if (s.kind !== "hyperspectral" && s.kind !== "terrain")
    link(
      parent,
      localizedCopy("ui.remoteScience.messagePNGScalarPreviewRegenerableCache"),
      `/api/science/products/${p.id}/preview.png`,
    );
  const exports = e("p");
  for (const format of [
    "json",
    "csv",
    "geojson",
    "gpx",
    "kml",
    "evidence.json",
    "source.json",
  ])
    link(
      exports,
      localizedCopy("ui.remoteScience.messageExport",{value1:(String(format))}),
      `/api/science/products/${p.id}/export/${format}`,
    );
  parent.append(
    exports,
    e(
      "p",
      localizedCopy("ui.remoteScience.messageEvidenceStaysLocalHashesAreIntegrityAids"),
    ),
  );
  const draw = () => {
    if(disposed||map.destroyed||!mapHolder.isConnected){dispose();return;}
    chart?.destroy();
    chart = null;
    pointsLayer?.remove();
    output.replaceChildren();
    const band = (id) => data.bands.find((b) => b.id === id)?.values || [];
    if (select.value === "spectrum") {
      const sample = e("input");
      sample.type = "number";
      sample.min = 0;
      sample.max = data.sampleLocations.length - 1;
      sample.value = Math.floor(data.sampleLocations.length / 2);
      setPresentationText(sample,localizedCopy("ui.remoteScience.messageSpectralSampleIndex"),"aria-label");
      output.append(
        e(
          "p",
          localizedCopy("ui.remoteScience.messageChooseAValidGeographicSampleBadMissingBands"),
        ),
        sample,
      );
      const label = e("p"),
        holder = e("div");
      holder.className = "science-chart";
      const canvas = e("canvas");
      holder.append(canvas);
      output.append(label, holder);
      const update = () => {
        chart?.destroy();
        const i = Number(sample.value);
        if (!Number.isInteger(i) || i < 0 || i >= data.sampleLocations.length)
          return;
        const point = data.sampleLocations[i],
          values = data.bands.map((b) => ({
            x: b.wavelengthNm,
            y: b.values[i],
          }));
        setPresentationText(label,localizedCopy("ui.remoteScience.messageSample",{value1:(String(i)),value2:(String(point.latitude.toFixed(7))),value3:(String(point.longitude.toFixed(7))),value4:(String(data.bands[0].unit))}));
        if (values.every((v) => v.y == null)) {
          label.textContent += textForUI("ui.remoteScience.messageNoValidSpectrumAtThis");
          return;
        }
        chart = new window.Chart(canvas, {
          type: "line",
          data: {
            datasets: [
              {
                label: data.bands[0].unit,
                data: values,
                borderColor: "#4ce0ce",
                spanGaps: false,
                pointRadius: 2,
              },
            ],
          },
          options: {
            animation: false,
            responsive: true,
            scales: {
              x: {
                type: "linear",
                title: { display: true, text: textForUI("ui.emitLibrary.messageWavelengthNm") },
              },
              y: { title: { display: true, text: data.bands[0].unit } },
            },
          },
        });
      };
      sample.oninput = update;
      update();
      return;
    }
    const canvas = e("canvas");
    canvas.width = p.width;
    canvas.height = p.height;
    canvas.className = "scientific-raster";
    setPresentationText(canvas,localizedCopy("ui.remoteScience.messageColorizedScientificFootprintSampleGrid"),"aria-label");
    const c = canvas.getContext("2d");
    const view = select.value,
      values =
        view === "temperature"
          ? data.derived.temperature.map((k) =>
              k == null
                ? null
                : displayReading("temperature", k - 273.15, "°C").value,
            )
          : view === 'elevation' ? data.derived.elevation.map(value=>displayReading('elevation',value,'m').value) : data.derived[view];
    const finite = (values || []).filter(Number.isFinite),
      min = finite.length ? Math.min(...finite) : 0,
      max = finite.length ? Math.max(...finite) : 1;
    const unit =
      view === "temperature"
        ? displayReading("temperature", 0, "°C").unit
        : view === "elevation"
          ? displayReading('elevation',0,'m').unit + ' (' + s.verticalDatum + ')'
          : view === "ndvi"
            ? textForUI("ui.remoteScience.messageNDVIUnitless")
            : textForUI("ui.remoteScience.messageScaledSourceReflectanceFixed003DisplayStretch");
    const colors = [];
    for (let i = 0; i < data.sampleLocations.length; i++) {
      let color;
      if (view === "natural-color" || view === "false-color") {
        const ids =
            view === "natural-color"
              ? ["B04", "B03", "B02"]
              : ["B08", "B04", "B03"],
          channels = ids.map((id) => band(id)[i]);
        if (channels.every(Number.isFinite))
          color = `rgb(${channels.map((v) => Math.round(Math.max(0, Math.min(1, v / 0.3)) * 255)).join(",")})`;
      } else if (Number.isFinite(values?.[i])) {
        const t = Math.max(
          0,
          Math.min(1, (values[i] - min) / (max - min || 1)),
        );
        color = `rgb(${Math.round(255 * t)},${Math.round(210 * (1 - Math.abs(t - 0.5) * 2))},${Math.round(255 * (1 - t))})`;
      }
      colors.push(color);
      if (color) {
        c.fillStyle = color;
        c.fillRect(i % p.width, Math.floor(i / p.width), 1, 1);
      }
    }
    output.append(
      e(
        "p",
        localizedCopy("ui.remoteScience.messageTransparentCellsAreNodataQualityMasked",{value1:(String(unit)),value2:(String(values ? " · Valid range " + number(min) + " to " + number(max) + " · " + finite.length + " valid / " + values.length + " samples" : ""))}),
      ),
      canvas,
    );
    if (values) {
      const legend = e("div");
      legend.className = "remote-legend";
      setPresentationText(legend,`${number(min)} → ${number(max)} ${unit}`);
      output.append(legend);
    }
    pointsLayer = window.L.layerGroup().addTo(map.map);
    for (let i = 0; i < data.sampleLocations.length; i++)
      if (colors[i]) {
        const loc = data.sampleLocations[i];
        window.L.circleMarker([loc.latitude, loc.longitude], {
          radius: 3,
          color: colors[i],
          fillOpacity: 0.85,
          weight: 0,
        })
          .bindTooltip(
            ()=>presentationTooltip(`${i}: ${values ? number(values[i]) + " " + unit : "Scaled spectral reflectance sample"}`),
          )
          .addTo(pointsLayer);
      }
    output.append(
      action(localizedCopy("ui.remoteScience.messageDownloadPNGPreview"), async () => {
        const image=document.createElement("canvas");image.width=720;image.height=460;
        const drawing=image.getContext("2d");drawing.fillStyle="#0d1b26";drawing.fillRect(0,0,image.width,image.height);
        drawing.imageSmoothingEnabled=false;drawing.drawImage(canvas,16,16,288,288);
        drawing.fillStyle="#ecf5f5";drawing.font="14px sans-serif";
        const lines=[textForUI("ui.remoteScience.messageColorPreviewNotA",{value1:(String(names[p.instrumentId])),value2:(String(view))}),`${unit}${values?` · range ${number(min)} to ${number(max)}`:""}`,textForUI("ui.remoteScience.messageSourceObserved",{value1:(String(p.sourceObservationUtc||"Unknown"))}),textForUI("ui.remoteScience.messageFlightPacket",{value1:(String(p.flightPacketUtc))}),textForUI("ui.remoteScience.messageProcessed",{value1:(String(p.processedUtc))}),textForUI("ui.remoteScience.messageSourceIDOriginalGridsRetainedIn",{value1:(String(p.sourceId))}),textForUI("ui.remoteScience.messageTransparentCellsMissingQualityExcludedSensorOrientedFootprintGrid")];
        lines.forEach((line,i)=>drawing.fillText(line,16,326+i*19,688));
        const blob = await new Promise((resolve) =>
          image.toBlob(resolve, "image/png"),
        );
        const url = URL.createObjectURL(blob),
          a = e("a");
        a.href = url;
        a.download = `AeroScience-${p.id}-${view}-preview.png`;
        a.click();
        URL.revokeObjectURL(url);
      }),
    );
    if (view === "elevation") {
      output.append(action(localizedCopy("ui.remoteScience.messageDownloadElevationDisplayCSV"),async()=>{
        const rows=[[textForUI("ui.atmosphericResearch.messageLatitude"),textForUI("ui.atmosphericResearch.messageLongitude"),textForUI("ui.remoteScience.messageDisplayElevation"),textForUI("ui.remoteScience.messageDisplayUnits"),textForUI("ui.scienceMapView.messageVerticalReference"),textForUI("ui.remoteScience.messageOriginalElevationM")],...data.sampleLocations.map((location,index)=>[location.latitude,location.longitude,values[index],displayReading('elevation',0,'m').unit,s.verticalDatum,data.derived.elevation[index]])];
        const csv=rows.map(row=>row.map(value=>'"'+String(typeof value==='string'&&/^[=+@-]/.test(value)?"'"+value:value??'').replaceAll('"','""')+'"').join(',')).join('\r\n');const a=e('a');a.href=URL.createObjectURL(new Blob([csv],{type:'text/csv'}));a.download='AeroScience-terrain-display.csv';a.click();setTimeout(()=>URL.revokeObjectURL(a.href),1000);
      }));
      link(output,localizedCopy("ui.remoteScience.messageOpenElevationProfileAlongAFlightRoute"),`#workspace/map?missions=${p.missionId}&instrument=terrain-scanner&source=${p.sourceId}`);
    }
  };
  select.onchange = draw;
  draw();
  if(!disposed)stopUnits=onUnitsChanged(draw);
}
