import { openHyperspectralImport, hyperspectralCard } from "./hyperspectral-import.js";
import { scientificAccessSettings } from "./scientific-access.js";
import { ResearchMap } from "./map.js";
import { displayReading, onUnitsChanged } from "./science-display.js";
const e = (tag, text) => {
  const node = document.createElement(tag);
  if (text !== undefined) node.textContent = text;
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
  "thermal-imager": "Thermal Imager",
  "multispectral-imager": "Multispectral Imager",
  "hyperspectral-imager": "Hyperspectral Imager",
  "terrain-scanner": "Terrain Scanner (LiDAR / Elevation)",
};
const number = (n) =>
  Number.isFinite(n)
    ? n.toLocaleString(undefined, { maximumFractionDigits: 3 })
    : "No data";
function link(parent, title, href) {
  const a = e("a", title);
  a.href = href;
  parent.append(a, document.createTextNode(" · "));
}
export { scientificSourcesSettings as sourceSettings } from "./scientific-sources.js";
export async function remoteProducts(parent,api,missionId=null,instrumentId=null){
 const section=e("section");section.className="panel research-panel remote-products";section.append(e("h2","Scientific Results"));parent.append(section);
 const missionTitle=e("h3"),summary=e("p"),datasetSummary=e("div"),list=e("div"),viewer=e("div"),range=e("p"),mapHolder=e("div");section.append(missionTitle);
 const atmosphericRows=new Map();
 let offset=0,total=0,pageCount=0,requestGeneration=0,viewGeneration=0,timer,signature="",map=null,mapOffset=0,atmosAfter=0,atmosShown=0,mapAreas=[],mapSamples=[];
 if(!missionId){const label=e("label","Recent mission "),missions=e("select");missions.setAttribute("aria-label","Results mission");const empty=e("option","Choose a mission (all older missions remain in Mission History)");empty.value="";missions.append(empty);for(const m of await api('/api/missions?limit=100')){const option=e("option",m.name);option.value=m.id;missions.append(option);}label.append(missions);section.append(label);missions.onchange=()=>{missionId=missions.value||null;reset();};}
 const label=e("label","Instrument "),filter=e("select");filter.setAttribute("aria-label","Product instrument");
 const catalog=await api("/api/instruments"),displayNames=Object.fromEntries(catalog.map(i=>[i.id,i.name]));
 for(const [id,name] of [["","All instruments"],...Object.entries(displayNames)]){const option=e("option",name);option.value=id;filter.append(option);}filter.value=instrumentId||"";label.append(filter);
 const hyperspectral=e("div");section.append(label,summary,datasetSummary,hyperspectral);
 const previous=action("Browse Previous",async()=>{offset=Math.max(0,offset-30);await load();},()=>offset===0),next=action("View More Acquisitions",async()=>{offset+=30;await load();},()=>offset+pageCount>=total);
 const mapMore=action("View More Acquisition Locations",loadLocations);mapMore.hidden=true;
 section.append(action("Refresh Results",load),action("View Acquisitions on Map",async()=>{if(!missionId){summary.textContent="Select a mission first.";return;}if(!map)map=new ResearchMap(mapHolder,null,{coverageOnly:true});await loadLocations();}),mapHolder,mapMore,range,previous,next,list,viewer);
 function reset(){list.replaceChildren();range.textContent="Loading selected results…";atmosphericRows.clear();datasetSummary.replaceChildren();offset=0;signature="";requestGeneration++;viewGeneration++;viewer.replaceChildren();map?.destroy();map=null;mapOffset=0;atmosAfter=0;atmosShown=0;mapAreas=[];mapSamples=[];mapHolder.replaceChildren();delete mapHolder.dataset.fitted;mapMore.hidden=true;hyperspectral.replaceChildren();if(filter.value==='hyperspectral-imager')hyperspectralCard(hyperspectral,api);load();}
 filter.onchange=reset;
 async function loadLocations(){
  const own=missionId+"|"+filter.value;if(!map||!missionId)return;let more=false;
  if(!filter.value||names[filter.value]){const page=await api(`/api/missions/${missionId}/scientific-locations?offset=${mapOffset}${filter.value?'&instrumentId='+encodeURIComponent(filter.value):''}`);if(own!==missionId+"|"+filter.value||!section.isConnected)return;for(const item of page.items){mapAreas.push({vertices:item.footprint.boundary,label:`${item.instrumentId} / flight ${item.flightPacketUtc} / source ${item.sourceObservationUtc||'unknown'}. Footprint envelope; masked gaps remain.`});mapSamples.push(...item.locations.map(p=>({...p,instrumentId:item.instrumentId,timestampUtc:item.flightPacketUtc})));}mapOffset+=page.items.length;more=page.hasMore;}
  if(!filter.value||!names[filter.value]){const page=await api(`/api/missions/${missionId}/scientific-atmosphere-locations?after=${atmosAfter}${filter.value?'&instrumentId='+encodeURIComponent(filter.value):''}`);if(own!==missionId+"|"+filter.value||!section.isConnected)return;mapSamples.push(...page.items.filter(p=>Number.isFinite(p.latitude)&&Number.isFinite(p.longitude)));atmosAfter=page.next;atmosShown+=page.items.length;more||=page.hasMore;}
  map.set({footprints:mapAreas,samples:mapSamples});if(!mapHolder.dataset.fitted){map.fit();mapHolder.dataset.fitted='true';}mapMore.hidden=!more||mapSamples.length>=60000;range.textContent=`Map shows ${mapOffset} external acquisitions / ${atmosShown} atmospheric values and ${mapSamples.length} valid sample locations. Footprints overlap independently; no imagery composite or continuous coverage is implied.${mapSamples.length>=60000?' Map memory bound reached; filter by instrument or browse individual acquisitions.':''}`;
 }

 async function load(){const own=++requestGeneration;const query=`${missionId?'&missionId='+missionId:''}${filter.value?'&instrumentId='+encodeURIComponent(filter.value):''}`;
  const page=await api(`/api/science/products/page?offset=${offset}${query}`);if(own!==requestGeneration||!section.isConnected)return;
  if(offset>0&&offset>=page.total){offset=Math.max(0,Math.floor(Math.max(0,page.total-1)/30)*30);return load();}
  total=page.total;pageCount=page.items.length;previous.disabled=offset===0;next.disabled=offset+pageCount>=total;range.textContent=page.total?`Showing ${offset+1}–${offset+page.items.length} of ${page.total} acquisitions`:'No completed acquisitions yet.';
  if(missionId){const overview=await api(`/api/missions/${missionId}/scientific-results`);if(own!==requestGeneration||!section.isConnected)return;missionTitle.textContent=overview.mission.name;summary.textContent=(overview.diagnostics||[]).filter(d=>!filter.value||d.instrumentId===filter.value).map(d=>`${displayNames[d.instrumentId]||d.instrumentId}: ${d.acquisitionSets} successful acquisitions / ${d.observationCount} saved values or products / ${d.rejectedObservations} unavailable / ${d.missingIntervals} missing intervals`).join(' · ');if(filter.value&&!names[filter.value])range.textContent="Recorded atmospheric measurement values are listed below; no external imagery is expected for this instrument.";for(const row of atmosphericRows.values())row.box.hidden=true;
    for(const d of overview.datasets.filter(d=>!names[d.instrumentId]&&(!filter.value||filter.value===d.instrumentId))){
      let row=atmosphericRows.get(d.id);
      if(!row){const box=e('details'),title=e('summary'),values=e('pre'),label=e('p');let after=0,hasMore=true;
        const more=action('View More Measurements',async()=>{const page=await api(`/api/science/datasets/${d.id}/observations?after=${after}&limit=1000`);after=page.next;hasMore=page.hasMore;values.textContent=page.observations.map(o=>`${o.timestampUtc} ${o.measurementId}: ${o.value} ${o.unit} (${o.provenance})`).join('\n');label.textContent=`${page.observations.length} recorded values shown. Source labels appear beside each value; these are not archived satellite measurements.`;},()=>!hasMore);
        box.append(title,more,label,values,e('p','Export the full mission for all recorded observations.'));row={box,title};atmosphericRows.set(d.id,row);datasetSummary.append(box);
      }
      row.box.hidden=false;row.title.textContent=`${displayNames[d.instrumentId]||d.instrumentId} — ${d.observationCount} atmospheric measurement values`;
    }
  }

  const key=JSON.stringify(page.items.map(p=>[p.id,p.bytes]));if(key===signature)return;signature=key;list.replaceChildren();
  for(const p of page.items){const row=e('article');row.className='plan-row';row.append(e('strong',names[p.instrumentId]||p.instrumentId),e('span',`Flight ${p.flightPacketUtc} · Source ${p.sourceObservationUtc||'Unknown / composite'} · ${p.validSampleCount==null?'Historical valid sample count not indexed':p.validSampleCount+' valid sample positions'} · ${number(p.bytes/1048576)} MiB`),action('View Data',async()=>{const current=++viewGeneration;const data=await api(`/api/science/products/${p.id}`);if(current!==viewGeneration||!viewer.isConnected)return;viewer.replaceChildren();await viewProduct(viewer,data,api);}));list.append(row);}
 }
 async function poll(){if(!section.isConnected)return;if(!document.hidden&&!section.closest('[hidden]'))try{await load();}catch(error){summary.textContent=error.message;}timer=setTimeout(poll,5000);}
 await load();timer=setTimeout(poll,5000);return section;
}
export async function viewProduct(parent, data, api = null) {
  const p = data.product,
    s = data.source.manifest;
  parent.append(
    e("h3", `${names[p.instrumentId] || p.instrumentId} · ${s.name}`),
    e("p", p.provenance),
    e(
      "p",
      `Source observation: ${p.sourceObservationUtc || "Unknown"} · Flight packet: ${p.flightPacketUtc} · Processing: ${p.processedUtc}`,
    ),
    e(
      "p",
      `Source: ${s.provider} · ${s.license} · Vertical datum: ${s.verticalDatum}`,
    ),
  );
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
  map.set({plan:{id:p.id,waypoints:[]},footprints:[{vertices:p.footprint.boundary,label:"Derived acquisition footprint; cloud/nodata cells can be excluded"}],markers:[],track:[]});
  map.fit(p.footprint.boundary);
  const select = e("select");
  select.setAttribute("aria-label", "Scientific product view");
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
    e("summary", "Provenance, quality and limitations"),
    e("p", p.quality),
    e("p", s.citation),
    e(
      "p",
      `Footprint: ${p.footprint.method}. Vertical reference: ${p.footprint.verticalReference}`,
    ),
    e(
      "p",
      `Integrity: ${p.sha256}; source collection ${data.source.manifestSha256}`,
    ),
  );
  for (const warning of [...p.warnings, ...data.processingMethods])
    details.append(e("p", warning));
  for (const b of data.bands)
    details.append(
      e(
        "p",
        `${b.id} · ${b.unit}${b.wavelengthNm ? ` · ${b.wavelengthNm} nm` : ""} · ${b.originalGrid.crs} · native pixel ${Math.abs(b.originalGrid.pixelWidth)}×${Math.abs(b.originalGrid.pixelHeight)} ${b.originalGrid.crs === "EPSG:4326" ? "degrees" : "m"} · ${b.samplingMethod}`,
      ),
    );
  parent.append(details);
  if (api) {
    const protection = await api(
      `/api/science/datasets/${p.datasetId}/protection`,
    );
    if(disposed||map.destroyed||!mapHolder.isConnected){dispose();return;}
    const protect = action(
      protection.isProtected ? "Remove Dataset Protection" : "Protect Dataset",
      async () => {
        const result = await api(
          `/api/science/datasets/${p.datasetId}/protection`,
          { protected: !protection.isProtected },
        );
        protection.isProtected = result.isProtected;
        protect.textContent = protection.isProtected
          ? "Remove Dataset Protection"
          : "Protect Dataset";
      },
    );
    parent.append(protect);
  }
  if (s.kind !== "hyperspectral")
    link(
      parent,
      "PNG scalar preview (regenerable cache)",
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
      "Export " + format,
      `/api/science/products/${p.id}/export/${format}`,
    );
  parent.append(
    exports,
    e(
      "p",
      "Evidence stays local. Hashes are integrity aids, not proof of flight authenticity or an objective pass/fail result.",
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
      sample.setAttribute("aria-label", "Spectral sample index");
      output.append(
        e(
          "p",
          "Choose a valid geographic sample. Bad/missing bands stay gaps; reflectance and radiance are never interchanged.",
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
        label.textContent = `Sample ${i} · ${point.latitude.toFixed(7)}, ${point.longitude.toFixed(7)} · ${data.bands[0].unit}`;
        if (values.every((v) => v.y == null)) {
          label.textContent += " · No valid spectrum at this sample";
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
                title: { display: true, text: "Wavelength (nm)" },
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
    canvas.setAttribute(
      "aria-label",
      "Colorized scientific footprint sample grid",
    );
    const c = canvas.getContext("2d");
    const view = select.value,
      values =
        view === "temperature"
          ? data.derived.temperature.map((k) =>
              k == null
                ? null
                : displayReading("temperature", k - 273.15, "°C").value,
            )
          : data.derived[view];
    const finite = (values || []).filter(Number.isFinite),
      min = finite.length ? Math.min(...finite) : 0,
      max = finite.length ? Math.max(...finite) : 1;
    const unit =
      view === "temperature"
        ? displayReading("temperature", 0, "°C").unit
        : view === "elevation"
          ? "m (" + s.verticalDatum + ")"
          : view === "ndvi"
            ? "NDVI (unitless)"
            : "Scaled source reflectance; fixed 0–0.3 display stretch";
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
        `${unit}${values ? " · Valid range " + number(min) + " to " + number(max) + " · " + finite.length + " valid / " + values.length + " samples" : ""}. Transparent cells are nodata/quality masked. The grid follows sensor orientation; the map locates each actual sample.`,
      ),
      canvas,
    );
    if (values) {
      const legend = e("div");
      legend.className = "remote-legend";
      legend.textContent = `${number(min)} → ${number(max)} ${unit}`;
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
            `${i}: ${values ? number(values[i]) + " " + unit : "Scaled spectral reflectance sample"}`,
          )
          .addTo(pointsLayer);
      }
    output.append(
      action("Download PNG preview", async () => {
        const image=document.createElement("canvas");image.width=720;image.height=460;
        const drawing=image.getContext("2d");drawing.fillStyle="#0d1b26";drawing.fillRect(0,0,image.width,image.height);
        drawing.imageSmoothingEnabled=false;drawing.drawImage(canvas,16,16,288,288);
        drawing.fillStyle="#ecf5f5";drawing.font="14px sans-serif";
        const lines=[`${names[p.instrumentId]} · ${view} · Color preview, not a numeric GeoTIFF`,`${unit}${values?` · range ${number(min)} to ${number(max)}`:""}`,`Source observed: ${p.sourceObservationUtc||"Unknown"}`,`Flight packet: ${p.flightPacketUtc}`,`Processed: ${p.processedUtc}`,`Source ID ${p.sourceId} · original grids retained in JSON`,"Transparent cells: missing/quality excluded. Sensor-oriented footprint grid."];
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
      const line = e("div"),
        profile = e("canvas");
      line.className = "science-chart";
      line.append(profile);
      output.append(line);
      const row = Math.floor(p.height / 2),
        profileValues = values.slice(row * p.width, (row + 1) * p.width);
      chart = new window.Chart(profile, {
        type: "line",
        data: {
          labels: profileValues.map((_, i) => i + 1),
          datasets: [
            {
              label: "Elevation (m), " + s.verticalDatum,
              data: profileValues,
              spanGaps: false,
              borderColor: "#e0c070",
            },
          ],
        },
        options: {
          animation: false,
          scales: {
            x: {
              title: {
                display: true,
                text: "Footprint center-row sample (not distance)",
              },
            },
            y: { title: { display: true, text: "Elevation (m)" } },
          },
        },
      });
    }
  };
  select.onchange = draw;
  draw();
  if(!disposed)stopUnits=onUnitsChanged(draw);
}
