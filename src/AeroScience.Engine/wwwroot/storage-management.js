import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
const e = (tag, text) => {
  const n = document.createElement(tag);
  if (text !== undefined) setPresentationText(n,text);
  return n;
};
const gb = (n) =>
  n == null
    ? textForUI("ui.downloadActivity.messageUnlimited")
    : textForUI("ui.storageManagement.messageGB",{value1:(String((n / 1e9).toLocaleString(undefined, { maximumFractionDigits: 4 })))});
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
    e("h2", localizedCopy("ui.storageManagement.messageStorageDataStorageManagement")),
    e(
      "p",
      localizedCopy("ui.storageManagement.message1GB1000000000BytesLimitsSuspend"),
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
      ["flight", textForUI("ui.storageManagement.messageFlightMissionStorage")],
      ["science", textForUI("ui.app.messageScientificDataLibrary")],
    ]) {
      const s = snapshot[key],
        card = e("article");
      card.append(
        e("h3", title),
        e("p", s.directory),
        e("strong", s.status),
        e(
          "p",
          localizedCopy("ui.storageManagement.messageUsedFreeDisk",{value1:(String(gb(s.usedBytes))),value2:(String(gb(s.limitBytes))),value3:(String(s.percentage == null ? "No cap" : s.percentage.toFixed(1) + "%")),value4:(String(gb(s.freeBytes)))}),
        ),
      );
      const progress = e("progress");
      progress.max = 100;
      progress.value = Math.min(100, s.percentage || 0);
      progress.hidden=s.percentage==null;
      setPresentationText(progress,localizedCopy("ui.storageManagement.messageCapacity",{value1:(title)}),"aria-label");
      card.append(
        progress,
        e(
          "p",
          localizedCopy("ui.storageManagement.messageDatabaseWALSHMFootprintClearingRowsMayRetain",{value1:(String(gb(s.databaseBytes)))}),
        ),
      );
      card.append(
        e(
          "p",
          key === "flight"
            ? localizedCopy("ui.storageManagement.messageRecordedMissions",{value1:(String(s.missions))})
            : localizedCopy("ui.storageManagement.messageDatasetsCompletedProductsOriginal",{value1:(String(s.datasets)),value2:(String(s.products)),value3:(String(gb(s.sourceBytes))),value4:(String(gb(s.cacheBytes)))}),
        ),
      );
      const details = e("details");
      details.append(
        e("summary", localizedCopy("ui.storageManagement.messageViewStorageDetails")),
        e("p", s.accounting),
        e(
          "p",
          localizedCopy("ui.storageManagement.messageProtectedBackupsExcludedFromCapRetained",{value1:(String(gb(s.backupBytes))),value2:(String(gb(s.pendingBytes || 0))),value3:(String(s.pendingOperations || 0)),value4:(String(gb(s.reservedBytes||0))),value5:(String(snapshot.checkedUtc))}),
        ),
      );
      if (s.error) details.append(e("p", s.error));
      card.append(details);
      overview.append(card);
    }
  }
  await load();
  for (const [key, label] of [
    ["missionLimitBytes", textForUI("ui.storageManagement.messageFlightMissionMaximum")],
  ]) {
    const row = e("label", label + " "),
      select = e("select"),
      custom = e("input");
    custom.type = "number";
    custom.min = 0.001;
    custom.step = 0.001;
    setPresentationText(custom,localizedCopy("ui.storageManagement.messageCustomGBd2c1ee6f",{value1:(label)}),"aria-label");
    setPresentationText(select,localizedCopy("ui.storageManagement.messagePreset",{value1:(label)}),"aria-label");
    for (const [id, text] of [
      ["25", "25 GB"],
      ["50", "50 GB"],
      ["100", "100 GB"],
      ["250", "250 GB"],
      ["unlimited", textForUI("ui.downloadActivity.messageUnlimited")],
      ["custom", textForUI("ui.storageManagement.messageCustomGB")],
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
  settings.append(e("p",localizedCopy("ui.storageManagement.messageScientificLibraryCapacityAvailablePhysicalStorageOnly")));
  const automatic = e("input");
  automatic.type = "checkbox";
  automatic.checked = snapshot.preferences.automaticCacheCleanup;
  const autoLabel = e(
    "label",
    localizedCopy("ui.storageManagement.messageAutomaticallyCleanVerifiedRegenerableCachesOnly"),
  );
  autoLabel.prepend(automatic);
  settings.append(
    autoLabel,
    e(
      "p",
      localizedCopy("ui.storageManagement.messagePinnedMissionsDatasetsProtectedAutomaticDeletionOfCompleted"),
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
  setPresentationText(filter,localizedCopy("ui.storageManagement.messageFilterMissionDatasetCategoryOrFilename"),"placeholder",true);
  setPresentationText(filter,localizedCopy("ui.storageManagement.messageCleanupFilter"),"aria-label");
  setPresentationText(sort,localizedCopy("ui.storageManagement.messageCleanupSort"),"aria-label");
  setPresentationText(status,localizedCopy("ui.storageManagement.messageCleanupStatus"),"aria-label");
  for (const [id, text] of [
    ["created", textForUI("ui.storageManagement.messageCreatedDate")],
    ["age", textForUI("ui.storageManagement.messageAge")],
    ["size", textForUI("ui.storageManagement.messageSizeLargestFirst")],
    ["mission", textForUI("ui.storageManagement.messageMissionGrouping")],
    ["dataset", textForUI("ui.storageManagement.messageDatasetGrouping")],
    ["category", textForUI("ui.missionTimeline.messageCategory")],
  ]) {
    const o = e("option", text);
    o.value = id;
    sort.append(o);
  }
  for (const value of ["", textForUI("ui.storageManagement.messageEligible"), textForUI("ui.storageManagement.messageProtected"), textForUI("ui.storageManagement.messageInUse"), textForUI("ui.atmosphericResearch.messageExcluded")]) {
    const o = e("option", value || localizedCopy("html.index.textAllStatuses"));
    o.value = value;
    status.append(o);
  }
  const summary = e("p"),
    tableWrap = e("div"),
    result = e("p");
  tableWrap.className = "cleanup-table-wrap";
  cleanup.append(
    e("h3", localizedCopy("ui.storageManagement.messagePreviewCleanup")),
    e(
      "p",
      localizedCopy("ui.storageManagement.messageOnlyPositivelyRegisteredReproducibleScientificThumbnailsQualify"),
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
    setPresentationText(summary,localizedCopy("ui.storageManagement.messageRegenerableCachesOnlyEligibleFiles",{value1:(String(p.eligibleFiles)),value2:(String(gb(p.estimatedBytes))),value3:(String(p.oldestUtc || "None")),value4:(String(p.newestUtc || "None")),value5:(String(gb(p.currentUsage))),value6:(String(gb(p.expectedUsageAfter))),value7:(String(p.createdUtc)),value8:(String(p.total))}));
    const table = e("table"),
      head = e("thead"),
      tr = e("tr");
    for (const name of [
      textForUI("ui.storageManagement.messageFileItem"),
      textForUI("ui.missionTimeline.messageCategory"),
      textForUI("toolbar.mission"),
      textForUI("ui.storageManagement.messageDatasetProduct"),
      textForUI("ui.storageManagement.messageCreatedUTCAge"),
      textForUI("ui.storageManagement.messageSize"),
      textForUI("ui.storageManagement.messageCleanupReason"),
      textForUI("ui.receivedEvidenceUi.messageStatus"),
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
          localizedCopy("ui.storageManagement.messageDependsOnRetainedProductRecipe",{value1:(String(row.cache.productId)),value2:(String(row.cache.recipe)),value3:(String(row.cache.lastUsedUtc)),value4:(String(row.cache.sha256))}),
        ),
      );
      file.append(detail);
      tr.append(file);
      for (const value of [
        row.category,
        row.missionName,
        `${row.cache.datasetId} / ${row.cache.productId}`,
        textForUI("ui.storageManagement.messageDays",{value1:(String(row.cache.createdUtc)),value2:(String(row.ageDays.toFixed(1)))}),
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
      if (!previewId) throw Error(textForUI("ui.storageManagement.messagePreviewCleanupFirst"));
      const p = await api(`/api/storage/cleanup/${previewId}`);
      if (!p.eligibleFiles) {
        setPresentationText(result,localizedCopy("ui.storageManagement.messageNoEligibleCacheFiles"));
        return;
      }
      if (
        !confirm(
          localizedCopy("ui.storageManagement.messageRemoveAllEligibleRegenerableCacheFilesFrom",{value1:(String(p.eligibleFiles)),value2:(String(gb(p.estimatedBytes)))}),
        )
      )
        return;
      const r = await api("/api/storage/cleanup", {
        previewId,
        confirmed: true,
      });
      setPresentationText(result,localizedCopy("ui.storageManagement.messageRemovedSkippedFailedActual",{value1:(String(r.removed)),value2:(String(r.skipped)),value3:(String(r.failed)),value4:(String(r.actualBytesRemoved)),value5:(String(r.diskSpaceReclaimed ?? "unavailable"))}));
      previewId = null;
      tableWrap.replaceChildren();
      await load(true);
      await historyRows();
    }),
    button("Cancel", async () => {
      previewId = null;
      tableWrap.replaceChildren();
      setPresentationText(summary,localizedCopy("ui.storageManagement.messageCleanupCancelledNoFilesWereRemoved"));
    }),
    result,
  );
  async function historyRows() {
    const rows = await api("/api/storage/cleanup/history");
    history.replaceChildren(e("h3", localizedCopy("ui.storageManagement.messageLocalCleanupHistory")));
    for (const row of rows) {
      const d = e("details");
      d.append(
        e(
          "summary",
          localizedCopy("ui.storageManagement.messageRemovedSkippedFailedActualBytes",{value1:(String(row.completedUtc)),value2:(String(row.removed)),value3:(String(row.skipped)),value4:(String(row.failed)),value5:(String(row.actualBytesRemoved))}),
        ),
      );
      for (const note of row.messages) d.append(e("p", note));
      history.append(d);
    }
  }
  await historyRows();
}
