import {presentationTextNode} from './i18n.js';
import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {openOnlineAccess} from './online-access.js';
import {openAviationReference} from './aviation-reference.js';
import {dialogHeader,requestDialogClose,markDialogSaved} from './dialog-ui.js';
const layers = [
  ["offline", localizedCopy("ui.basemaps.messageOfflineGeographicMap")],
  ["streets", "OpenStreetMap / Streets"],
  ["satellite", localizedCopy("ui.basemaps.messageSatelliteImagery")],
  ["satellite-labels", localizedCopy("ui.basemaps.messageSatelliteLabels")],
  ["esri-outdoor", localizedCopy("ui.basemaps.messageEsriOutdoorTopographicContext")],
  ["esri-navigation", localizedCopy("ui.basemaps.messageEsriNavigationRoadContext")],
];
let csrf = "",
  configuration,
  loading,
  sessionReady = false;
const listeners = new Set();
let privacyDialog;
async function firstRunPrivacy(config) {
  if (!config.configuration.requiresPrivacyChoice || privacyDialog) return;
  const refresh=async()=>{configuration=null;await settings();notify();};
  privacyDialog=openOnlineAccess(api,{
    onAllowed:refresh,
    onClosed:()=>{privacyDialog=null;},
    onOffline:async()=>{await api('/api/maps/privacy',{online:false});await refresh();}
  });
}
async function api(path, value) {
  if (value !== undefined && !csrf) {
    const response = await fetch("/api/session", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: "{}",
    });
    if (!response.ok) throw Error(textForUI("ui.basemaps.messageOpenThePairedDashboardSessionFirst"));
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
  if (!r.ok) throw Error(data.error || textForUI("ui.basemaps.messageMapSettingsUnavailable"));
  return data;
}
const element = (tag, text) => {
  const e = document.createElement(tag);
  if (text !== undefined) setPresentationText(e,text);
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
  const wrap = element("label", localizedCopy("ui.basemaps.messageBasemap")),
    select = element("select"),
    message = element("p");
  message.className = "footnote map-layer-status";
  setPresentationText(select,localizedCopy("ui.basemaps.messageMapLayersBasemap"),"aria-label");
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
      message.replaceChildren();
      if(client.error){message.append(presentationTextNode(client.error+" "));if(["satellite","satellite-labels","esri-outdoor","esri-navigation"].includes(select.value)){const a=element("a",localizedCopy("ui.basemaps.messageConfigureEsriAPIKey"));a.href="#settings/maps";message.append(a);}}

    } catch (e) {
      if (own === generation) {
        changed({ layer: "offline", tiles: [] });
        setPresentationText(message,e.message);
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
      setPresentationText(message,e.message);
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
    title = element("h2", localizedCopy("ui.basemaps.messageMapsBasemaps"));
  panel.append(title);
  const faa=element('button',localizedCopy("ui.aviationReference.messageFAACatalogNotices"));faa.type='button';faa.className='secondary';faa.onclick=()=>openAviationReference(api);panel.append(faa);
  parent.append(panel);
  const data = await request("/api/maps/settings"),
    current = data.configuration;
  panel.append(element("p", data.privacy));
  panel.append(
    element(
      "p",
      localizedCopy("ui.basemaps.messageEsriImageryOutdoorAndNavigationUseYour"),
    ),
  );
  const online = element("input");
  online.type = "checkbox";
  online.checked = current.preferences.onlineEnabled;
  const onlineLabel = element("label", localizedCopy("ui.basemaps.messageEnableOnlineMaps"));
  onlineLabel.prepend(online);
  panel.append(onlineLabel);
  const provider = element("select");
  setPresentationText(provider,localizedCopy("ui.basemaps.messageSatelliteProvider"),"aria-label");
  const providerOption = element("option", localizedCopy("ui.basemaps.messageEsriArcGISLocationPlatform"));
  providerOption.value = "esri";
  provider.append(providerOption);
  panel.append(provider);
  const key = element("input");
  key.type = "password";
  key.autocomplete = "new-password";
  setPresentationText(key,current.keyConfigured
    ? localizedCopy("ui.basemaps.messageKeySavedLeaveBlankToKeep")
    : localizedCopy("ui.basemaps.messageYourPublicEsriBasemapAPIKey"),"placeholder",true);
  setPresentationText(key,localizedCopy("ui.basemaps.messagePublicEsriAPIKey"),"aria-label");
  panel.append(key);
  const remove = element("input");
  remove.type = "checkbox";
  const removeLabel = element("label", localizedCopy("ui.basemaps.messageRemoveSavedKey"));
  removeLabel.prepend(remove);
  panel.append(removeLabel);
  const select = element("select");
  setPresentationText(select,localizedCopy("ui.basemaps.messagePreferredMapLayer"),"aria-label");
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
    localizedCopy("ui.basemaps.messageIUnderstandThePrivacyDisclosureAnd"),
  );
  consentLabel.prepend(consent);
  panel.append(consentLabel);
  const status = element(
      "p",
      current.warning || localizedCopy("ui.basemaps.messageOfflineMapsWorkWithoutAProviderAccount"),
    ),
    save = element("button", localizedCopy("ui.basemaps.messageSaveMapSettings")),
    test = element("button", localizedCopy("ui.basemaps.messageTestSavedSatelliteConnection"));
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
      setPresentationText(status,localizedCopy("ui.basemaps.messageMapSettingsSavedSelectingOfflineSendsNo"));
    } catch (e) {
      setPresentationText(status,e.message);
    } finally {
      save.disabled = false;
    }
  };
  test.onclick = async () => {
    test.disabled = true;
    try {
      const r = await request("/api/maps/test", {});
      setPresentationText(status,r.error ||
        localizedCopy("ui.basemaps.messageAuthenticatedImageryMetadataLoadedVerifyVisibleTiles"));
    } catch (e) {
      setPresentationText(status,e.message);
    } finally {
      test.disabled = false;
    }
  };
  panel.append(save, test, status);
  if (!data.canConfigure)
    panel.append(
      element(
        "p",
        localizedCopy("ui.basemaps.messageConfigureProviderCredentialsInSettingsOnThe"),
      ),
    );
  for (const [text, url] of [
    [
      textForUI("ui.basemaps.messageEsriPricingAndAccountControls"),
      "https://location.arcgis.com/pricing/",
    ],
    [
      textForUI("ui.basemaps.messageEsriAttributionAndUsageTerms"),
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

export {api as mapRequest};
