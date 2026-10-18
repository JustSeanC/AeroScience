import { ResearchMap } from "./map.js";
import { displayReading, onUnitsChanged } from "./science-display.js";
const e = (tag, text) => {
  const node = document.createElement(tag);
  if (text !== undefined) node.textContent = text;
  return node;
};
const action = (text, run) => {
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
      b.disabled = false;
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
export async function sourceSettings(parent, api) {
  const section = e("section");
  section.className = "panel research-panel";
  section.append(
    e("h2", "Scientific Data Sources"),
    e(
      "p",
      "Import a licensed, real GeoTIFF/ENVI source collection using its metadata manifest. Copies remain local in your Science Library. Source coverage and observation dates vary; basemap imagery is never a scientific sensor source.",
    ),
  );
  parent.append(section);
  section.append(
    action("Import scientific source collection…", () =>
      api("/api/desktop/science-import", {}),
    ),
  );
  link(
    section,
    "Remote-sensing quick start",
    "/help/remote-sensing/quick-start.md",
  );
  link(
    section,
    "Supported files and import walkthrough",
    "/help/remote-sensing/sources.md",
  );
  const lat = e("input"),
    lon = e("input");
  lat.type = lon.type = "number";
  lat.step = lon.step = "any";
  lat.placeholder = "Latitude";
  lon.placeholder = "Longitude";
  lat.setAttribute("aria-label", "Source availability latitude");
  lon.setAttribute("aria-label", "Source availability longitude");
  const results = e("div");
  section.append(lat, lon, action("Check source coverage", load), results);
  async function load() {
    results.replaceChildren();
    const query =
      lat.value !== "" && lon.value !== ""
        ? `?latitude=${encodeURIComponent(lat.value)}&longitude=${encodeURIComponent(lon.value)}`
        : "";
    const sources = await api("/api/science/sources" + query);
    if (!sources.length)
      results.append(
        e(
          "p",
          "No scientific source collections imported. Scientific data unavailable for this location or acquisition. Follow the import guide; no reference datasets are bundled.",
        ),
      );
    for (const entry of sources) {
      const s = entry.source,
        m = s.manifest,
        card = e("article");
      card.className = "panel";
      card.append(
        e("h3", m.name),
        e(
          "p",
          `${m.kind} · ${m.provider} · ${entry.filesAvailable ? "Files available" : "Unavailable"}${entry.coversPoint === null ? "" : entry.coversPoint ? " · Covers selected point" : " · Outside coverage"}`,
        ),
        e(
          "p",
          `Observed: ${m.observationUtc || "Unknown, not live"} · Published: ${m.publicationUtc || "Not supplied"} · Imported: ${s.importedUtc}`,
        ),
      );
      card.append(
        e(
          "p",
          `${m.bands.length} bands · ${number(s.files.reduce((n, f) => n + f.bytes, 0) / 1048576)} MiB · ${m.license} · Vertical datum: ${m.verticalDatum}`,
        ),
      );
      if (entry.error) card.append(e("p", entry.error));
      const details = e("details");
      details.append(
        e("summary", "Bands, grids and source provenance"),
        e("p", m.citation),
      );
      for (const b of m.bands) {
        const g = s.grids[b.id];
        details.append(
          e(
            "p",
            `${b.id}: ${b.unit}${b.wavelengthNm ? ` · ${b.wavelengthNm} nm` : ""}${b.badBand ? " · Bad band excluded" : ""} · ${g.crs}, ${g.width}×${g.height}, native pixels ${Math.abs(g.pixelWidth)}×${Math.abs(g.pixelHeight)} ${g.crs === "EPSG:4326" ? "degrees" : "m"}`,
          ),
        );
      }
      details.append(
        e(
          "p",
          m.notes ||
            "Original source checksums and metadata are retained with every product.",
        ),
      );
      card.append(details);
      results.append(card);
    }
  }
  section.append(action("Refresh local sources", load));
  await load();
}
export async function remoteProducts(
  parent,
  api,
  missionId = null,
  instrumentId = null,
) {
  const section = e("section");
  section.className = "panel research-panel remote-products";
  section.append(
    e("h2", "Remote-Sensing Products"),
    e(
      "p",
      "Historical geographic observations sampled along a simulated flight footprint. Original observation, flight-packet and processing times remain distinct.",
    ),
  );
  parent.append(section);
  let offset = 0;
  const list = e("div"),
    viewer = e("div"),
    filter = e("select");
  filter.setAttribute("aria-label", "Product instrument");
  for (const [id, name] of [
    ["", "All remote instruments"],
    ...Object.entries(names),
  ]) {
    const option = e("option", name);
    option.value = id;
    filter.append(option);
  }
  filter.value = instrumentId || "";
  filter.onchange = () => { offset=0;load(); };
  section.append(filter, action("Refresh products", load), list, viewer);
  section.append(
    action("Previous product page", async () => {
      offset = Math.max(0, offset - 30);
      await load();
    }),
    action("Next product page", async () => {
      offset += 30;
      await load();
    }),
  );
  async function load() {
    const products = await api(
      `/api/science/products?limit=30&offset=${offset}${missionId ? "&missionId=" + encodeURIComponent(missionId) : ""}${filter.value?"&instrumentId="+encodeURIComponent(filter.value):""}`,
    );
    list.replaceChildren();
    for (const p of products.filter(
      (p) => !filter.value || filter.value === p.instrumentId,
    )) {
      const row = e("article");
      row.className = "plan-row";
      row.append(
        e("strong", names[p.instrumentId] || p.instrumentId),
        e(
          "span",
          `Flight: ${p.flightPacketUtc} · Source: ${p.sourceObservationUtc || "Unknown"} · ${number(p.bytes / 1048576)} MiB`,
        ),
        action("View Data", async () => {
          viewer.replaceChildren();
          await viewProduct(viewer, await api(`/api/science/products/${p.id}`), api);
        }),
      );
      list.append(row);
    }
    if (!list.children.length)
      list.append(
        e(
          "p",
          "No saved products on this page. Scientific data unavailable for this location or acquisition until a supported source and a valid recording footprint are available.",
        ),
      );
  }
  await load();
  return section;
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
  map.set({
    plan: {
      id: p.id,
      surveyAreas: [
        {
          name: "Approximate acquisition footprint",
          vertices: p.footprint.boundary,
        },
      ],
      waypoints: [],
    },
    markers: [],
    track: [],
  });
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
  let chart, pointsLayer;
  const draw = () => {
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
  const stopUnits=onUnitsChanged(draw);
  const removed=new MutationObserver(()=>{
    if(!mapHolder.isConnected){removed.disconnect();stopUnits();chart?.destroy();map.destroy();}
  });
  removed.observe(document.body,{childList:true,subtree:true});
}
