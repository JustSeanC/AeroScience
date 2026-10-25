import {renderInstrumentSummaries,instrumentSummaries} from './preparation-summary.js';
import {openOnlineAccess} from './online-access.js';
import {openDownloadActivity,allowanceText} from "./download-activity.js";
import { openHyperspectralImport } from "./hyperspectral-import.js";
import { planningDistance } from "./science-display.js";
const node = (tag, text) => { const n = document.createElement(tag); if (text !== undefined) n.textContent = text; return n; };
const bytes = n => n == null ? "Unknown" : `${(n / 1048576).toFixed(1)} MiB`;
function button(label, run) { const b = node("button", label); b.type = "button"; b.className = "secondary"; b.onclick = async () => { b.disabled = true; try { await run(); } catch (e) { alert(e.message); } finally { b.disabled = false; } }; return b; }
const titles = { terrain: "Terrain / Elevation", thermal: "Thermal", multispectral: "Multispectral", hyperspectral: "Hyperspectral" };
export async function scientificAccessSettings(parent, api) {
  const section = node("section"); section.className = "panel research-panel";
  section.append(node("h2", "Automatic Scientific Data Access")); parent.append(section);
  const { configuration, canConfigure } = await api("/api/science/access");
  section.append(node("p", configuration.privacy), node("p", "Historical satellite and elevation data are external Earth observations, not live aircraft cameras or LiDAR. Downloads go directly to public providers; no AeroScience download account or API key is required for terrain, thermal or multispectral."));
  if (configuration.warning) section.append(node("p", configuration.warning));
  const form = node("form"); const p = configuration.preferences;
  function checkbox(label, checked) { const l = node("label"); const input = node("input"); input.type = "checkbox"; input.checked = checked; input.disabled = !canConfigure; l.append(input, document.createTextNode(" " + label)); form.append(l, node("br")); return input; }
  function input(label, value, type = "number") { const l = node("label", label + " "); const n = node("input"); n.type = type; n.value = value ?? ""; n.disabled = !canConfigure; l.append(n); form.append(l, node("br")); return n; }
  const enabled = checkbox("Allow automatic scientific-data discovery and downloads", p.onlineEnabled);
  const consent = checkbox("I understand and accept the separate scientific-data privacy disclosure above", p.privacyAccepted);
  const unmetered = checkbox("Download on unmetered networks only (unknown network cost pauses downloads)", p.unmeteredOnly);
  section.append(node("p", "Windows network cost: " + configuration.networkCost));
  const background = checkbox("Allow bounded background retrieval around the active aircraft and saved plans", p.backgroundRetrieval);
  const maximum = input("Maximum network transfer per preparation (MiB)", p.maximumDownloadBytes / 1048576); maximum.min = 1; maximum.max = 512;
  const nasaMaximum = input("NASA original-scene download cap (MiB)", (p.nasaOriginalMaximumBytes ?? 1073741824) / 1048576); nasaMaximum.min = 1; nasaMaximum.max = 20480;
  form.append(node("p", "NASA Download & Import uses complete original ENVI files after explicit scene approval. This separate cap defaults to 1 GiB and does not increase automatic subset limits; job/preflight, consent and storage caps also apply. Connect Earthdata in External Data Manager → Find & Download Data → Hyperspectral Research. No NASA password is requested."));
  const lookahead=input("Advanced lookahead seconds (0 = adaptive, maximum 900)",p.lookaheadSeconds??0);lookahead.min=0;lookahead.max=900;
  const hourly=input("Overall scientific transfer budget per rolling hour (MiB)",(p.prefetchHourlyBytes??268435456)/1048576);hourly.min=1;hourly.max=20480;
  const unlimited=checkbox("Unlimited in-flight hourly transfer allowance",p.prefetchHourlyBytes==null);
  const preflight=input("Preflight transfer allowance (MiB per application session)",(p.preflightTransferBytes??1073741824)/1048576);preflight.min=1;preflight.max=20480;
  const preUnlimited=checkbox("Unlimited preflight transfer allowance",p.preflightTransferBytes===null);
  const toggle=()=>{hourly.disabled=!canConfigure||unlimited.checked;preflight.disabled=!canConfigure||preUnlimited.checked;};unlimited.onchange=preUnlimited.onchange=toggle;toggle();
  form.append(node("p","Unlimited can use substantial network data. These are transfer-volume allowances, not connection speed. Per-preparation, provider, consent and storage limits remain in force. Preflight does not debit the in-flight hourly allowance."));
  const concurrent = input("Concurrent preparations", p.concurrentDownloads); concurrent.min = 1; concurrent.max = 8;
  form.append(node("p","One preparation slot is the conservative default; up to eight are configurable. Multi-provider benchmarks encountered rate limits and HTTP 503 at higher settings. Higher limits can saturate the network or trigger provider throttling. Each provider connection remains bounded; existing saved limits are preserved."));
  const days = input("Search lookback days", p.lookbackDays); days.min = 1; days.max = 3650;
  const cloud = input("Maximum scene cloud cover (%) — local quality masks still apply", p.maximumCloudPercent); cloud.min = 0; cloud.max = 100;
  const date = input("Preferred latest observation date (blank = now)", p.preferredDateUtc?.slice(0, 10), "date");
  const policy = node("select"); for (const [value, label] of [["clear-recent", "Clearest scenes within the date window"], ["newest", "Newest acceptable scene"]]) { const o = node("option", label); o.value = value; o.selected = value === p.selectionPolicy; policy.append(o); } policy.disabled = !canConfigure; form.append(node("label", "Source date / quality policy "), policy);
  const result = node("p");
  const combinedAccess=button("Review Combined Online Access",()=>openOnlineAccess(api,{opener:combinedAccess,onAllowed:state=>{
    if(!section.isConnected)return;
    enabled.checked=state.science.configuration.preferences.onlineEnabled;
    consent.checked=state.science.configuration.preferences.privacyAccepted;
    result.textContent="Online maps, scientific access and automatic FAA/TFR updates are allowed. Your other settings and download approvals are unchanged.";
  }}));
  section.append(combinedAccess,node("p","Use one disclosure to allow maps, scientific access and FAA/TFR updates, or keep using the individual controls below. NASA authorization and original-download approvals remain separate."));
  form.append(node("br"), button("Save scientific access preferences", async () => { if (!form.reportValidity()) return; await api("/api/science/access", { ...(await api("/api/science/access")).configuration.preferences, onlineEnabled: enabled.checked, privacyAccepted: consent.checked, unmeteredOnly: unmetered.checked, backgroundRetrieval: background.checked, maximumDownloadBytes: Math.round(Number(maximum.value) * 1048576), nasaOriginalMaximumBytes: Math.round(Number(nasaMaximum.value) * 1048576), concurrentDownloads: Number(concurrent.value), lookaheadSeconds:Number(lookahead.value),prefetchHourlyBytes:unlimited.checked?null:Math.round(Number(hourly.value)*1048576),preflightTransferBytes:preUnlimited.checked?null:Math.round(Number(preflight.value)*1048576), lookbackDays: Number(days.value), maximumCloudPercent: Number(cloud.value), preferredDateUtc: date.value ? date.value + "T23:59:59Z" : null, selectionPolicy: policy.value }); result.textContent = enabled.checked ? "Saved. Enable an instrument and prepare a plan, or fly for bounded background discovery. Eligible archived remote-source attempts can be reconstructed after flight; absent atmospheric measurements remain missing." : "Offline-only scientific operation saved. Retained sources and mission history remain available."; }), button("Cancel current downloads", () => api("/api/science/prepare/cancel", {})), result);
  if (!canConfigure) form.append(node("p", "Change scientific access controls in the companion PC's Settings. Paired clients share its saved preferences and readiness."));
  section.append(form, node("p", "Prepared source subsets are retained scientific originals and count toward your Science Library limit. Cleanup removes only eligible regenerable caches. Download/storage figures shown before preparation are conservative limits, not guaranteed file sizes."));
  const lat = input("Prepare latitude", "", "number"), lon = input("Prepare longitude", "", "number"); lat.step = lon.step = "any"; lat.min = -80; lat.max = 84; lon.min = -180; lon.max = 179.9999;
  form.append(button("Prepare a small area", async () => { if (!lat.value || !lon.value || !form.reportValidity()) { result.textContent = "Enter a valid latitude and longitude."; return; } await api("/api/science/prepare", { latitude: Number(lat.value), longitude: Number(lon.value) }); result.textContent = "Small-area terrain, thermal and multispectral preparations requested. Check Scientific Data Readiness."; }));
  await scientificReadiness(section, api);
}
export async function scientificReadiness(parent, api, map = null) {
  const section=node("section");section.className="panel research-panel scientific-readiness";
  const heading=node("h3","Scientific Data Preparation"),status=node("p"),warning=node("p"),summaries=node("div"),help=node("a","Configure Scientific Data Access"),advanced=node("details");help.href="#settings/science?section=access";
  advanced.append(node("summary","Advanced — individual regions & diagnostics"),button("View Download Activity",()=>openDownloadActivity(api)));
  section.append(heading,status,summaries,warning,help,advanced);parent.append(section);
  let timer;
  async function refresh(){if(!section.isConnected){clearTimeout(timer);return;}
    if(!document.hidden&&!section.closest("[hidden]")){try{
      const [state,prediction,access,plans]=await Promise.all([api("/api/state"),api("/api/science/prediction"),api("/api/science/access"),api("/api/science/plan-readiness")]);if(!section.isConnected)return;
      const selected=state.activeMission?state.research?.planId:state.selectedPlanId;
      const plan=plans.find(p=>p.planId===selected);
      if(selected){
        const definition=await api(`/api/plans/${selected}`);heading.textContent="Data Preparation — "+definition.name;
        const items=await api(`/api/plans/${selected}/preparation/instruments`);
        renderInstrumentSummaries(summaries,items.length?items:(definition.instruments||[]).filter(c=>c.enabled).map(c=>({instrumentId:c.instrumentId,name:c.instrumentId,status:["terrain-scanner","thermal-imager","multispectral-imager","hyperspectral-imager"].includes(c.instrumentId)?"Not Prepared":"Live simulator telemetry required; no remote preparation",total:0,ready:0,awaiting:0,unavailable:0})));
        status.textContent=plan?`${plan.status} · ${plan.ready} ready / ${plan.awaiting} awaiting / ${plan.unavailable} unavailable logical instrument × region units`:'Plan selected · Not Prepared. Selecting a plan does not queue downloads.';status.textContent+=' You may start with incomplete external data. Upcoming regions will be attempted during flight under consent, budgets and flight priority. Late arrivals do not create missed past observations.';
        if(map&&plan){const regions=await api(`/api/plans/${selected}/preparation/regions`);map.setReadiness(regions);}
      }else{heading.textContent=state.activeMission?"Data Preparation — Active Flight":"Scientific Data Preparation";renderInstrumentSummaries(summaries,instrumentSummaries(prediction.areas));status.textContent=prediction.areas.length?'Active flight corridor; source readiness does not establish valid-pixel survey coverage.':'No flight corridor or mission preparation selected.';map?.setReadiness(prediction.areas);}
      warning.textContent=prediction.lookaheadSeconds>0&&(prediction.readyAheadSeconds??0)<prediction.queue.estimatedPreparationSeconds?"Aircraft may outrun source preparation. Flight recording continues; eligible saved packets can be reviewed for post-flight reconstruction.":"Ready sources retain their actual dates, cloud/nodata masks and coverage limits.";
      help.hidden=access.configuration.preferences.onlineEnabled;
    }catch(e){warning.textContent=e.message;}}
    timer=setTimeout(refresh,3000);
  }await refresh();return section;
}
