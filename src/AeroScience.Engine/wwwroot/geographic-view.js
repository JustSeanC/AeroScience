/* Shared WGS84 display helpers and a conservative offline canvas renderer for Coherent.
 * The simulator owns the outer panel. This component only handles its own map surface. */
(function (root) {
  "use strict";
  const wrap = (x) => ((((x + 540) % 360) + 360) % 360) - 180;
  const project = (p, base) => {
    const lat =
      (Math.max(-85.0511, Math.min(85.0511, p.latitude)) * Math.PI) / 180;
    return [
      base + wrap(p.longitude - base),
      (-Math.log(Math.tan(Math.PI / 4 + lat / 2)) * 180) / Math.PI,
    ];
  };
  root.AeroScienceGeo = { wrap, project };
  root.AeroScienceMovingMap = class {
    constructor(container, info) {
      this.container = container;
      this.info = info;
      this.center = [0, 0];
      this.zoom = 1;
      this.follow = true;
      this.items = [];
      this.track = [];
      this.markers = [];
      this.aircraft = null;
      this.selected = 0;
      this.routeRevision = null;
      this.fitted = false;
      this.base = document.createElement("canvas");
      this.live = document.createElement("canvas");
      if (!this.base.getContext("2d") || !this.live.getContext("2d"))
        throw new Error("Canvas rendering is unavailable.");
      container.appendChild(this.base);
      container.appendChild(this.live);
      this.base.setAttribute("aria-hidden", "true");
      this.live.setAttribute(
        "aria-label",
        "Offline north-up mission map. Drag to pan, wheel to zoom.",
      );
      this.live.tabIndex = 0;
      this.drag = null;
      this.live.onmousedown = (e) => {
        if (e.button !== 0) return;
        this.follow = false;
        this.drag = [e.clientX, e.clientY, ...this.center];
        e.preventDefault();
        e.stopPropagation();
      };
      this.move = (e) => {
        if (!this.drag) return;
        const scale = this.scale();
        this.center = [
          this.drag[2] - (e.clientX - this.drag[0]) / scale,
          this.drag[3] - (e.clientY - this.drag[1]) / scale,
        ];
        this.draw();
      };
      this.up = () => {
        this.drag = null;
      };
      window.addEventListener("mousemove", this.move);
      window.addEventListener("mouseup", this.up);
      this.live.onwheel = (e) => {
        e.preventDefault();
        e.stopPropagation();
        this.zoomBy(e.deltaY < 0 ? 1.4 : 1 / 1.4);
      };
      this.live.onkeydown = (e) => {
        const d = 40 / this.scale();
        if (e.key === "+" || e.key === "=") this.zoomBy(1.4);
        else if (e.key === "-") this.zoomBy(1 / 1.4);
        else if (
          ["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"].indexOf(e.key) >=
          0
        ) {
          this.follow = false;
          this.center[0] +=
            e.key === "ArrowLeft" ? -d : e.key === "ArrowRight" ? d : 0;
          this.center[1] +=
            e.key === "ArrowUp" ? -d : e.key === "ArrowDown" ? d : 0;
          this.draw();
        } else return;
        e.preventDefault();
      };
      if (typeof ResizeObserver === "function") {
        this.observer = new ResizeObserver(() => this.resize());
        this.observer.observe(container);
      }
      this.resize();
    }
    scale() {
      return (Math.max(1, this.width || 320) / 360) * this.zoom;
    }
    pixel(p) {
      const q = project(p, this.center[0]),
        s = this.scale();
      return [
        this.width / 2 + (q[0] - this.center[0]) * s,
        this.height / 2 + (q[1] - this.center[1]) * s,
      ];
    }
    resize() {
      const rect = this.container.getBoundingClientRect(),
        w = Math.max(200, Math.round(rect.width)),
        h = Math.max(220, Math.round(rect.height));
      if (w === this.width && h === this.height) return;
      this.width = w;
      this.height = h;
      for (const c of [this.base, this.live]) {
        c.width = w;
        c.height = h;
      }
      this.draw();
    }
    zoomBy(f) {
      this.zoom = Math.max(0.5, Math.min(65536, this.zoom * f));
      this.draw();
    }
    fit() {
      const points = this.items.length ? this.items : this.track;
      if (!points.length) {
        if (this.aircraft)
          this.center = project(this.aircraft, this.aircraft.longitude);
        this.zoom = 16;
        this.draw();
        return;
      }
      const base = points[0].longitude,
        xy = points.map((p) => project(p, base)),
        xs = xy.map((p) => p[0]),
        ys = xy.map((p) => p[1]),
        minX = Math.min(...xs),
        maxX = Math.max(...xs),
        minY = Math.min(...ys),
        maxY = Math.max(...ys);
      this.center = [(minX + maxX) / 2, (minY + maxY) / 2];
      this.zoom = Math.max(
        0.5,
        Math.min(
          65536,
          (Math.min(
            (this.width - 50) / Math.max(0.002, maxX - minX),
            (this.height - 50) / Math.max(0.002, maxY - minY),
          ) *
            360) /
            this.width,
        ),
      );
      this.follow = false;
      this.fitted = true;
      this.draw();
    }
    route(revision, items) {
      if (this.routeRevision !== revision) {
        this.track = [];
        this.selected = 0;
        this.fitted = false;
      }
      this.routeRevision = revision;
      this.items = items;
      this.fit();
    }
    update(state) {
      this.aircraft = state.aircraft;
      this.markers = state.markers || [];
      for (const point of state.track || []) {
        if (
          !this.track.length ||
          point.sequence > this.track[this.track.length - 1].sequence
        )
          this.track.push(point);
      }
      if (this.track.length > 512) this.track = this.track.slice(-512);
      const pixel = this.aircraft ? this.pixel(this.aircraft) : null;
      const baseChanged =
        this.follow &&
        this.aircraft &&
        (!this.fitted ||
          Math.abs(pixel[0] - this.width / 2) > this.width * 0.15 ||
          Math.abs(pixel[1] - this.height / 2) > this.height * 0.15);
      if (baseChanged) {
        this.center = project(this.aircraft, this.aircraft.longitude);
        if (!this.fitted) {
          this.zoom = 1024;
          this.fitted = true;
        }
        this.draw();
      } else this.drawLive();
      this.state = state;
      this.describe();
      this.resize();
    }
    describe() {
      const route = this.items.filter((p) => p.kind === "waypoint"),
        p = route[this.selected],
        a = this.aircraft;
      let detail = p
        ? `${p.index + 1}. ${p.name}  /  ${p.latitude.toFixed(7)}, ${p.longitude.toFixed(7)}  /  ${p.targetAltitudeFeet == null ? "Altitude unspecified" : p.targetAltitudeFeet + " ft MSL"}  /  ${p.instructions || ""}`
        : "No planned waypoint selected.";
      if (
        p &&
        a &&
        this.state &&
        this.state.selectedWaypoint === this.selected &&
        Number.isFinite(this.state.distanceMetres)
      )
        detail += `  /  WGS84 ${(this.state.distanceMetres / 1852).toFixed(2)} NM / ${this.state.bearingDegrees.toFixed(1)} deg true`;
      if(a)detail+=` / Ground track ${Number.isFinite(a.groundTrackDegrees)?a.groundTrackDegrees.toFixed(1)+" deg true":"unavailable"} / GS ${Number.isFinite(a.groundspeedKnots)?a.groundspeedKnots.toFixed(0)+" kt":"unavailable"}`;
      this.info.textContent =
        detail +
        (a ? "" : "  /  Live position unavailable/stale.") +
        " Display labels and instructions may be abbreviated; full values remain in the companion plan.";
    }
    path(ctx, points, color, width) {
      if (!points.length) return;
      ctx.beginPath();
      let previous = null;
      for (const p of points) {
        const q = this.pixel(p);
        if (!previous || Math.abs(q[0] - previous[0]) > this.width * 2)
          ctx.moveTo(q[0], q[1]);
        else ctx.lineTo(q[0], q[1]);
        previous = q;
      }
      ctx.strokeStyle = color;
      ctx.lineWidth = width;
      ctx.stroke();
    }
    draw() {
      if (!this.width) return;
      const c = this.base.getContext("2d");
      c.fillStyle = "#0b1a27";
      c.fillRect(0, 0, this.width, this.height);
      c.strokeStyle = "#243746";
      c.lineWidth = 1;
      for (let x = 0; x < this.width; x += 80) {
        c.beginPath();
        c.moveTo(x, 0);
        c.lineTo(x, this.height);
        c.stroke();
      }
      for (let y = 0; y < this.height; y += 80) {
        c.beginPath();
        c.moveTo(0, y);
        c.lineTo(this.width, y);
        c.stroke();
      }
      const countries = root.AeroScienceCountries;
      for (const f of (countries && countries.features) || []) {
        const polys =
          f.geometry.type === "Polygon"
            ? [f.geometry.coordinates]
            : f.geometry.coordinates;
        for (const poly of polys)
          for (const ring of poly)
            this.path(
              c,
              ring.map((p) => ({ latitude: p[1], longitude: p[0] })),
              "#577080",
              1,
            );
      }
      if (this.zoom > 16) {
        c.font = "11px sans-serif";
        let labels = 0;
        const context = [
          ...((root.AeroSciencePlaces && root.AeroSciencePlaces.features) ||
            []),
          ...((root.AeroScienceAirports && root.AeroScienceAirports.features) ||
            []),
        ];
        for (const f of context) {
          const q = this.pixel({
            longitude: f.geometry.coordinates[0],
            latitude: f.geometry.coordinates[1],
          });
          if (
            q[0] < 0 ||
            q[0] > this.width ||
            q[1] < 25 ||
            q[1] > this.height - 45
          )
            continue;
          const p = f.properties,
            airport = !!(p.gps_code || p.iata_code);
          c.fillStyle = airport ? "#acc7ee" : "#9caeb8";
          c.fillRect(q[0] - 2, q[1] - 2, 4, 4);
          if (labels++ < Math.max(8,Math.floor(this.width/70)))
            c.fillText(
              (p.gps_code || p.NAME || p.name || "").slice(0, 28),
              q[0] + 5,
              q[1] + 3,
            );
        }
      }
      const waypoints = this.items.filter((p) => p.kind === "waypoint");
      this.path(c, waypoints, "#f5cd79", 2);
      const areas = {};
      for (const p of this.items)
        if (p.kind === "survey")
          (areas[p.area] || (areas[p.area] = [])).push(p);
      for (const a of Object.keys(areas).map((key) => areas[key]))
        this.path(c, a.concat(a[0]), "#bf9af0", 2);
      c.font = "12px sans-serif";
      let lastLabel=null;
      for (const p of waypoints) {
        const q = this.pixel(p);
        if (q[0] < 0 || q[0] > this.width || q[1] < 0 || q[1] > this.height)
          continue;
        c.fillStyle = p.index === this.selected ? "#ffffff" : "#f5cd79";
        c.beginPath();
        c.arc(q[0], q[1], p.index === this.selected ? 5 : 3, 0, Math.PI * 2);
        c.fill();
        const important=p.index===this.selected||p.index===this.state?.nextWaypoint||p.index===0||p.index===waypoints.length-1;
        if(important||!lastLabel||Math.hypot(q[0]-lastLabel[0],q[1]-lastLabel[1])>65){c.fillText(String(p.index+1),q[0]+7,q[1]-7);lastLabel=q;}
      }
      c.fillStyle = "#c8d9e3";
      c.fillText("N ^ / Offline Natural Earth / Geographic reference", 8, 17);
      const lat =
          ((2 * Math.atan(Math.exp((-this.center[1] * Math.PI) / 180)) -
            Math.PI / 2) *
            180) /
          Math.PI,
        nm =
          ((((80 / this.scale()) * Math.PI) / 180) *
            6371008.8 *
            Math.max(0.01, Math.cos((lat * Math.PI) / 180))) /
          1852;
      c.fillRect(10, this.height - 22, 80, 2);
      c.fillText(
        "~" + nm.toFixed(nm < 1 ? 2 : 1) + " NM",
        10,
        this.height - 28,
      );
      this.drawLive();
    }
    drawLive() {
      if (!this.width) return;
      const c = this.live.getContext("2d");
      c.clearRect(0, 0, this.width, this.height);
      let segment = [],
        last = null;
      for (const p of this.track) {
        if (
          p.breakBefore === true ||
          (p.breakBefore === undefined &&
            last &&
            Date.parse(p.timestampUtc) - Date.parse(last.timestampUtc) > 5000)
        ) {
          this.path(c, segment, "#50dcc7", 2);
          segment = [];
        }
        segment.push(p);
        last = p;
      }
      this.path(c, segment, "#50dcc7", 2);
      for (const p of this.markers) {
        if (!Number.isFinite(p.latitude) || !Number.isFinite(p.longitude))
          continue;
        const q = this.pixel(p);
        c.fillStyle = "#eb9bbd";
        c.fillRect(q[0] - 3, q[1] - 3, 6, 6);
      }
      if (this.aircraft) {
        const q = this.pixel(this.aircraft);
        c.save();
        c.translate(...q);
        c.rotate(((this.aircraft.groundTrackDegrees??this.aircraft.headingDegrees) * Math.PI) / 180);
        c.beginPath();
        c.moveTo(0, -11);
        c.lineTo(7, 8);
        c.lineTo(0, 4);
        c.lineTo(-7, 8);
        c.closePath();
        c.fillStyle = "#50dcc7";
        c.fill();
        c.restore();
      }
    }
    destroy() {
      if (this.observer) this.observer.disconnect();
      this.base.remove();
      this.live.remove();
      window.removeEventListener("mousemove", this.move);
      window.removeEventListener("mouseup", this.up);
    }
  };
})(window);
