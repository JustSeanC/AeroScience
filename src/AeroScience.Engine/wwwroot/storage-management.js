const e = (tag, text) => {
  const n = document.createElement(tag);
  if (text !== undefined) n.textContent = text;
  return n;
};
const gb = (n) =>
  n == null
    ? "Unlimited"
    : `${(n / 1e9).toLocaleString(undefined, { maximumFractionDigits: 4 })} GB`;
const button = (text, run) => {
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
export async function storageManagement(parent, api) {
  const section = e("section");
  section.id = "storage-management";
  section.className = "panel research-panel";
  section.append(
    e("h2", "Storage & Data · Storage Management"),
    e(
      "p",
      "1 GB = 1,000,000,000 bytes. Limits suspend new writes safely; they never automatically erase completed flights, observations, scientific products, original imported sources, plans, presets or backups.",
    ),
  );
  parent.append(section);
  const overview = e("div"),
    settings = e("div"),
    cleanup = e("div"),
    history = e("div");
  overview.className = "research-grid";
  section.append(overview, settings, cleanup, history);
  let snapshot,
    previewId,
    offset = 0;
  const choices = {};
  async function load(refresh = false) {
    snapshot = await api(
      refresh ? "/api/storage/refresh" : "/api/storage/management",
      refresh ? {} : undefined,
    );
    overview.replaceChildren();
    for (const [key, title] of [
      ["flight", "Flight & Mission Storage"],
      ["science", "Scientific Data Library"],
    ]) {
      const s = snapshot[key],
        card = e("article");
      card.append(
        e("h3", title),
        e("p", s.directory),
        e("strong", s.status),
        e(
          "p",
          `${gb(s.usedBytes)} used / ${gb(s.limitBytes)} · ${s.percentage == null ? "No cap" : s.percentage.toFixed(1) + "%"} · Free disk ${gb(s.freeBytes)}`,
        ),
      );
      const progress = e("progress");
      progress.max = 100;
      progress.value = Math.min(100, s.percentage || 0);
      progress.hidden=s.percentage==null;
      progress.setAttribute("aria-label", title + " capacity");
      card.append(
        progress,
        e(
          "p",
          `Database/WAL/SHM footprint: ${gb(s.databaseBytes)}. Clearing rows may retain physical SQLite allocation.`,
        ),
      );
      card.append(
        e(
          "p",
          key === "flight"
            ? `${s.missions} recorded missions`
            : `${s.datasets} datasets · ${s.products} completed products · original sources ${gb(s.sourceBytes)} · registered caches ${gb(s.cacheBytes)}`,
        ),
      );
      const details = e("details");
      details.append(
        e("summary", "View Storage Details"),
        e("p", s.accounting),
        e(
          "p",
          `Protected backups (excluded from cap): ${gb(s.backupBytes)}. Retained processing files: ${gb(s.pendingBytes || 0)} in ${s.pendingOperations || 0} work records; retained for review, never automatic cleanup. In-progress budget reservations: ${gb(s.reservedBytes||0)} (not a claim of actual used bytes). Unrelated files are not classified as deletable content. Usage checked ${snapshot.checkedUtc}.`,
        ),
      );
      if (s.error) details.append(e("p", s.error));
      card.append(details);
      overview.append(card);
    }
  }
  await load();
  for (const [key, label] of [
    ["missionLimitBytes", "Flight/Mission maximum"],
  ]) {
    const row = e("label", label + " "),
      select = e("select"),
      custom = e("input");
    custom.type = "number";
    custom.min = 0.001;
    custom.step = 0.001;
    custom.setAttribute("aria-label", label + " custom GB");
    select.setAttribute("aria-label", label + " preset");
    for (const [id, text] of [
      ["25", "25 GB"],
      ["50", "50 GB"],
      ["100", "100 GB"],
      ["250", "250 GB"],
      ["unlimited", "Unlimited"],
      ["custom", "Custom GB"],
    ]) {
      const o = e("option", text);
      o.value = id;
      select.append(o);
    }
    const current = snapshot.preferences[key];
    select.value =
      current == null
        ? "unlimited"
        : [25, 50, 100, 250].includes(current / 1e9)
          ? String(current / 1e9)
          : "custom";
    custom.value = current == null ? 50 : current / 1e9;
    const changed = () => (custom.hidden = select.value !== "custom");
    select.onchange = changed;
    changed();
    row.append(select, custom);
    settings.append(row);
    choices[key] = () =>
      select.value === "unlimited"
        ? null
        : Math.round(
            Number(select.value === "custom" ? custom.value : select.value) *
              1e9,
          );
  }
  settings.append(e("p","Scientific Library capacity: available physical storage only. Per-download allowances, concurrent reservations and the recovery safety reserve still apply; there is no total source-count or Library-size quota."));
  const automatic = e("input");
  automatic.type = "checkbox";
  automatic.checked = snapshot.preferences.automaticCacheCleanup;
  const autoLabel = e(
    "label",
    " Automatically clean verified regenerable caches only",
  );
  autoLabel.prepend(automatic);
  settings.append(
    autoLabel,
    e(
      "p",
      "Pinned missions/datasets: protected. Automatic deletion of completed flights and original scientific data: disabled.",
    ),
    button("Save storage limits", async () => {
      await api("/api/storage/preferences", {
        missionLimitBytes: choices.missionLimitBytes(),
        scienceLimitBytes: null,
        automaticCacheCleanup: automatic.checked,
        protectPinned: true,
      });
      await load();
    }),
    button("Refresh usage", () => load(true)),
    button("Open Data Management", () =>
      api("/api/desktop/data-management", {}),
    ),
    button("Compact Flight Database…", () => api("/api/desktop/compact-flight", {})),
    button("Compact Science Database…", () => api("/api/desktop/compact-science", {})),
  );
  const filter = e("input"),
    sort = e("select"),
    status = e("select");
  filter.placeholder = "Filter mission, dataset, category or filename";
  filter.setAttribute("aria-label", "Cleanup filter");
  sort.setAttribute("aria-label", "Cleanup sort");
  status.setAttribute("aria-label", "Cleanup status");
  for (const [id, text] of [
    ["created", "Created date"],
    ["age", "Age"],
    ["size", "Size (largest first)"],
    ["mission", "Mission grouping"],
    ["dataset", "Dataset grouping"],
    ["category", "Category"],
  ]) {
    const o = e("option", text);
    o.value = id;
    sort.append(o);
  }
  for (const value of ["", "Eligible", "Protected", "In Use", "Excluded"]) {
    const o = e("option", value || "All statuses");
    o.value = value;
    status.append(o);
  }
  const summary = e("p"),
    tableWrap = e("div"),
    result = e("p");
  tableWrap.className = "cleanup-table-wrap";
  cleanup.append(
    e("h3", "Preview Cleanup"),
    e(
      "p",
      "Only positively registered, reproducible scientific thumbnails qualify. Mission recordings, original imagery, measurements and completed research products are retained. Unregistered temporary/orphan files are excluded for recovery review.",
    ),
    button("Preview Cleanup", preview),
    button("Refresh Preview", preview),
    filter,
    sort,
    status,
    summary,
    tableWrap,
  );
  async function preview() {
    const p = await api("/api/storage/cleanup/preview", {});
    previewId = p.id;
    offset = 0;
    await page();
  }
  async function page() {
    if (!previewId) return;
    const p = await api(
      `/api/storage/cleanup/${previewId}?offset=${offset}&sort=${encodeURIComponent(sort.value)}&filter=${encodeURIComponent(filter.value)}&status=${encodeURIComponent(status.value)}`,
    );
    summary.textContent = `Regenerable caches only · ${p.eligibleFiles} eligible files · estimated ${gb(p.estimatedBytes)} · oldest created ${p.oldestUtc || "None"} · newest ${p.newestUtc || "None"} · current science usage ${gb(p.currentUsage)} → expected ${gb(p.expectedUsageAfter)}. Preview ${p.createdUtc}. ${p.total} matching items; showing up to 100.`;
    const table = e("table"),
      head = e("thead"),
      tr = e("tr");
    for (const name of [
      "File / Item",
      "Category",
      "Mission",
      "Dataset / Product",
      "Created UTC / Age",
      "Size",
      "Cleanup Reason",
      "Status",
    ])
      tr.append(e("th", name));
    head.append(tr);
    table.append(head);
    const body = e("tbody");
    for (const row of p.items) {
      const tr = e("tr"),
        file = e("td"),
        detail = e("details");
      detail.append(
        e("summary", row.cache.relativePath.split("/").at(-1)),
        e("p", row.cache.relativePath),
        e(
          "p",
          `Depends on retained product ${row.cache.productId}. Recipe: ${row.cache.recipe}. Last-used ${row.cache.lastUsedUtc}. SHA-256 ${row.cache.sha256}`,
        ),
      );
      file.append(detail);
      tr.append(file);
      for (const value of [
        row.category,
        row.missionName,
        `${row.cache.datasetId} / ${row.cache.productId}`,
        `${row.cache.createdUtc} / ${row.ageDays.toFixed(1)} days`,
        gb(row.cache.bytes),
        row.reason,
        row.status,
      ])
        tr.append(e("td", value));
      body.append(tr);
    }
    table.append(body);
    tableWrap.replaceChildren(table);
  }
  let timer;
  filter.oninput = () => {
    clearTimeout(timer);
    timer = setTimeout(() => {
      offset = 0;
      page();
    }, 250);
  };
  sort.onchange = status.onchange = () => {
    offset = 0;
    page();
  };
  cleanup.append(
    button("Previous cleanup page", async () => {
      offset = Math.max(0, offset - 100);
      await page();
    }),
    button("Next cleanup page", async () => {
      offset += 100;
      await page();
    }),
    button("Clean Eligible Files", async () => {
      if (!previewId) throw Error("Preview cleanup first.");
      const p = await api(`/api/storage/cleanup/${previewId}`);
      if (!p.eligibleFiles) {
        result.textContent = "No eligible cache files.";
        return;
      }
      if (
        !confirm(
          `Remove all ${p.eligibleFiles} eligible regenerable cache files from this preview (including any hidden by your filter)? Estimated ${gb(p.estimatedBytes)}. Original research, completed products, protected/in-use files and backups will remain. Each candidate is revalidated.`,
        )
      )
        return;
      const r = await api("/api/storage/cleanup", {
        previewId,
        confirmed: true,
      });
      result.textContent = `Removed ${r.removed}; skipped ${r.skipped}; failed ${r.failed}. Actual bytes removed: ${r.actualBytesRemoved}. Measured free-space change: ${r.diskSpaceReclaimed ?? "unavailable"} bytes (other disk activity may affect this).`;
      previewId = null;
      tableWrap.replaceChildren();
      await load(true);
      await historyRows();
    }),
    button("Cancel", async () => {
      previewId = null;
      tableWrap.replaceChildren();
      summary.textContent = "Cleanup cancelled; no files were removed.";
    }),
    result,
  );
  async function historyRows() {
    const rows = await api("/api/storage/cleanup/history");
    history.replaceChildren(e("h3", "Local cleanup history"));
    for (const row of rows) {
      const d = e("details");
      d.append(
        e(
          "summary",
          `${row.completedUtc}: ${row.removed} removed, ${row.skipped} skipped, ${row.failed} failed; ${row.actualBytesRemoved} actual bytes removed`,
        ),
      );
      for (const note of row.messages) d.append(e("p", note));
      history.append(d);
    }
  }
  await historyRows();
}
