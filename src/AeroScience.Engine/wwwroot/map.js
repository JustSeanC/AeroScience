import {mapLayerManager} from './map-layer-manager.js';
import { planningDistance, onUnitsChanged } from "./science-display.js";
import { airportLocations, searchLocations, locationLabel } from "./location-search.js";
import "./geographic-view.js";
import {table as geographicTable} from './workstation-ui.js';
const valid = (p) =>
  Number.isFinite(p?.latitude) && Number.isFinite(p?.longitude);
const longitude = (x) => window.AeroScienceGeo.wrap(x);
const ll = (p, base = p.longitude) => [
  Math.max(-85.0511, Math.min(85.0511, p.latitude)),
  base + longitude(p.longitude - base),
];
let geography;
const loadGeography = () =>
  (geography ??= Promise.all(
    ["countries", "places", "airports", "airport-search"].map(async (name) => {
      const response = await fetch(`/geography/${name}.${name === "airport-search" ? "json" : "geojson"}`);
      if (!response.ok) throw Error("Bundled geography unavailable");
      const value = await response.json();
      return name === "airport-search" ? airportLocations(value) : value;
    }),
  ));
const label = (value) => {
  const node = document.createElement("span");
  node.textContent = value;
  return node;
};
const activeMaps = new Set();
export class ResearchMap {
  constructor(parent, onPoint, options = {}) {
    for (const previous of activeMaps)
      if (!previous.container.isConnected) previous.destroy();
    activeMaps.add(this);
    this.unitsCleanup=onUnitsChanged(()=>{this.staticKey=null;this.draw();});
    this.locations = [];
    this.data = {};
    this.onPoint = onPoint;
    this.options = options;
    const tools = document.createElement("div");
    tools.className = "map-tools";tools.hidden=Boolean(options.coverageOnly);
    this.container = document.createElement("div");
    this.container.className = "geographic-map";
    this.container.setAttribute(
      "aria-label",
      "Geographic mission map. Pan by dragging; wheel or pinch to zoom.",
    );
    this.coordinate = document.createElement("p");
    this.coordinate.className = "footnote";
    parent.append(tools, this.container, this.coordinate);
    this.routeValues=document.createElement('details');this.routeValues.className='advanced-details';this.routeValues.hidden=true;this.routeValues.append(document.createElement('summary'));this.routeValues.firstChild.textContent='Route waypoints — accessible values and selection';this.routeRows=document.createElement('div');this.routeValues.append(this.routeRows);parent.append(this.routeValues);
    this.routeValues.addEventListener('toggle',()=>{if(this.routeValues.open)this.showRouteValues();});
    const L = window.L;
    this.L = L;
    this.map = L.map(this.container, {
      worldCopyJump: true,
      preferCanvas: true,
      zoomControl: true,
      // Leaflet 1.9's delayed CSS zoom completion can outlive remove().
      // Product maps are replaceable; keep zoom immediate and teardown synchronous.
      zoomAnimation: false,
    }).setView([38, -97], 4);
    this.map.on("moveend",()=>{if(this.destroyed)return;const center=this.map.getCenter();this.container.dataset.center=JSON.stringify([center.lat,center.lng,this.map.getZoom()]);});
    this.container.dataset.center=JSON.stringify([38,-97,4]);
    this.map.attributionControl.addAttribution(
      "Leaflet · Natural Earth (public domain) · Geographic reference, not navigation",
    );
    this.route = L.polyline([], { color: "#f5cd79", dashArray: "8 6" }).addTo(
      this.map,
    );
    this.track = L.layerGroup().addTo(this.map);
    this.points = L.layerGroup().addTo(this.map);
    this.areas = new L.FeatureGroup().addTo(this.map);
    this.markers=L.layerGroup().addTo(this.map);
    this.samples=L.layerGroup().addTo(this.map);
    this.footprints=L.layerGroup().addTo(this.map);
    this.aircraft=L.layerGroup().addTo(this.map);
    this.patterns=L.layerGroup().addTo(this.map);
    this.readiness=L.layerGroup().addTo(this.map);
    this.draftLayer=L.layerGroup().addTo(this.map);
    this.previewLayer=L.layerGroup().addTo(this.map);
    const overlays=[
      ['planned-route','Planned route',this.route,'Mission'],['patterns','Semantic pattern outlines',this.patterns,'Mission'],
      ['waypoints','Waypoint markers',this.points,'Mission'],['recorded-track','Recorded track',this.track,'Mission'],
      ['survey-areas','Survey areas',this.areas,'Mission'],['aircraft','Aircraft',this.aircraft,'Mission'],
      ['mission-draft','Draft route and survey geometry',this.draftLayer,'Mission'],['mission-preview','Pattern preview and modeled geometry',this.previewLayer,'Mission'],
      ['observations','Observation markers',this.markers,'Science'],['sample-locations','Scientific sample locations',this.samples,'Science'],
      ['science-readiness','Scientific data readiness',this.readiness,'Science'],['science-footprints','Science footprints',this.footprints,'Science']
    ].map(([id,name,layer,group])=>({id,name,layer,group}));
    const action = (name, fn) => {
      const b = document.createElement("button");
      b.type = "button";
      b.className = "secondary";
      b.textContent = name;
      b.onclick = fn;
      tools.append(b);
      return b;
    };
    action("Fit route & track", () => this.fit());
    action("Center aircraft", () => {
      if (valid(this.data.aircraft))
        this.map.setView(
          ll(this.data.aircraft),
          Math.max(this.map.getZoom(), 9),
        );
      else this.coordinate.textContent = "No live aircraft position available.";
    });
    const search = document.createElement("input");
    search.type = "search";
    search.placeholder = "City, airport, ICAO/IATA or lat, lon";
    search.setAttribute("aria-label", "Offline geographic location search");
    tools.append(search);
    const results = document.createElement("select");
    results.setAttribute("aria-label", "Search results");results.hidden=true;results.size=5;tools.append(results);
    const searchStatus=document.createElement("p");searchStatus.setAttribute("role","status");tools.append(searchStatus);
    const choose = p => {
      if(this.destroyed||!p)return;
      this.center(p);this.initialized=true;
      if(this.searchMarker)this.map.removeLayer(this.searchMarker);
      this.searchMarker=L.circleMarker(ll(p),{radius:8,color:"#ffffff"}).bindTooltip(label(locationLabel(p))).addTo(this.map);
      searchStatus.textContent="Selected: "+locationLabel(p);
    };
    const find = async () => {
      const q=search.value.trim();results.replaceChildren();results.hidden=true;
      if(!q){searchStatus.textContent="Enter an airport code, name, city or latitude, longitude. Your map position has not changed.";return;}
      await this.ready;
      if(this.destroyed)return;
      const matches=searchLocations(q,this.airportSearch||[],this.places||[]);
      const placeholder=document.createElement("option");placeholder.textContent="Select a result to center the map";placeholder.value="";results.append(placeholder);
      for(const [i,p] of matches.entries()){const option=document.createElement("option");option.value=String(i);option.textContent=locationLabel(p);results.append(option);}
      results.value="";results.hidden=!matches.length;
      results.onchange=()=>{if(results.value!=="")choose(matches[Number(results.value)]);};
      searchStatus.textContent=matches.length?`${matches.length} results. Select one to center the map. Search alone does not move it.`:"No results. Try an ICAO/IATA code, airport name, city or explicit latitude, longitude. Map position unchanged.";
    };
    const findButton=action("Find", find);
    const searchGroup=document.createElement("div");searchGroup.className="map-search";
    searchGroup.append(search,findButton);tools.append(searchGroup);
    search.onkeydown = (e) => {
      if (e.key === "Enter") {
        e.preventDefault();
        find();
      }
    };
    this.onlineTiles = [];
    this.applyBasemap = (client) => {
      if(this.destroyed)return;
      this.basemapClient = client;
      this.basemapVisible = !!(
        this.container.clientWidth &&
        this.container.clientHeight &&
        !document.hidden
      );
      for (const tile of this.onlineTiles) this.map.removeLayer(tile);
      this.onlineTiles = [];
      if (this.esriLogo) {
        this.map.removeControl(this.esriLogo);
        this.esriLogo = null;
      }
      this.onlineLayer = client.layer;
      this.layerError = client.error || null;
      this.countries?.setStyle({
        opacity: client.tiles.length ? 0 : 1,
        fillOpacity: client.tiles.length ? 0 : 0.65,
      });
      if (!this.basemapVisible) return;
      for (const source of client.tiles || []) {
        const attribution = label(source.attribution);
        if (client.layer === "streets") {
          const link = document.createElement("a");
          link.href = "https://www.openstreetmap.org/copyright";
          link.textContent = "© OpenStreetMap contributors";
          link.target = "_blank";
          link.rel = "noopener noreferrer";
          attribution.replaceChildren(link);
        }
        const safe = attribution.outerHTML;
        const tile = L.tileLayer(source.url, {
          maxZoom: source.maxZoom,
          attribution: safe,
          updateWhenIdle: true,
          updateWhenZooming: false,
          keepBuffer: 0,
        }).addTo(this.map);
        tile.once("tileerror", () => {
          if (this.destroyed || !this.onlineTiles.includes(tile)) return;
          for (const layer of this.onlineTiles) this.map.removeLayer(layer);
          this.onlineTiles = [];
          if (this.esriLogo) {
            this.map.removeControl(this.esriLogo);
            this.esriLogo = null;
          }
          this.countries?.setStyle({ opacity: 1, fillOpacity: 0.65 });
          this.layerError =
            "Online provider unavailable or quota/authentication failed. Offline geography remains available; route and recording are unchanged. Select another layer or retry in Settings.";
          this.coordinate.textContent = this.layerError;
        });
        this.onlineTiles.push(tile);
      }
      if ((client.layer.startsWith("satellite") || client.layer.startsWith("esri-")) && this.onlineTiles.length) {
        const Logo = L.Control.extend({
          onAdd() {
            const a = document.createElement("a");
            a.href = "https://www.esri.com/";
            a.target = "_blank";
            a.rel = "noopener noreferrer";
            const img = document.createElement("img");
            img.src = "https://js.arcgis.com/3.46/esri/images/map/logo-med.png";
            img.alt = "Powered by Esri";
            img.width = 65;
            img.style.height = "auto";
            a.append(img);
            return a;
          },
        });
        this.esriLogo = new Logo({ position: "bottomleft" }).addTo(this.map);
      }
    };
    this.layers = mapLayerManager(L,this.map,overlays,this.applyBasemap,{
      beginReadOnly:()=>{const mode=this.interactionMode;this.interaction(null);return()=>this.interaction(mode);}
    });
    this.layerCleanup = ()=>this.layers.dispose();
    tools.append(results,searchStatus);
    this.map.on("click", (e) => {
      if (onPoint && this.interactionMode && !this.drawing)
        onPoint({ latitude: e.latlng.lat, longitude: longitude(e.latlng.lng) });
    });
    if (options.onAreasChange && options.legacyDrawControls) {
      this.map.addControl(
        new L.Control.Draw({
          draw: {
            polygon: { allowIntersection: false },
            polyline: false,
            rectangle: false,
            circle: false,
            circlemarker: false,
            marker: false,
          },
          edit: { featureGroup: this.areas },
        }),
      );
      this.map.on(
        "draw:drawstart draw:editstart draw:deletestart",
        () => (this.drawing = true),
      );
      this.map.on("draw:drawstop draw:editstop draw:deletestop", () =>
        setTimeout(() => (this.drawing = false), 0),
      );
      this.map.on("draw:created", (e) => {
        e.layer.areaName = `Survey area ${this.areas.getLayers().length + 1}`;
        this.areas.addLayer(e.layer);
        this.areaChanges();
      });
      this.map.on("draw:edited draw:deleted", () => this.areaChanges());
    }
    this.visibilityChanged = () => {
      if(this.destroyed)return;
      const visible = !!(
        this.container.clientWidth &&
        this.container.clientHeight &&
        !document.hidden
      );
      if (this.basemapClient && visible !== this.basemapVisible)
        this.applyBasemap(this.basemapClient);
    };
    document.addEventListener("visibilitychange", this.visibilityChanged);
    this.observer = new ResizeObserver(() => {
      if(this.destroyed)return;
      this.map.invalidateSize({ pan: false });
      this.visibilityChanged();
    });
    this.observer.observe(this.container);
    this.ready = loadGeography()
      .then(([countries, places, airports, airportSearch]) => {
        if(this.destroyed)return;
        this.airportSearch=airportSearch;this.places=places.features;
        this.countries = L.geoJSON(countries, {
          style: {
            color: "#567080",
            weight: 1,
            fillColor: "#193039",
            fillOpacity:
              this.onlineLayer && this.onlineLayer !== "offline" ? 0 : 0.65,
            opacity: this.onlineLayer && this.onlineLayer !== "offline" ? 0 : 1,
          },
          onEachFeature: (f, l) =>
            l.bindTooltip(label(f.properties.NAME || f.properties.ADMIN)),
        })
          .addTo(this.map)
          .bringToBack();
        this.locations = [...places.features, ...airports.features];
        this.context = L.layerGroup();
        for (const f of this.locations) {
          const airport = !!(f.properties.iata_code || f.properties.gps_code);
          const c = f.geometry.coordinates;
          const m = L.circleMarker([c[1], c[0]], {
            radius: airport ? 3 : 2,
            color: airport ? "#acc7ee" : "#9caeb8",
            weight: 1,
            fillOpacity: 0.7,
          });
          m.bindTooltip(
            label(
              (airport ? "Airport: " : "") +
                (f.properties.NAME || f.properties.name) +
                (f.properties.gps_code ? " · " + f.properties.gps_code : ""),
            ),
          );
          this.context.addLayer(m);
        }
        const context = () => {
          if(this.destroyed)return;
          if (this.map.getZoom() >= 5) this.context.addTo(this.map);
          else this.map.removeLayer(this.context);
        };
        this.map.on("zoomend", context);
        context();
        this.resolveDeparture();
      })
      .catch(() => {
        if(this.destroyed)return;
        this.coordinate.textContent =
          "Bundled geography could not load. Coordinate editing remains available; check installation files.";
        this.locations = [];
      });
    this.map.whenReady(() => this.draw());
  }
  destroy() {
    if(this.destroyed)return;
    this.destroyed=true;
    activeMaps.delete(this);
    this.layerCleanup?.();this.unitsCleanup?.();
    this.observer?.disconnect();
    document.removeEventListener("visibilitychange", this.visibilityChanged);
    this.map.remove();
  }
  areaChanges() {
    if(this.destroyed)return;
    this.options.onAreasChange(
      this.areas.getLayers().map((l) => ({
        name: l.areaName || "Survey area",
        vertices: l
          .getLatLngs()[0]
          .map((p) => ({ latitude: p.lat, longitude: longitude(p.lng) })),
      })),
    );
  }
  setReadiness(states,{limit=128}={}) {
    if(this.destroyed)return;
    const key=JSON.stringify(states.map(s=>[s.key,s.status,s.area,s.sourceCoverage]));if(key===this.readinessKey)return;this.readinessKey=key;this.readiness.clearLayers();
    const colors={Ready:"#3adbb0","Dataset Ready":"#3adbb0",Downloading:"#62bbff",Queued:"#e9ce70","Partially usable":"#e6a453","Limited Coverage":"#e6a453",Unavailable:"#df6975","Dataset Required":"#df6975","Dataset Outside Coverage":"#a08ab8",Offline:"#a0aab8"};
    for(const s of states.slice(0,Math.min(4096,limit))){const a=s.area;if(!a||s.status==="Superseded")continue;(s.sourceCoverage?.length?this.L.polygon(s.sourceCoverage.map(p=>[p.latitude,p.longitude]),{color:colors[s.status]||"#a0aab8",weight:1,fillOpacity:.08}):this.L.rectangle([[a.south,a.west],[a.north,a.east]],{color:colors[s.status]||"#a0aab8",weight:1,fillOpacity:.08})).bindTooltip(label(`${s.kind}: ${s.status} · Requested preparation envelope; per-pixel masks and actual source coverage still apply`)).addTo(this.readiness);}
  }
  set(data) {
    if(this.destroyed)return;
    const planId = data.plan?.id || null;
    if (planId !== this.planId) {
      this.planId = planId;
      this.initialized = false;
    }
    this.data = data;
    this.draw();
    const hasPlan =
      (data.plan?.waypoints?.length || data.plan?.surveyAreas?.length || 0) > 0;
    if (!this.initialized && hasPlan) {
      this.fit([
        ...(data.plan.waypoints || []),
        ...(data.plan.surveyAreas || []).flatMap((a) => a.vertices),
      ]);
      this.initialized = true;
    }
    this.resolveDeparture();
    if (!this.initialized && !data.plan?.departure && valid(data.aircraft)) {
      this.map.setView(ll(data.aircraft), 9);
      this.initialized = true;
    }
  }
  geometry() {
    const d = this.data;
    return [
      ...(d.plan?.waypoints || []),
      ...(d.plan?.surveyAreas || []).flatMap((a) => a.vertices),
      ...(d.track || []),
      d.aircraft,
    ].filter(valid);
  }
  resolveDeparture() {
    if (this.destroyed || this.initialized || !this.locations?.length) return;
    const q = this.data.plan?.departure?.trim().toLowerCase();
    const f =
      q &&
      this.locations.find((f) =>
        Object.values(f.properties).some((v) => String(v).toLowerCase() === q),
      );
    if (f) {
      this.map.setView(
        [f.geometry.coordinates[1], f.geometry.coordinates[0]],
        8,
      );
      this.initialized = true;
    } else if (valid(this.data.aircraft)) {
      this.map.setView(ll(this.data.aircraft), 9);
      this.initialized = true;
    }
  }
  interaction(mode) {
    if(this.destroyed)return;
    this.interactionMode=mode;
    this.container.classList.toggle("map-placement-mode",!!mode);
  }
  draft(points=[],polygon=false) {
    if(this.destroyed)return;
    this.draftLayer.clearLayers();
    for(const p of points)this.L.circleMarker(ll(p,points[0]?.longitude),{radius:7,color:"#ffffff",fillColor:"#f5cd79",fillOpacity:1}).addTo(this.draftLayer);
    if(points.length>1)(polygon&&points.length>=3?this.L.polygon(points.map(p=>ll(p,points[0]?.longitude)),{color:"#ffffff",dashArray:"4 6",fillOpacity:.08}):this.L.polyline(points.map(p=>ll(p,points[0]?.longitude)),{color:"#ffffff",dashArray:"4 6"})).addTo(this.draftLayer);
  }
  preview(result) {
    if(this.destroyed)return;
    this.previewLayer.clearLayers();this.previewResult=result;
    this.container.dataset.previewPoints=String(result?.waypoints?.length||0);
    if(!result)return;
    for(const vertices of result.modeledSwaths||[])this.L.polygon(vertices.map(p=>ll(p)),{color:'#6ce4b9',weight:1,fillOpacity:.08}).bindTooltip('Modeled nadir flat-ground swath; source validity, relief and continuous acquisition are unverified.').addTo(this.previewLayer);
    if(result.terrainPlanningFootprint)this.L.polygon(result.terrainPlanningFootprint.boundary.map(p=>ll(p)),{color:'#efb96b',weight:2,fillOpacity:.15}).bindTooltip('Relative-AGL terrain-aware centre footprint; no simulator vertical-datum correction.').addTo(this.previewLayer);
    const points=result.waypoints||[];const d=result.definition;
    if(result.segments?.length){for(const segment of result.segments)this.L.polyline(points.slice(segment.start,segment.end+1).map(p=>ll(p,points[0]?.longitude)),{color:segment.role==="scan"?"#50dcc7":"#f0ad70",dashArray:segment.role==="scan"?null:"6 6",weight:3}).bindTooltip(segment.role==="scan"?"In-area scan leg":"Transition: may leave survey polygon; turning feasibility unvalidated").addTo(this.previewLayer);}
    else this.L.polyline(points.map(p=>ll(p,points[0]?.longitude)),{color:"#ffffff",dashArray:"3 7",weight:3}).addTo(this.previewLayer);
    if(d&&d.fitMode!=="polygon"&&["lawnmower","creepingLine"].includes(d.kind)&&points.length>=4){
      const bearing=d.headingDegrees*Math.PI/180,distance=Math.min(d.lengthMetres/3,20000),R=6371008.8;
      const start=ll(d.center),end=[start[0]+distance*Math.cos(bearing)/R*180/Math.PI,start[1]+distance*Math.sin(bearing)/(R*Math.max(.02,Math.cos(start[0]*Math.PI/180)))*180/Math.PI];
      this.L.polyline([start,end],{color:"#72b8f1",weight:4}).bindTooltip(`Reference heading ${d.headingDegrees}° true (approximate display arrow)`).addTo(this.previewLayer);
      const arrow=document.createElement("span");arrow.textContent="➤";arrow.style.display="block";arrow.style.transform=`rotate(${d.headingDegrees-90}deg)`;
      this.L.marker(end,{interactive:false,icon:this.L.divIcon({className:"pattern-arrow",html:arrow,iconSize:[20,20]})}).addTo(this.previewLayer);
      this.L.polyline([ll(points[1]),ll(points[2])],{color:"#eb9bbd",weight:5}).bindTooltip("Step direction between long scan legs").addTo(this.previewLayer);
      this.L.rectangle(this.L.latLngBounds(points.map(p=>ll(p,points[0]?.longitude))),{color:"#8aa1b0",weight:1,dashArray:"2 8",fill:false}).bindTooltip("Pattern extent; not guaranteed sensor coverage or polygon clipping").addTo(this.previewLayer);
    }
    if(d?.kind==="orbit")this.L.circle(ll(d.center),{radius:d.radiusMetres,color:"#ffffff",fill:false}).addTo(this.previewLayer);
    for(const [p,name] of [[points[0],"Preview entry"],[points.at(-1),"Preview exit"]])if(p)this.L.circleMarker(ll(p),{radius:6,color:name.includes("entry")?"#50dcc7":"#eb9bbd"}).bindTooltip(name).addTo(this.previewLayer);
  }
  center(point) {
    if (!this.destroyed && valid(point))
      this.map.setView(ll(point), Math.max(9, this.map.getZoom()));
  }
  fit(points = this.geometry()) {
    if(this.destroyed)return;
    const p = points.filter(valid);
    if (!p.length) {
      this.map.setView([38, -97], 4);
      return;
    }
    const base = p[0].longitude;
    this.map.fitBounds(
      p.map((v) => ll(v, base)),
      { padding: [28, 28], maxZoom: this.options.fitMaxZoom || 12 },
    );
  }
  showRouteValues(){
    if(this.destroyed||!this.routeValues.open)return;const waypoints=this.data.plan?.waypoints||[];this.routeRows.replaceChildren();
    geographicTable(this.routeRows,'Planned waypoint coordinates',['Waypoint','Latitude (degrees north)','Longitude (degrees east)'],waypoints.map((point,index)=>[point.name||`Waypoint ${index+1}`,point.latitude,point.longitude]),{onSelect:index=>{this.center(waypoints[index]);this.options.onWaypointSelect?.(index);}});
  }
  draw() {
    if (this.destroyed || !this.map) return;
    const L = this.L,
      d = this.data,
      plan = d.plan || {},
      base = this.geometry()[0]?.longitude ?? 0;
    const staticKey = JSON.stringify([
      plan.waypoints,
      plan.surveyAreas,
      d.markers,
      d.samples,
      d.footprints,
      plan.patterns,
    ]);
    const staticChanged = staticKey !== this.staticKey;
    this.staticKey = staticKey;
    if(staticChanged){this.routeValues.hidden=!plan.waypoints?.length;if(this.routeValues.open)this.showRouteValues();}
    if (staticChanged)
      this.route.setLatLngs(
        (plan.waypoints || []).filter(valid).map((p) => ll(p, base)),
      );
    this.track.clearLayers();
    // Missing recording intervals remain visibly disconnected. Original telemetry is unchanged.
    let segment = [],
      previous = null;
    const flush = () => {
      if (segment.length)
        L.polyline(segment, { color: "#50dcc7", weight: 3 }).addTo(this.track);
      segment = [];
    };
    for (const p of (d.track || []).filter(valid)) {
      if (
        p.breakBefore === true ||
        (p.breakBefore === undefined &&
          previous &&
          p.timestampUtc &&
          previous.timestampUtc &&
          new Date(p.timestampUtc) - new Date(previous.timestampUtc) >
            (d.maximumGapSeconds || 5) * 1000)
      )
        flush();
      segment.push(ll(p, base));
      previous = p;
    }
    flush();
    if (staticChanged) {
      this.points.clearLayers();this.markers.clearLayers();this.samples.clearLayers();this.footprints.clearLayers();this.patterns.clearLayers();
      for(const d of plan.patterns||[])if(d.kind==="orbit"&&valid(d.center))L.circle(ll(d.center),{radius:d.radiusMetres,color:"#f5cd79",fill:false,weight:2}).bindTooltip(label(`Orbit: ${planningDistance(d.radiusMetres)} · ${d.clockwise?"clockwise":"counterclockwise"} · ${d.orbits} revolution(s); exports use 5° navigation points`)).addTo(this.patterns);
      for (const [i, p] of (plan.waypoints || []).entries()) {
        if (!valid(p)) continue;
        if(p.patternId&&(plan.patterns||[]).some(d=>d.id===p.patternId&&d.kind==="orbit")&&i>0&&i<plan.waypoints.length-1&&plan.waypoints[i-1]?.patternId===p.patternId&&plan.waypoints[i+1]?.patternId===p.patternId)continue;
        const m = L.marker(ll(p, base), {
          draggable: !!this.options.onWaypointsChange,
          keyboard:false, // Equivalent, non-overlapping selection is in the route table.
          title:p.name||`Waypoint ${i+1}`,
          alt:p.name||`Waypoint ${i+1}`,
          icon: L.divIcon({
            className: "waypoint-pin",
            html: String(i + 1),
            iconSize: [28, 28],
          }),
        }).addTo(this.points);
        m.bindTooltip(label(p.name || `Waypoint ${i + 1}`));
        m.on("click",e=>{this.L.DomEvent.stopPropagation(e);this.options.onWaypointSelect?.(i);});
        m.on("dragend", () => {
          const q = m.getLatLng();
          const points = structuredClone(plan.waypoints);
          points[i] = { ...p, latitude: q.lat, longitude: longitude(q.lng) };
          this.options.onWaypointsChange(points);
        });
      }
      for (const p of (d.markers || []).filter(valid))
        L.circleMarker(ll(p, base), { radius: 6, color: "#eb9bbd" })
          .bindTooltip(
            label(`${p.category || "Marker"} · ${p.timestampUtc || ""}`),
          )
          .addTo(this.markers);
      for (const p of (d.samples || []).filter(valid))
        L.circleMarker(ll(p, base), { radius: 2, color: "#a4bdf3" }).bindTooltip(label(`${p.instrumentId||"Scientific"} successful sample · ${p.timestampUtc||""}`)).addTo(
          this.samples,
        );
      for(const f of d.footprints||[])if(f.vertices?.length>=3&&f.vertices.every(valid))L.polygon(f.vertices.map(p=>ll(p,base)),{color:"#a4bdf3",weight:1,fillOpacity:.07}).bindTooltip(label(f.label||"Derived scientific sampling footprint")).addTo(this.footprints);
    }
    if (valid(d.aircraft)) {
      if (!this.aircraftMarker)
        this.aircraftMarker = L.marker(ll(d.aircraft, base), {
          icon: L.divIcon({
            className: "aircraft-pin",
            html: "▲",
            iconSize: [24, 24],
          }),
        })
          .bindTooltip(this.options.aircraftLabel||"Live aircraft")
          .addTo(this.aircraft);
      else this.aircraftMarker.setLatLng(ll(d.aircraft, base));
    } else if (this.aircraftMarker) {
      this.aircraft.removeLayer(this.aircraftMarker);
      this.aircraftMarker = null;
    }
    if (staticChanged && !this.drawing) {
      this.areas.clearLayers();
      for (const a of plan.surveyAreas || []) {
        const poly = L.polygon(
          a.vertices.map((p) => ll(p, base)),
          { color: "#bf9af0", fillOpacity: 0.12 },
        );
        poly.areaName = a.name;
        poly.bindTooltip(label(a.name));
        this.areas.addLayer(poly);
      }
    }
    this.coordinate.textContent =
      (this.layerError ? this.layerError + " " : "") +
      (this.onlineTiles.length
        ? "Online basemap with bundled geographic reference"
        : "Offline Natural Earth geography") +
      " · Gold: planned · Turquoise: recorded · Pink: markers · Wheel/pinch to zoom. " +
      (this.initialized ? "" : "No location selected: regional default USA. ") +
      (this.onPoint
        ? "Choose Add Waypoint or Draw Survey Area to edit; pan normally otherwise. "
        : "") +
      "Mercator display ends at ±85.0511°; WGS84 coordinates and exports retain polar positions. Offline geography is coarse. Airport search: OurAirports (public domain), a non-authoritative snapshot; verify current operational information.";
  }
}
