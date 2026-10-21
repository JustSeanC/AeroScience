import {dialogHeader,requestDialogClose,markDialogSaved} from './dialog-ui.js';
import {table as scientificTable,numeric} from './workstation-ui.js';
let settings = null;
const chartPreferences = new Map();
const unitListeners = new Set();
export function onUnitsChanged(listener) { unitListeners.add(listener); return () => unitListeners.delete(listener); }
const categories = {
  temperature: "temperature",
  pressure: "pressure",
  "wind-speed": "windSpeed",
  "wind-direction": "windDirection",
  density: "density",
  altitude: "altitude",
  distance: "distance",
};
const e = (tag, value) => {
  const n = document.createElement(tag);
  if (value !== undefined) n.textContent = value;
  return n;
};
export async function initializeUnits(api) {
  const before=JSON.stringify(settings?.preferences);
  settings = await api("/api/display/units");
  if(before!==JSON.stringify(settings.preferences))for(const listener of unitListeners)listener();
  return settings;
}
export function syncUnits(preferences) {
  if (settings && preferences && JSON.stringify(settings.preferences)!==JSON.stringify(preferences)) {
    settings.preferences = preferences;for(const listener of unitListeners)listener();
  }
}
export function displayReading(id, value, fallback = "") {
  const category = categories[id.replace(/\.user-adjusted$/, '')],
    options = settings?.options[category],
    choice =
      category === "windDirection"
        ? "degreesTrue"
        : settings?.preferences[category];
  const conversion = options?.find((o) => o.id === choice);
  return {
    value: Number.isFinite(value)
      ? value * (conversion?.scale ?? 1) + (conversion?.offset ?? 0)
      : null,
    unit: conversion?.label || fallback,
    decimals: conversion?.decimals ?? 3,
  };
}
export async function unitControls(parent, api, changed = () => {}) {
  await initializeUnits(api);
  const details = e("details");
  details.className = "unit-settings";
  details.append(e("summary", "Science display units"));
  parent.append(details);
  await planningUnitControls(details,api);
  const grid = e("div");
  grid.className = "research-grid";
  details.append(
    e(
      "p",
      "Display preferences apply across Science, charts, history and the toolbar. Canonical recordings and default exports remain unchanged.",
    ),
    grid,
  );
  for (const [category, options] of Object.entries(settings.options)) {
    if (category === "windDirection") continue;
    const label = e("label", category.replace(/([A-Z])/g, " $1")),
      select = e("select");
    for (const o of options) {
      const option = e("option", o.label);
      option.value = o.id;
      select.append(option);
    }
    select.value = settings.preferences[category];
    select.onchange = async () => {
      try {
        const next = await api("/api/display/units", {
          ...settings.preferences,
          [category]: select.value,
        });
        syncUnits(next);await changed();
      } catch (error) {
        label.append(e("span", error.message));
      }
    };
    label.append(select);
    grid.append(label);
  }
  for (const [name, key] of [
    ["Aviation", "aviation"],
    ["Scientific", "scientific"],
  ]) {
    const b = e("button", name + " units");
    b.type = "button";
    b.className = "secondary";
    b.onclick = async () => {
      await api("/api/display/units", settings[key]);
      await changed();
    };
    details.append(b);
  }
  if (settings.warning) details.append(e("p", settings.warning));
}
export function reduceExtrema(points, maximum = 1200) {
  if (points.length <= maximum) return points;
  const width = Math.ceil(points.length / (maximum / 4)),
    result = [];
  for (let i = 0; i < points.length; i += width) {
    const bucket = points.slice(i, i + width),
      min = bucket.reduce((a, b) => (a.y < b.y ? a : b)),
      max = bucket.reduce((a, b) => (a.y > b.y ? a : b));
    const selected = new Set([bucket[0], min, max, bucket.at(-1)]);
    result.push(...bucket.filter((p) => selected.has(p)));
  }
  return result;
}
export function scienceCharts(parent, rows) {
  for (const [id, xkey, title] of [
    ["temperature", "altitudeMslFeet", "Temperature vs altitude"],
    ["pressure", "altitudeMslFeet", "Pressure vs altitude"],
    ["wind-speed", "altitudeMslFeet", "Wind speed vs altitude"],
    ["temperature", "timestampUtc", "Temperature over time"],
  ]) {
    const card = e("article");
    card.className = "panel science-chart";
    card.append(e("h3", title));
    parent.append(card);
    const points = rows
      .filter(
        (r) =>
          r.measurementId === id &&
          ["valid", "acquisitionGap"].includes(r.quality) &&
          r[xkey] != null,
      )
      .map((r) => ({
        x:
          xkey === "timestampUtc"
            ? Date.parse(r[xkey])
            : displayReading("altitude", r[xkey], "ft MSL").value,
        y: displayReading(id, r.value, r.unit).value,
        row: r,
      }))
      .filter((p) => Number.isFinite(p.x) && Number.isFinite(p.y));
    if (!points.length) {
      card.append(
        e("p", "No valid saved observations in this display window."),
      );
      continue;
    }
    const unit = displayReading(id, 0, points[0].row.unit).unit,
      xunit =
        xkey === "timestampUtc"
          ? "UTC"
          : displayReading("altitude", 0, "ft MSL").unit;
    const tools = e("div");
    tools.className = "map-tools";
    card.append(tools);
    const select = e("select");
    select.setAttribute("aria-label", title + " time range");
    for (const [value, label] of [
      ["all", "All loaded observations"],
      ["600", "Last 10 minutes"],
      ["60", "Last minute"],
    ]) {
      const o = e("option", label);
      o.value = value;
      select.append(o);
    }
    const preferenceKey = (parent.id || "archive") + ":" + title;
    const preferences = chartPreferences.get(preferenceKey) || {
      range: "all",
      points: true,
    };
    select.value = preferences.range;
    tools.append(select);
    const mark = e("label", " Show observation points"),
      check = e("input");
    check.type = "checkbox";
    check.checked = preferences.points;
    mark.prepend(check);
    tools.append(mark);
    const viewport = e("div");
    viewport.className = "chart-viewport";
    const canvas = e("canvas");
    canvas.setAttribute("aria-label", title);
    canvas.setAttribute("role", "img");
    viewport.append(canvas);
    card.append(viewport);
    const summary = e("p");
    summary.className = "chart-summary";
    card.append(summary);
    const values=e('details');values.className='chart-values advanced-details';values.append(e('summary','Inspect recorded chart values'));const valueRows=e('div');values.append(valueRows);card.append(values);let displayed=points;
    let chart;
    const render = () => {
      let selected =
        select.value === "all"
          ? points
          : points.filter(
              (p) =>
                Date.parse(p.row.timestampUtc) >=
                Date.parse(points.at(-1).row.timestampUtc) -
                  Number(select.value) * 1000,
            );
      displayed=selected;valueRows.replaceChildren();scientificTable(valueRows,title+' recorded values',[xunit,unit,'Observation UTC','Quality','Provenance'],selected.map(p=>[xkey==='timestampUtc'?p.row.timestampUtc:numeric(p.x),numeric(p.y),p.row.timestampUtc,p.row.quality,p.row.provenance]));
      const reduced = reduceExtrema(selected);
      const min = Math.min(...selected.map((p) => p.y)),
        max = Math.max(...selected.map((p) => p.y));
      summary.textContent = `${selected.length} actual measurement values · Min ${min.toFixed(2)} ${unit} · Max ${max.toFixed(2)} ${unit}. ${reduced.length < selected.length ? "Display reduction preserves each time bucket’s minima and maxima. " : ""}No interpolation or smoothing. Observation markers leave recording gaps empty; repeated altitudes retain time order in tooltips.`;
      const datasets = [];
      if (xkey === "timestampUtc") {
        let segment = [],
          last;
        for (const p of reduced) {
          const gap =
            last &&
            p.x - last.x > Math.max(5000, Number(p.row.intervalMs || 0) * 2000);
          if (gap) {
            datasets.push({ data: segment, showLine: false });
            segment = [];
          }
          segment.push(p);
          last = p;
        }
        datasets.push({ data: segment, showLine: false });
      } else datasets.push({ data: reduced, showLine: false });
      chart?.destroy();
      chart = new window.Chart(canvas, {
        type: "scatter",
        data: {
          datasets: datasets.map((d) => ({
            ...d,
            label: title,
            borderColor: "#50dcc7",
            backgroundColor: "#50dcc7",
            borderWidth: 1.5,
            pointRadius: check.checked ? 2 : 0,
            pointHitRadius: 10,
            tension: 0,
          })),
        },
        options: {
          responsive: true,
          maintainAspectRatio: false,
          animation: false,
          parsing: false,
          scales: {
            x: {
              type: "linear",
              title: {
                display: true,
                text: xunit,
                color: "#bed0dc",
                font: { size: 14 },
              },
              ticks: {
                color: "#a9bfcc",
                font: { size: 13 },
                maxTicksLimit: 6,
                callback:
                  xkey === "timestampUtc"
                    ? (v) => new Date(v).toISOString().slice(11, 19)
                    : undefined,
              },
              grid: { color: "#29404e" },
            },
            y: {
              title: {
                display: true,
                text: unit,
                color: "#bed0dc",
                font: { size: 14 },
              },
              ticks: { color: "#a9bfcc", font: { size: 13 } },
              grid: { color: "#29404e" },
            },
          },
          plugins: {
            legend: { display: false },
            tooltip: {
              callbacks: {
                label: (context) => {
                  const p = context.raw;
                  return [
                    `${p.y.toFixed(3)} ${unit}`,
                    `${p.row.timestampUtc}`,
                    `Altitude ${displayReading("altitude", p.row.altitudeMslFeet, "ft MSL").value?.toFixed(0) ?? "Unavailable"} ${displayReading("altitude", 0, "ft MSL").unit}`,
                    `Quality: ${p.row.quality} · ${p.row.provenance}`,
                  ];
                },
              },
            },
          },
        },
      });
    };
    select.onchange = () => {
      chartPreferences.set(preferenceKey, {
        range: select.value,
        points: check.checked,
      });
      render();
    };
    check.onchange = () => {
      chartPreferences.set(preferenceKey, {
        range: select.value,
        points: check.checked,
      });
      render();
    };
    render();
    const action = (label, fn) => {
      const b = e("button", label);
      b.type = "button";
      b.className = "secondary";
      b.onclick = fn;
      tools.append(b);
    };
    action("Expand", () => {
      const launch=document.activeElement;const dialog = e("dialog");
      dialog.className = "expanded-chart";
      const close = e("button", "Close expanded chart");
      close.type = "button";
      dialog.append(close);
      document.body.append(dialog);
      const original = card.parentNode,
        next = card.nextSibling;
      dialog.append(card);
      dialogHeader(dialog,{title:"Expanded scientific chart",opener:launch});dialog.showModal();
      chart.resize();
      close.onclick = () => requestDialogClose(dialog);
      dialog.onclose = () => {
        original.insertBefore(card, next);
        dialog.remove();
        chart.resize();
        launch?.focus({preventScroll:true});
      };
    });
    action("Save PNG", () => {
      const a = e("a");
      a.download = title.replaceAll(" ", "-") + ".png";
      a.href = chart.toBase64Image();
      a.click();
    });
    action("Export displayed CSV", () => {
      const csv = [
        "timestamp_utc,value,unit,altitude_msl_ft,quality,provenance",
        ...displayed.map((p) =>
          [
            p.row.timestampUtc,
            p.y,
            unit,
            p.row.altitudeMslFeet,
            p.row.quality,
            p.row.provenance,
          ]
            .map((v) => '"' + String(v ?? "").replaceAll('"', '""') + '"')
            .join(","),
        ),
      ].join("\r\n");
      const url = URL.createObjectURL(new Blob([csv], { type: "text/csv" }));
      const a = e("a");
      a.href = url;
      a.download = id + "-display-units.csv";
      a.click();
      URL.revokeObjectURL(url);
    });
  }
}
new MutationObserver(() => {
  for (const chart of Object.values(window.Chart?.instances || {}))
    if (!chart.canvas?.isConnected) chart.destroy();
}).observe(document.documentElement, { childList: true, subtree: true });

// Planning reuses the same persisted preferences/conversion table as Science.
// Canonical distance is metres; canonical altitude remains feet MSL.
export function planningMeasure(category, canonical, adaptive = true) {
  const profile=settings?.preferences?.planningProfile||(["kilometres","metres"].includes(settings?.preferences?.distance)?"metric":"aviation");
  const selected=category==="distance"?(profile==="metric"?"kilometres":"nauticalMiles"):category==="speed"?(profile==="metric"?"kilometresPerHour":"knots"):(profile==="metric"?"metres":"feet");
  let conversion=settings?.options?.[category==="speed"?"windSpeed":category]?.find(o=>o.id===selected);
  if(!conversion&&category==="speed")conversion={scale:profile==="metric"?1.852:1,label:profile==="metric"?"km/h":"kt"};
  if(!conversion)conversion=category==="distance"?{scale:1/1852,label:"NM"}:{scale:1,label:"ft MSL"};
  if(category==="distance"&&adaptive&&["kilometres","metres"].includes(selected))
    conversion=Math.abs(canonical||0)>=1000?{scale:.001,label:"km"}:{scale:1,label:"m"};
  return {value:canonical==null?null:canonical*conversion.scale,scale:conversion.scale,unit:conversion.label};
}
const planningNumber=value=>Number.isFinite(value)?value.toLocaleString(undefined,{maximumFractionDigits:Math.abs(value)<1?6:3}):"Unavailable";
export function planningDistance(metres) {const d=planningMeasure("distance",metres);return `${planningNumber(d.value)} ${d.unit}`;}
export function planningArea(squareMetres) {
  if(!Number.isFinite(squareMetres))return "Unavailable";
  const distance=(settings?.preferences?.planningProfile||(["kilometres","metres"].includes(settings?.preferences?.distance)?"metric":"aviation"))==="metric"?"kilometres":"nauticalMiles";
  const [scale,unit]=distance==="nauticalMiles"?[1/(1852*1852),"NM²"]:distance==="miles"?[1/(1609.344*1609.344),"mi²"]:Math.abs(squareMetres)>=1e6?[1e-6,"km²"]:[1,"m²"];
  return `${planningNumber(squareMetres*scale)} ${unit}`;
}
export function planningSurvey(summary) {return `Approximate area ${planningArea(summary.squareMetres)} · E–W ${planningDistance(summary.widthMetres)} × N–S ${planningDistance(summary.lengthMetres)} (spherical geographic estimate)`;}
const planningBindings=new Map();
export function planningText(node, render) {
  planningBindings.get(node)?.();
  let last;
  const refresh=()=>{
    if(last!==undefined&&node.textContent!==last){planningBindings.get(node)?.();planningBindings.delete(node);return;}
    node.textContent=render();last=node.textContent;
  };refresh();
  const unsubscribe=onUnitsChanged(refresh);planningBindings.set(node,unsubscribe);
}
export function planningField(input,category,canonical,title,suffix="") {
  let value=canonical,scale=1,rendered=input.value;
  const read=()=>{if(input.value!==rendered){value=input.value===""?null:Number(input.value)/scale;rendered=input.value;}return value;};
  const refresh=()=>{
    read();const d=planningMeasure(category,value);scale=d.scale;
    const label=input.closest("label");
    const units=suffix==="AGL"?d.unit.replace('MSL','AGL'):suffix==='interval'?d.unit.replace(' MSL',''):d.unit,reference=suffix&&!['AGL','interval'].includes(suffix)?", "+suffix:"";
    if(label?.firstChild?.nodeType===Node.TEXT_NODE)label.firstChild.textContent=`${title} (${units}${reference})`;
    input.setAttribute("aria-label",`${title} (${units}${reference})`);
    input.value=value==null?"":String(Number((value*scale).toFixed(category==="altitude"?3:6)));rendered=input.value;
  };
  const set=canonical=>{value=canonical;rendered=input.value;refresh();};
  // Initial value has not been entered by a user, so never interpret it as display units.
  input.value="";rendered="";refresh();input.step="any";
  input.addEventListener("input",read);const unsubscribe=onUnitsChanged(refresh);planningBindings.set(input,unsubscribe);
  input.planningUnits={read,set,refresh};return input.planningUnits;
}
export async function planningUnitControls(parent,api) {
  await initializeUnits(api);
  const label=e("label","Planning units"),select=e("select"),status=e("span");
  select.setAttribute("aria-label","Planning units");
  for(const [id,name] of [["aviation","Aviation — NM / feet / knots"],["metric","Metric — m or km / meters / km/h"]]){const option=e("option",name);option.value=id;select.append(option);}
  const refresh=()=>{const p=settings.preferences;select.value=p.planningProfile||(["kilometres","metres"].includes(p.distance)?"metric":"aviation");};
  select.onchange=async()=>{
    select.disabled=true;
    try{
      const metric=select.value==="metric";
      const next=await api("/api/display/units",{...settings.preferences,planningProfile:metric?"metric":"aviation"});
      syncUnits(next);status.textContent="Display converted; geographic geometry and stored units are unchanged.";
    }catch(error){status.textContent=error.message;refresh();}finally{select.disabled=false;}
  };
  label.append(select,status);parent.append(label);refresh();const unsubscribe=onUnitsChanged(refresh);planningBindings.set(select,unsubscribe);
}
new MutationObserver(()=>{for(const [node,unsubscribe] of planningBindings)if(!node.isConnected){unsubscribe();planningBindings.delete(node);}}).observe(document.documentElement,{childList:true,subtree:true});
