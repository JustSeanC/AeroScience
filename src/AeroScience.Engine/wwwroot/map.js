const valid = (p) =>
  Number.isFinite(p?.latitude) && Number.isFinite(p?.longitude);
const longitude = (x) => ((x + 540) % 360) - 180;
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
export class ResearchMap {
  constructor(parent, onPoint, options = {}) {
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
    const online = document.createElement("label"),
      check = document.createElement("input");
    check.type = "checkbox";
    online.append(check, " Optional online detail tiles");
    tools.append(online);
    check.onchange = () => {
      if (check.checked) {
        if (
          !confirm(
            "Online map detail sends the visible map area and your IP address to OpenStreetMap tile servers and needs internet. Attribution and normal browser caching apply. No bulk download or offline tile cache is provided. Enable for this map?",
          )
        ) {
          check.checked = false;
          return;
        }
        this.tiles = L.tileLayer(
          "https://tile.openstreetmap.org/{z}/{x}/{y}.png",
          {
            maxZoom: 19,
            attribution: "© OpenStreetMap contributors",
            crossOrigin: true,
          },
        ).addTo(this.map);
        this.tiles.on("tileerror", () => {
          this.coordinate.textContent =
            "Online tiles unavailable. Bundled offline geography remains available.";
        });
      } else if (this.tiles) this.map.removeLayer(this.tiles);
    };
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
    this.observer = new ResizeObserver(() =>
      this.map.invalidateSize({ pan: false }),
    );
    this.observer.observe(this.container);
    this.ready = loadGeography()
      .then(([countries, places, airports]) => {
        L.geoJSON(countries, {
          style: {
            color: "#567080",
            weight: 1,
            fillColor: "#193039",
            fillOpacity: 0.65,
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
    this.data = data;
    this.draw();
    const hasPlan =
      (data.plan?.waypoints?.length || data.plan?.surveyAreas?.length || 0) > 0;
    if (!this.initialized && hasPlan) {
      this.fit();
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
    }
  }
  center(point) {
    if (valid(point))
      this.map.setView(ll(point), Math.max(9, this.map.getZoom()));
  }
  fit() {
    const p = this.geometry();
    if (!p.length) {
      this.map.setView([38, -97], 4);
      return;
    }
    const base = p[0].longitude;
    this.map.fitBounds(
      p.map((v) => ll(v, base)),
      { padding: [28, 28], maxZoom: 12 },
    );
  }
  draw() {
    if (!this.map) return;
    const L = this.L,
      d = this.data,
      plan = d.plan || {},
      base = this.geometry()[0]?.longitude ?? 0;
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
        previous &&
        p.timestampUtc &&
        previous.timestampUtc &&
        new Date(p.timestampUtc) - new Date(previous.timestampUtc) >
          (d.maximumGapSeconds || 5) * 1000
      )
        flush();
      segment.push(ll(p, base));
      previous = p;
    }
    flush();
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
    if (valid(d.aircraft))
      L.marker(ll(d.aircraft, base), {
        icon: L.divIcon({
          className: "aircraft-pin",
          html: "▲",
          iconSize: [24, 24],
        }),
      })
        .bindTooltip("Live aircraft")
        .addTo(this.points);
    if (!this.drawing) {
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
      "Offline Natural Earth geography · Gold: planned · Turquoise: recorded · Pink: markers · Wheel/pinch to zoom. " +
      (this.initialized ? "" : "No location selected: regional default USA. ") +
      (this.onPoint
        ? "Click to add; numbered waypoints can be dragged. "
        : "") +
      "Mercator display ends at ±85.0511°; WGS84 coordinates and exports retain polar positions. Offline geography is coarse and airports are not exhaustive.";
  }
}
