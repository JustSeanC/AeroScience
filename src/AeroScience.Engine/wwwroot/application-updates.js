const e = (tag, text) => {
  const n = document.createElement(tag);
  if (text !== undefined) n.textContent = text;
  return n;
};
export async function updateSettings(parent, api) {
  const section = e("section"),
    data = await api("/api/updates");
  section.className = "panel research-panel";
  section.append(
    e("h2", "Updates"),
    e(
      "p",
      `Current application: ${data.currentVersion}. Toolbar updates remain separate under MSFS Toolbar settings.`,
    ),
    e(
      "p",
      "Optional checks contact the official public GitHub Releases API at most once per day automatically. GitHub receives the request IP and application identification; no flight data are submitted. Updates are never executed automatically.",
    ),
  );
  parent.append(section);
  const automatic = e("input"),
    pre = e("input");
  automatic.type = pre.type = "checkbox";
  automatic.checked = data.preferences.automatic;
  pre.checked = data.preferences.includePrereleases;
  for (const [node, text] of [
    [automatic, " Automatic update notifications"],
    [pre, " Include alpha / prerelease versions"],
  ]) {
    const label = e("label", text);
    label.prepend(node);
    section.append(label);
  }
  const status = e(
      "p",
      `${data.message || "Not checked yet"} Last checked: ${data.lastCheckedUtc || "Never"}`,
    ),
    save = e("button", "Save update preferences"),
    check = e("button", "Check for Updates");
  save.className=check.className="secondary";
  save.onclick = async () => {
    try {
      await api("/api/updates/preferences", {
        automatic: automatic.checked,
        includePrereleases: pre.checked,
      });
      status.textContent = "Update preferences saved.";
    } catch (e) {
      status.textContent = e.message;
    }
  };
  check.onclick = async () => {
    check.disabled = true;
    try {
      const r = await api("/api/updates/check", {});
      status.textContent = `${r.message} Last checked: ${r.lastCheckedUtc}`;
      await notifyUpdates(api);
    } catch (e) {
      status.textContent = e.message;
    } finally {
      check.disabled = false;
    }
  };
  section.append(save, check, status);
}
export async function notifyUpdates(api) {
  try {
    const data = await api("/api/updates"),
      old = document.getElementById("application-update-notice");
    if(old?.dataset.version===data.available?.version&&(!data.remindAfterUtc||Date.parse(data.remindAfterUtc)<=Date.now()))return;
    old?.remove();
    if (
      !data.available ||
      (data.remindAfterUtc && Date.parse(data.remindAfterUtc) > Date.now())
    )
      return;
    const banner = e("aside");
    banner.id = "application-update-notice";
    banner.dataset.version=data.available.version;
    banner.className = "panel update-notice";
    banner.setAttribute("role", "status");
    banner.append(
      e(
        "strong",
        `AeroScience ${data.available.version} is available (current ${data.currentVersion})`,
      ),
    );
    for (const title of ["View Release", "Download Update"]) {
      const a = e("a", title);
      a.href = data.available.releaseUrl;
      a.target = "_blank";
      a.rel = "noopener noreferrer";
      banner.append(a);
    }
    banner.append(
      e(
        "p",
        "Download manually from the official release page. AeroScience will not run an installer automatically.",
      ),
    );
    const later = e("button", "Remind Me Later");
    later.className="secondary";
    later.onclick = async () => {
      await api("/api/updates/remind", {});
      banner.remove();
    };
    banner.append(later);
    document.getElementById("workspace").prepend(banner);
  } catch {
    /* Offline checks never block the workspace. */
  }
}
