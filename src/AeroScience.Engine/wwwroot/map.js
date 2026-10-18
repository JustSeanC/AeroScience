import "./geographic-view.js";
import { layerSelector } from "./basemaps.js";
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
    ["countries", "places", "airports"].map(async (name) => {
      const response = await fetch(`/geography/${name}.geojson`);
      if (!response.ok) throw Error("Bundled geography unavailable");
      return response.json();
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
    this.locations = [];
    this.data = {};
    this.onPoint = onPoint;
    this.options = options;
    const tools = document.createElement("div");
    tools.className = "map-tools";
    this.container = document.createElement("div");
    this.container.className = "geographic-map";
    this.container.setAttribute(
      "aria-label",
      "Geographic mission map. Pan by dragging; wheel or pinch to zoom.",
    );
    this.coordinate = document.createElement("p");
    this.coordinate.className = "footnote";
    parent.append(tools, this.container, this.coordinate);
    const L = window.L;
    this.L = L;
    this.map = L.map(this.container, {
      worldCopyJump: true,
      preferCanvas: true,
      zoomControl: true,
    }).setView([38, -97], 4);
    this.map.attributionControl.addAttribution(
      "Leaflet · Natural Earth (public domain) · Geographic reference, not navigation",
    );
    this.route = L.polyline([], { color: "#f5cd79", dashArray: "8 6" }).addTo(
      this.map,
    );
    this.track = L.layerGroup().addTo(this.map);
    this.points = L.layerGroup().addTo(this.map);
    this.areas = new L.FeatureGroup().addTo(this.map);
    const action = (name, fn) => {
      const b = document.createElement("button");
      b.type = "button";
      b.className = "secondary";
      b.textContent = name;
      b.onclick = fn;
      tools.append(b);
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
    results.setAttribute("aria-label", "Search results");
    results.hidden = true;
    tools.append(results);
    const find = async () => {
      await this.ready;
      const q = search.value.trim().toLowerCase();
      const parts = q.split(",").map(Number);
      if (
        q.includes(",") &&
        parts.length === 2 &&
        parts.every(Number.isFinite) &&
        Math.abs(parts[0]) <= 90 &&
        Math.abs(parts[1]) <= 180
      ) {
        this.map.setView(ll({ latitude: parts[0], longitude: parts[1] }), 9);
        return;
      }
      const matches = this.locations
        .filter((f) =>
          Object.values(f.properties).join(" ").toLowerCase().includes(q),
        )
        .slice(0, 30);
      results.replaceChildren();
      matches.forEach((f, i) => {
        const o = document.createElement("option");
        o.value = i;
        o.textContent = Object.values(f.properties).filter(Boolean).join(" · ");
        results.append(o);
      });
      results.hidden = !matches.length;
      results.onchange = () => {
        const f = matches[Number(results.value)];
        this.map.setView(
          [f.geometry.coordinates[1], f.geometry.coordinates[0]],
          9,
        );
      };
      if (matches.length) results.onchange();
      else
        this.coordinate.textContent =
          "No bundled match. Enter explicit latitude, longitude; the offline airport gazetteer is not exhaustive.";
    };
    action("Find", find);
    search.onkeydown = (e) => {
      if (e.key === "Enter") {
        e.preventDefault();
        find();
      }
    };
    this.onlineTiles = [];
    this.applyBasemap = (client) => {
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
          if (!this.onlineTiles.includes(tile)) return;
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
      if (client.layer.startsWith("satellite") && this.onlineTiles.length) {
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
    this.layerCleanup = layerSelector(tools, this.applyBasemap);
    this.map.on("click", (e) => {
      if (onPoint && !this.drawing)
        onPoint({ latitude: e.latlng.lat, longitude: longitude(e.latlng.lng) });
    });
    if (options.onAreasChange) {
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
      this.map.invalidateSize({ pan: false });
      this.visibilityChanged();
    });
    this.observer.observe(this.container);
    this.ready = loadGeography()
      .then(([countries, places, airports]) => {
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
          if (this.map.getZoom() >= 5) this.context.addTo(this.map);
          else this.map.removeLayer(this.context);
        };
        this.map.on("zoomend", context);
        context();
        this.resolveDeparture();
      })
      .catch(() => {
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
    this.layerCleanup?.();
    this.observer?.disconnect();
    document.removeEventListener("visibilitychange", this.visibilityChanged);
    this.map.remove();
  }
  areaChanges() {
    this.options.onAreasChange(
      this.areas.getLayers().map((l) => ({
        name: l.areaName || "Survey area",
        vertices: l
          .getLatLngs()[0]
          .map((p) => ({ latitude: p.lat, longitude: longitude(p.lng) })),
      })),
    );
  }
  set(data) {
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
    if (this.initialized || !this.locations?.length) return;
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
  center(point) {
    if (valid(point))
      this.map.setView(ll(point), Math.max(9, this.map.getZoom()));
  }
  fit(points = this.geometry()) {
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
  draw() {
    if (!this.map) return;
    const L = this.L,
      d = this.data,
      plan = d.plan || {},
      base = this.geometry()[0]?.longitude ?? 0;
    const staticKey = JSON.stringify([
      plan.waypoints,
      plan.surveyAreas,
      d.markers,
      d.samples,
    ]);
    const staticChanged = staticKey !== this.staticKey;
    this.staticKey = staticKey;
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
      this.points.clearLayers();
      for (const [i, p] of (plan.waypoints || []).entries()) {
        if (!valid(p)) continue;
        const m = L.marker(ll(p, base), {
          draggable: !!this.options.onWaypointsChange,
          icon: L.divIcon({
            className: "waypoint-pin",
            html: String(i + 1),
            iconSize: [28, 28],
          }),
        }).addTo(this.points);
        m.bindTooltip(label(p.name || `Waypoint ${i + 1}`));
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
          .addTo(this.points);
      for (const p of (d.samples || []).filter(valid))
        L.circleMarker(ll(p, base), { radius: 2, color: "#a4bdf3" }).addTo(
          this.points,
        );
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
          .bindTooltip("Live aircraft")
          .addTo(this.map);
      else this.aircraftMarker.setLatLng(ll(d.aircraft, base));
    } else if (this.aircraftMarker) {
      this.map.removeLayer(this.aircraftMarker);
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
        ? "Click to add; numbered waypoints can be dragged. "
        : "") +
      "Mercator display ends at ±85.0511°; WGS84 coordinates and exports retain polar positions. Offline geography is coarse and airports are not exhaustive.";
  }
}
