import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {effectiveLocale,onLocaleChange} from './i18n.js';
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
  if (value !== undefined) setPresentationText(n,value);
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
  if(id==='elevation')return {value:Number.isFinite(value)?value*(settings?.preferences.altitude==='feet'?1/.3048:1):null,unit:settings?.preferences.altitude==='feet'?'ft':'m',decimals:settings?.preferences.altitude==='feet'?0:1};
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
  details.append(e("summary", localizedCopy("ui.scienceDisplay.messageScienceDisplayUnits")));
  parent.append(details);
  await planningUnitControls(details,api);
  const grid = e("div");
  grid.className = "research-grid";
  details.append(
    e(
      "p",
      localizedCopy("ui.scienceDisplay.messageDisplayPreferencesApplyAcrossScienceChartsHistory"),
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
    [textForUI("ui.scienceDisplay.messageAviation"), "aviation"],
    [textForUI("ui.scienceDisplay.messageScientific"), "scientific"],
  ]) {
    const b = e("button", localizedCopy("ui.scienceDisplay.messageUnits",{value1:(name)}));
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
    ["temperature", "altitudeMslFeet", textForUI("ui.scienceDisplay.messageTemperatureVsAltitude")],
    ["pressure", "altitudeMslFeet", textForUI("ui.scienceDisplay.messagePressureVsAltitude")],
    ["wind-speed", "altitudeMslFeet", textForUI("ui.scienceDisplay.messageWindSpeedVsAltitude")],
    ["temperature", "timestampUtc", textForUI("ui.scienceDisplay.messageTemperatureOverTime")],
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
        e("p", localizedCopy("ui.scienceDisplay.messageNoValidSavedObservationsInThisDisplay")),
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
    setPresentationText(select,localizedCopy("ui.scienceDisplay.messageTimeRange",{value1:(title)}),"aria-label");
    for (const [value, label] of [
      ["all", textForUI("ui.scienceDisplay.messageAllLoadedObservations")],
      ["600", textForUI("ui.scienceDisplay.messageLast10Minutes")],
      ["60", textForUI("ui.scienceDisplay.messageLastMinute")],
    ]) {
      const o = e("option", label);
      o.value = value;
      select.append(o);
    }
    const preferenceKey = (parent.id || "archive") + ":" + id + ":" + xkey;
    const preferences = chartPreferences.get(preferenceKey) || {
      range: "all",
      points: true,
    };
    select.value = preferences.range;
    tools.append(select);
    const mark = e("label", localizedCopy("ui.scienceDisplay.messageShowObservationPoints")),
      check = e("input");
    check.type = "checkbox";
    check.checked = preferences.points;
    mark.prepend(check);
    tools.append(mark);
    const viewport = e("div");
    viewport.className = "chart-viewport";
    const canvas = e("canvas");
    setPresentationText(canvas,title,"aria-label");
    canvas.setAttribute("role", "img");
    viewport.append(canvas);
    card.append(viewport);
    const summary = e("p");
    summary.className = "chart-summary";
    card.append(summary);
    const values=e('details');values.className='chart-values advanced-details';values.append(e('summary',localizedCopy("ui.scienceDisplay.messageInspectRecordedChartValues")));const valueRows=e('div');values.append(valueRows);card.append(values);let displayed=points;
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
      displayed=selected;valueRows.replaceChildren();scientificTable(valueRows,title+' recorded values',[xunit,unit,textForUI("ui.scienceDisplay.messageObservationUTC"),textForUI("ui.atmosphericResearch.messageQuality"),textForUI("ui.surveyResearch.messageProvenance")],selected.map(p=>[xkey==='timestampUtc'?p.row.timestampUtc:numeric(p.x),numeric(p.y),p.row.timestampUtc,p.row.quality,p.row.provenance]));
      const reduced = reduceExtrema(selected);
      const min = Math.min(...selected.map((p) => p.y)),
        max = Math.max(...selected.map((p) => p.y));
      setPresentationText(summary,textForUI("ui.scienceDisplay.messageActualMeasurementValuesMin",{value1:(selected.length),value2:(min.toFixed(2)),value3:(unit),value4:(max.toFixed(2)),value5:(unit),value6:(reduced.length < selected.length ? localizedCopy("ui.scienceDisplay.messageDisplayReductionPreservesEachTimeBucketSMinima") : "")}));
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
                    textForUI("ui.scienceDisplay.messageAltitude",{value1:(String(displayReading("altitude", p.row.altitudeMslFeet, "ft MSL").value?.toFixed(0) ?? "Unavailable")),value2:(String(displayReading("altitude", 0, "ft MSL").unit))}),
                    textForUI("ui.scienceDisplay.messageQuality",{value1:(String(p.row.quality)),value2:(String(p.row.provenance))}),
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
    action(localizedCopy("ui.scienceDisplay.messageExpand"), () => {
      const launch=document.activeElement;const dialog = e("dialog");
      dialog.className = "expanded-chart";
      const close = e("button", localizedCopy("ui.scienceDisplay.messageCloseExpandedChart"));
      close.type = "button";
      dialog.append(close);
      document.body.append(dialog);
      const original = card.parentNode,
        next = card.nextSibling;
      dialog.append(card);
      dialogHeader(dialog,{title:localizedCopy("ui.scienceDisplay.messageExpandedScientificChart"),opener:launch,reuseHeading:false});dialog.showModal();
      chart.resize();
      close.onclick = () => requestDialogClose(dialog);
      dialog.onclose = () => {
        original.insertBefore(card, next);
        dialog.remove();
        chart.resize();
        launch?.focus({preventScroll:true});
      };
    });
    action(localizedCopy("ui.scienceDisplay.messageSavePNG"), () => {
      const a = e("a");
      a.download = title.replaceAll(" ", "-") + ".png";
      a.href = chart.toBase64Image();
      a.click();
    });
    action(localizedCopy("ui.scienceDisplay.messageExportDisplayedCSV"), () => {
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
const planningNumber=value=>Number.isFinite(value)?value.toLocaleString(effectiveLocale(),{maximumFractionDigits:Math.abs(value)<1?6:3}):textForUI("localization.unavailableValue");
export function planningDistance(metres) {const d=planningMeasure("distance",metres);return `${planningNumber(d.value)} ${d.unit}`;}
export function planningArea(squareMetres) {
  if(!Number.isFinite(squareMetres))return textForUI("localization.unavailableValue");
  const distance=(settings?.preferences?.planningProfile||(["kilometres","metres"].includes(settings?.preferences?.distance)?"metric":"aviation"))==="metric"?"kilometres":"nauticalMiles";
  const [scale,unit]=distance==="nauticalMiles"?[1/(1852*1852),"NM²"]:distance==="miles"?[1/(1609.344*1609.344),"mi²"]:Math.abs(squareMetres)>=1e6?[1e-6,"km²"]:[1,"m²"];
  return `${planningNumber(squareMetres*scale)} ${unit}`;
}
export function planningSurvey(summary) {return textForUI("ui.scienceDisplay.messageApproximateAreaEWNSSpherical",{value1:(String(planningArea(summary.squareMetres))),value2:(String(planningDistance(summary.widthMetres))),value3:(String(planningDistance(summary.lengthMetres)))});}
const planningBindings=new WeakMap();
export function planningText(node, render) {
  planningBindings.get(node)?.();
  let last;
  const refresh=()=>{
    if(last!==undefined&&node.textContent!==last){planningBindings.get(node)?.();planningBindings.delete(node);return;}
    setPresentationText(node,render());last=node.textContent;
  };refresh();
  const unitsUnsubscribe=onUnitsChanged(refresh),localeUnsubscribe=onLocaleChange(refresh,{owner:node});
  const unsubscribe=()=>{unitsUnsubscribe();localeUnsubscribe();};planningBindings.set(node,unsubscribe);
}
export function planningField(input,category,canonical,title,suffix="") {
  let value=canonical,scale=1,rendered=input.value;
  const read=()=>{if(input.value!==rendered){value=input.value===""?null:Number(input.value)/scale;rendered=input.value;}return value;};
  const refresh=()=>{
    read();const d=planningMeasure(category,value);scale=d.scale;
    const label=input.closest("label");
    const units=suffix==="AGL"?d.unit.replace('MSL','AGL'):suffix==='interval'?d.unit.replace(' MSL',''):d.unit,reference=suffix&&!['AGL','interval'].includes(suffix)?", "+suffix:"";
    const fieldLabel=localizedCopy('components.fieldWithUnit',{title,unit:units,reference});
    if(label?.firstChild?.nodeType===Node.TEXT_NODE)setPresentationText(label.firstChild,fieldLabel);
    setPresentationText(input,fieldLabel,"aria-label");
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
  const label=e("label",localizedCopy("ui.scienceDisplay.messagePlanningUnits")),select=e("select"),status=e("span");
  setPresentationText(select,localizedCopy("ui.scienceDisplay.messagePlanningUnits"),"aria-label");
  for(const [id,name] of [["aviation",textForUI("ui.scienceDisplay.messageAviationNMFeetKnots")],["metric",textForUI("ui.scienceDisplay.messageMetricMOrKmMeters")]]){const option=e("option",name);option.value=id;select.append(option);}
  const refresh=()=>{const p=settings.preferences;select.value=p.planningProfile||(["kilometres","metres"].includes(p.distance)?"metric":"aviation");};
  select.onchange=async()=>{
    select.disabled=true;
    try{
      const metric=select.value==="metric";
      const next=await api("/api/display/units",{...settings.preferences,planningProfile:metric?"metric":"aviation"});
      syncUnits(next);setPresentationText(status,localizedCopy("ui.scienceDisplay.messageDisplayConvertedGeographicGeometryAndStoredUnits"));
    }catch(error){setPresentationText(status,error.message);refresh();}finally{select.disabled=false;}
  };
  label.append(select,status);parent.append(label);refresh();const unsubscribe=onUnitsChanged(refresh);planningBindings.set(select,unsubscribe);
}
new MutationObserver(()=>{for(const [node,unsubscribe] of planningBindings)if(!node.isConnected){unsubscribe();planningBindings.delete(node);}}).observe(document.documentElement,{childList:true,subtree:true});
