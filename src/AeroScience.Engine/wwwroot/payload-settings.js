export function payloadSettings(parent, config, catalog) {
  const profiles = [
    ...(catalog?.generic || []),
    ...(catalog?.profiles || []),
  ].filter((p) => p.instrumentId === config.instrumentId);
  if (!profiles.length) return () => config.payload ?? null;
  const label = document.createElement("label");
  label.textContent = "Sensor profile";
  const select = document.createElement("select");
  select.setAttribute("aria-label", "Sensor profile");
  label.append(select);
  parent.append(label);
  let payload = structuredClone(
    config.payload ?? {
      profile: profiles[0],
      forwardDegrees: 0,
      rightDegrees: 0,
      custom: false,
      geometryAlgorithm: "sensor-plane-2",
    },
  );
  if (!profiles.some((p) => p.id === payload.profile.id))
    profiles.push(payload.profile);
  for (const p of profiles) {
    const o = document.createElement("option");
    o.value = p.id;
    o.textContent = p.name;
    select.append(o);
  }
  select.value = payload.profile.id;
  const info = document.createElement("details"),
    summary = document.createElement("summary"),
    content = document.createElement("div");
  summary.textContent = "Geometry, mount and documented limitations";
  info.append(summary, content);
  parent.append(info);
  const editor = (name, value, minimum, maximum) => {
    const l = document.createElement("label");
    l.textContent = name;
    const input = document.createElement("input");
    input.type = "number";
    input.step = "any";
    input.min = minimum;
    input.max = maximum;
    input.value = value??'';
    input.setAttribute("aria-label", name);
    l.append(input);
    content.append(l);
    return input;
  };
  let h, v, forward, right, custom,crossPixels,alongPixels,lineRate,measurementRate;
  function render() {
    content.replaceChildren();
    const description = document.createElement("p");
    description.textContent = payload.profile.limitations.join(" ");
    content.append(description);
    const customLabel = document.createElement("label");
    custom = document.createElement("input");
    custom.type = "checkbox";
    custom.checked = payload.custom;
    customLabel.append(
      custom,
      " Customize geometry (identified as a custom configuration)",
    );
    content.append(customLabel);
    h = editor(
      "Horizontal field of view (degrees)",
      payload.profile.horizontalFovDegrees,
      0.001,
      119.99,
    );
    v = editor(
      "Along-track angular envelope (degrees)",
      payload.profile.verticalFovDegrees,
      0.001,
      119.99,
    );
    crossPixels=editor('Cross-track detector elements (optional)',payload.profile.crossTrackPixels,1,100000);
    alongPixels=editor('Along-track detector elements (optional)',payload.profile.alongTrackPixels,1,100000);
    lineRate=editor('Nominal pushbroom line rate (Hz; optional)',payload.profile.lineRateHz,.01,100000000);
    measurementRate=editor('Effective scanner measurement rate (Hz; optional)',payload.profile.measurementRateHz,.01,100000000);
    forward = editor(
      "Mount forward tilt (degrees)",
      payload.forwardDegrees ?? 0,
      -60,
      60,
    );
    right = editor(
      "Mount right tilt (degrees)",
      payload.rightDegrees ?? 0,
      -60,
      60,
    );
    const lock = () => {
      for(const field of [h,v,crossPixels,alongPixels,lineRate,measurementRate])field.disabled=!custom.checked;
    };
    custom.onchange = lock;
    lock();
    for (const url of payload.profile.references) {
      const a = document.createElement("a");
      a.href = url;
      a.textContent = "Official reference";
      a.target = "_blank";
      a.rel = "noopener noreferrer";
      content.append(a, document.createTextNode(" "));
    }
  }
  select.onchange = () => {
    payload = {
      profile: structuredClone(profiles.find((p) => p.id === select.value)),
      forwardDegrees: Number(forward.value),
      rightDegrees: Number(right.value),
      custom: false,
      geometryAlgorithm: "sensor-plane-2",
    };
    render();
  };
  render();
  return () => ({
    ...payload,
    custom: custom.checked,
    forwardDegrees: Number(forward.value),
    rightDegrees: Number(right.value),
    profile: {
      ...payload.profile,
      horizontalFovDegrees: Number(h.value),
      verticalFovDegrees: Number(v.value),
      crossTrackPixels:crossPixels.value===''?null:Number(crossPixels.value),
      alongTrackPixels:alongPixels.value===''?null:Number(alongPixels.value),
      lineRateHz:lineRate.value===''?null:Number(lineRate.value),
      measurementRateHz:measurementRate.value===''?null:Number(measurementRate.value),
      pixelPitchMicrometres:custom.checked?null:payload.profile.pixelPitchMicrometres,
      focalLengthMillimetres:custom.checked?null:payload.profile.focalLengthMillimetres,
    },
  });
}
