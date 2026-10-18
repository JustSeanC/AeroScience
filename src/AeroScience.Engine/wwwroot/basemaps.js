const layers = [
  ["offline", "Offline Geographic Map"],
  ["streets", "OpenStreetMap / Streets"],
  ["satellite", "Satellite Imagery"],
  ["satellite-labels", "Satellite + Labels"],
];
let csrf = "",
  configuration,
  loading,
  sessionReady = false;
const listeners = new Set();
let privacyDialog;
async function firstRunPrivacy(config) {
  if (!config.configuration.requiresPrivacyChoice || privacyDialog) return;
  privacyDialog = element("dialog");
  privacyDialog.setAttribute("aria-label", "Choose your map privacy");
  privacyDialog.append(
    element("h2", "Choose your map privacy"),
    element(
      "p",
      "OpenStreetMap / Streets gives you recognizable maps without an account or API key. Tile requests reveal your viewed geographic region and IP address to the map provider. Offline-only maps send no tile requests.",
    ),
  );
  for (const [text, online] of [
    ["Continue with OpenStreetMap", true],
    ["Use offline-only maps", false],
  ]) {
    const button = element("button", text);
    button.className=online?"primary":"secondary";
    button.onclick = async () => {
      try {
        await api("/api/maps/privacy", { online });
        privacyDialog.close();
        privacyDialog.remove();
        privacyDialog = null;
        configuration = null;
        await settings();
        notify();
      } catch (e) {
        privacyDialog.append(element("p", e.message));
      }
    };
    privacyDialog.append(button);
  }
  privacyDialog.addEventListener("cancel", (e) => e.preventDefault());
  document.body.append(privacyDialog);
  privacyDialog.showModal();
}
async function api(path, value) {
  if (value !== undefined && !csrf) {
    const response = await fetch("/api/session", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: "{}",
    });
    if (!response.ok) throw Error("Open the paired dashboard session first.");
    csrf = (await response.json()).csrf;
  }
  const r = await fetch(
    path,
    value === undefined
      ? {}
      : {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
            "X-AeroScience-CSRF": csrf,
          },
          body: JSON.stringify(value),
        },
  );
  const data = await r.json();
  if (!r.ok) throw Error(data.error || "Map settings unavailable.");
  return data;
}
const element = (tag, text) => {
  const e = document.createElement(tag);
  if (text !== undefined) e.textContent = text;
  return e;
};
async function settings() {
  const result = (configuration ||= await (loading ||= api(
    "/api/maps/settings",
  ).finally(() => (loading = null))));
  await firstRunPrivacy(result);
  return result;
}
function notify() {
  for (const fn of listeners) fn();
}
let synchronizedRevision = null;
export function syncBasemaps(value) {
  if (!sessionReady || !value || value.revision === synchronizedRevision)
    return;
  synchronizedRevision = value.revision;
  if (configuration?.configuration?.revision === value.revision) return;
  configuration = null;
  settings()
    .then((config) => {
      if (config.configuration.revision !== value.revision)
        synchronizedRevision = null;
      notify();
    })
    .catch(() => {
      synchronizedRevision = null;
    });
}
export function layerSelector(parent, changed) {
  const wrap = element("label", "Map Layers "),
    select = element("select"),
    message = element("p");
  message.className = "footnote map-layer-status";
  select.setAttribute("aria-label", "Map Layers");
  for (const [id, name] of layers) {
    const o = element("option", name);
    o.value = id;
    select.append(o);
  }
  wrap.append(select);
  parent.append(wrap, message);
  let generation = 0;
  const apply = async () => {
    if (!sessionReady) return;
    const own = ++generation;
    try {
      const config = await settings();
      if (own !== generation) return;
      select.value = config.configuration.preferences.layer;
      const client = await api(
        "/api/maps/client?layer=" + encodeURIComponent(select.value),
      );
      if (own !== generation) return;
      changed(client);
      message.textContent =
        client.error ||
        (client.layer === "offline"
          ? "Offline geography · no tile-provider requests"
          : "Online maps · provider attribution and billing terms apply");
    } catch (e) {
      if (own === generation) {
        changed({ layer: "offline", tiles: [] });
        message.textContent = e.message;
      }
    }
  };
  select.onchange = async () => {
    try {
      await api("/api/maps/layer", { layer: select.value });
      configuration = null;
      await settings();
      notify();
    } catch (e) {
      message.textContent = e.message;
    }
  };
  const session = () => {
    sessionReady = true;
    configuration = null;
    apply();
  };
  listeners.add(apply);
  window.addEventListener("aeroscience-session", session);
  apply();
  return () => {
    generation++;
    listeners.delete(apply);
    window.removeEventListener("aeroscience-session", session);
  };
}
export async function mapSettingsUI(parent, request) {
  const panel = element("section"),
    title = element("h2", "Maps & Basemaps");
  panel.append(title);
  parent.append(panel);
  const data = await request("/api/maps/settings"),
    current = data.configuration;
  panel.append(element("p", data.privacy));
  panel.append(
    element(
      "p",
      "Satellite uses your own ArcGIS Location Platform public basemap API key. Use minimum basemap scopes and appropriate URL restrictions; localhost and paired LAN origins may need separate restrictions. Client tokens are visible to authorized browsers and the provider. Never enter privileged server credentials. Keys are protected with Windows DPAPI at rest.",
    ),
  );
  const online = element("input");
  online.type = "checkbox";
  online.checked = current.preferences.onlineEnabled;
  const onlineLabel = element("label", " Enable online maps");
  onlineLabel.prepend(online);
  panel.append(onlineLabel);
  const provider = element("select");
  provider.setAttribute("aria-label", "Satellite provider");
  const providerOption = element("option", "Esri ArcGIS Location Platform");
  providerOption.value = "esri";
  provider.append(providerOption);
  panel.append(provider);
  const key = element("input");
  key.type = "password";
  key.autocomplete = "new-password";
  key.placeholder = current.keyConfigured
    ? "Key saved — leave blank to keep"
    : "Your public Esri basemap API key";
  key.setAttribute("aria-label", "Public Esri API key");
  panel.append(key);
  const remove = element("input");
  remove.type = "checkbox";
  const removeLabel = element("label", " Remove saved key");
  removeLabel.prepend(remove);
  panel.append(removeLabel);
  const select = element("select");
  select.setAttribute("aria-label", "Preferred map layer");
  for (const [id, name] of layers) {
    const o = element("option", name);
    o.value = id;
    select.append(o);
  }
  select.value = current.preferences.layer;
  panel.append(select);
  const consent = element("input");
  consent.type = "checkbox";
  const consentLabel = element(
    "label",
    " I understand the privacy disclosure and provider billing/usage conditions.",
  );
  consentLabel.prepend(consent);
  panel.append(consentLabel);
  const status = element(
      "p",
      current.warning || "Offline maps work without a provider account.",
    ),
    save = element("button", "Save map settings"),
    test = element("button", "Test saved satellite connection");
  save.type = test.type = "button";
  save.disabled = test.disabled = !data.canConfigure;
  save.onclick = async () => {
    save.disabled = true;
    try {
      await request("/api/maps/settings", {
        onlineEnabled: online.checked,
        layer: online.checked ? select.value : "offline",
        provider: "esri",
        publicApiKey: key.value || null,
        removeKey: remove.checked,
        privacyAccepted: consent.checked,
      });
      key.value = "";
      configuration = null;
      await settings();
      notify();
      status.textContent =
        "Map settings saved. Selecting Offline sends no background tile requests.";
    } catch (e) {
      status.textContent = e.message;
    } finally {
      save.disabled = false;
    }
  };
  test.onclick = async () => {
    test.disabled = true;
    try {
      const r = await request("/api/maps/test", {});
      status.textContent =
        r.error ||
        "Authenticated imagery metadata loaded. Verify visible tiles and attribution on the map; requests may be billable.";
    } catch (e) {
      status.textContent = e.message;
    } finally {
      test.disabled = false;
    }
  };
  panel.append(save, test, status);
  if (!data.canConfigure)
    panel.append(
      element(
        "p",
        "Configure provider credentials in Settings on the Windows companion. Paired browsers can use the authorized public client token.",
      ),
    );
  for (const [text, url] of [
    [
      "Esri pricing and account controls",
      "https://location.arcgis.com/pricing/",
    ],
    [
      "Esri attribution and usage terms",
      "https://developers.arcgis.com/documentation/esri-and-data-attribution/",
    ],
    [
      "OpenStreetMap tile usage policy",
      "https://operations.osmfoundation.org/policies/tiles/",
    ],
  ]) {
    const a = element("a", text);
    a.href = url;
    a.target = "_blank";
    a.rel = "noopener noreferrer";
    panel.append(a, element("br"));
  }
}
