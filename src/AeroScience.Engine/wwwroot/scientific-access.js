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
  const concurrent = input("Concurrent preparations", p.concurrentDownloads); concurrent.min = 1; concurrent.max = 2;
  const days = input("Search lookback days", p.lookbackDays); days.min = 1; days.max = 3650;
  const cloud = input("Maximum scene cloud cover (%) — local quality masks still apply", p.maximumCloudPercent); cloud.min = 0; cloud.max = 100;
  const date = input("Preferred latest observation date (blank = now)", p.preferredDateUtc?.slice(0, 10), "date");
  const policy = node("select"); for (const [value, label] of [["clear-recent", "Clearest scenes within the date window"], ["newest", "Newest acceptable scene"]]) { const o = node("option", label); o.value = value; o.selected = value === p.selectionPolicy; policy.append(o); } policy.disabled = !canConfigure; form.append(node("label", "Source date / quality policy "), policy);
  const result = node("p");
  form.append(node("br"), button("Save scientific access preferences", async () => { if (!form.reportValidity()) return; await api("/api/science/access", { onlineEnabled: enabled.checked, privacyAccepted: consent.checked, unmeteredOnly: unmetered.checked, backgroundRetrieval: background.checked, maximumDownloadBytes: Math.round(Number(maximum.value) * 1048576), concurrentDownloads: Number(concurrent.value), lookbackDays: Number(days.value), maximumCloudPercent: Number(cloud.value), preferredDateUtc: date.value ? date.value + "T23:59:59Z" : null, selectionPolicy: policy.value }); result.textContent = enabled.checked ? "Saved. Enable an instrument and prepare a plan, or fly for bounded background discovery. No past missing observations will be backfilled." : "Offline-only scientific operation saved. Retained sources and mission history remain available."; }), button("Cancel current downloads", () => api("/api/science/prepare/cancel", {})), result);
  if (!canConfigure) form.append(node("p", "Change scientific access controls in the companion PC's Settings. Paired clients share its saved preferences and readiness."));
  section.append(form, node("p", "Prepared source subsets are retained scientific originals and count toward your Science Library limit. Cleanup removes only eligible regenerable caches. Download/storage figures shown before preparation are conservative limits, not guaranteed file sizes."));
  const lat = input("Prepare latitude", "", "number"), lon = input("Prepare longitude", "", "number"); lat.step = lon.step = "any"; lat.min = -80; lat.max = 84; lon.min = -180; lon.max = 179.9999;
  form.append(button("Prepare a small area", async () => { if (!lat.value || !lon.value || !form.reportValidity()) { result.textContent = "Enter a valid latitude and longitude."; return; } await api("/api/science/prepare", { latitude: Number(lat.value), longitude: Number(lon.value) }); result.textContent = "Small-area terrain, thermal and multispectral preparations requested. Check Scientific Data Readiness."; }));
  await scientificReadiness(section, api);
}
export async function scientificReadiness(parent, api) {
  const section = node("section"); section.className = "panel research-panel scientific-readiness";
  section.append(node("h3", "Scientific Data Readiness"), node("p", "External datasets have their own observation dates. Ready source coverage can still contain cloud/nodata pixels; only valid flight-time samples are recorded."));
  const content = node("div"); section.append(content); parent.append(section);
  let timer;
  async function refresh() {
    if (!section.isConnected) { clearTimeout(timer); return; }
    if (!section.closest("[hidden]")) {
      try {
        const [states, plans] = await Promise.all([api("/api/science/readiness"), api("/api/science/plan-readiness")]); content.replaceChildren();
        for (const plan of plans.filter(p => p.limitedCoverage)) content.append(node("p", `${plan.planName} · ${plan.message}`));
        if (!states.length) content.append(node("p", "No data preparations yet. Enable scientific access in Settings, then enable an instrument and save a plan or begin flight. Manual source collections remain usable offline."));
        for (const s of states.slice(0, 16)) {
          const item = node("article"); item.className = "source-notice";
          item.append(node("strong", `${titles[s.kind] || s.kind} · ${s.status}`), node("p", s.message));
          if (s.provider) item.append(node("p", `${s.provider} · ${s.dataset || ""}`));
          item.append(node("p", `${s.status === "Ready" ? "Prepared coverage" : "Requested area"}: ${s.area.west.toFixed(4)}, ${s.area.south.toFixed(4)} to ${s.area.east.toFixed(4)}, ${s.area.north.toFixed(4)} · Source observed: ${s.observationUtc || "Unknown / composite"}`));
          if (s.quality) item.append(node("p", s.quality));
          item.append(node("p", `Transferred: ${bytes(s.downloadedBytes)} · Download ceiling: ${bytes(s.estimatedDownloadBytes)} · Conservative staging bound: ${bytes(s.estimatedStorageBytes)}`));
          if (s.authorization) item.append(node("p", s.authorization));
          for (const d of s.discoveries || []) item.append(node("p", `${d.catalog}: ${d.dataset} · ${d.observationUtc || "Unknown date"} · ${d.format} · Discovery only`));
          if (s.status === "Unavailable" || s.status === "Offline") item.append(button("Retry this area", () => api("/api/science/prepare", { latitude: s.area.center.latitude, longitude: s.area.center.longitude, kinds: [s.kind] })));
          content.append(item);
        }
      } catch (error) { content.replaceChildren(node("p", "Readiness unavailable: " + error.message)); }
    }
    timer = setTimeout(refresh, 5000);
  }
  section.append(button("Cancel preparation downloads", () => api("/api/science/prepare/cancel", {})));
  await refresh();
  return section;
}
