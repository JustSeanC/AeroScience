import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
const e = (tag, text) => {
  const n = document.createElement(tag);
  if (text !== undefined) setPresentationText(n,text);
  return n;
};
export async function updateSettings(parent, api) {
  const section = e("section"),
    data = await api("/api/updates");
  section.className = "panel research-panel";
  section.append(
    e("h2", localizedCopy("ui.applicationUpdates.messageUpdates")),
    e(
      "p",
      localizedCopy("ui.applicationUpdates.messageCurrentApplicationToolbarUpdatesRemainSeparate",{value1:(String(data.currentVersion))}),
    ),
    e(
      "p",
      localizedCopy("ui.applicationUpdates.messageOptionalChecksContactTheOfficialPublicGitHub"),
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
      localizedCopy("ui.applicationUpdates.messageLastChecked",{value1:(String(data.message || "Not checked yet")),value2:(String(data.lastCheckedUtc || "Never"))}),
    ),
    save = e("button", localizedCopy("ui.applicationUpdates.messageSaveUpdatePreferences")),
    check = e("button", localizedCopy("ui.applicationUpdates.messageCheckForUpdates"));
  save.className=check.className="secondary";
  save.onclick = async () => {
    try {
      await api("/api/updates/preferences", {
        automatic: automatic.checked,
        includePrereleases: pre.checked,
      });
      setPresentationText(status,localizedCopy("ui.applicationUpdates.messageUpdatePreferencesSaved"));
    } catch (e) {
      setPresentationText(status,e.message);
    }
  };
  check.onclick = async () => {
    check.disabled = true;
    try {
      const r = await api("/api/updates/check", {});
      setPresentationText(status,localizedCopy("ui.applicationUpdates.messageLastChecked",{value1:(String(r.message)),value2:(String(r.lastCheckedUtc))}));
      await notifyUpdates(api);
    } catch (e) {
      setPresentationText(status,e.message);
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
        localizedCopy("ui.applicationUpdates.messageAeroScienceIsAvailableCurrent",{value1:(data.available.version),value2:(data.currentVersion)}),
      ),
    );
    for (const title of [textForUI("ui.applicationUpdates.messageViewRelease"), textForUI("ui.applicationUpdates.messageDownloadUpdate")]) {
      const a = e("a", title);
      a.href = data.available.releaseUrl;
      a.target = "_blank";
      a.rel = "noopener noreferrer";
      banner.append(a);
    }
    banner.append(
      e(
        "p",
        localizedCopy("ui.applicationUpdates.messageDownloadManuallyFromTheOfficialReleasePage"),
      ),
    );
    const later = e("button", localizedCopy("ui.applicationUpdates.messageRemindMeLater"));
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
