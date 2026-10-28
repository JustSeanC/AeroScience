/* Simulator-native UI. SDK globals are provided by MSFS, not a normal web browser.
 * See docs/toolbar.md for SDK sources, provenance, and outstanding simulator checks. */
class IngamePanelAeroScience extends TemplateElement {
  connectedCallback() {
    super.connectedCallback();
    if (document.readyState === "loading")
      document.addEventListener("DOMContentLoaded", () => this.initialize(), {
        once: true,
      });
    else this.initialize();
  }
  initialize() {
    if (this.initialized || !this.isConnected) return;
    this.initialized = true;
    this.pending = new Map();
    this.listener = null;
    this.state = null;
    this.lastReply = 0;
    this.serial=0;this.appliedSerial=0;this.seenEngines=new Set();this.intelligence=null;this.capabilities=[];this.mutationPending=false;
    this.byId = (id) => this.querySelector("#" + id);
    this.researchState=null;this.researchOffset=0;this.researchSerial=0;
    this.byId('as-research-previous').onclick=()=>{this.researchOffset=Math.max(0,this.researchOffset-(this.researchState?.pageSize||4));this.requestResearch();};
    this.byId('as-research-next').onclick=()=>{if(this.researchState?.nextOffset!=null){this.researchOffset=this.researchState.nextOffset;this.requestResearch();}};
    this.byId('as-compact').onclick=()=>{
      const compact=this.byId('as-root').classList.toggle('as-compact');
      this.byId('as-compact').setAttribute('aria-pressed',String(compact));this.byId('as-compact').textContent='Compact VR: '+(compact?'On':'Off');
      if(this.mapVisible&&this.mapRenderer)this.mapRenderer.resize();
    };
    for(const button of this.querySelectorAll('[data-science-action]'))button.onclick=()=>{
      const i=this.intelligence;if(button.disabled||!i||!this.capabilities.includes('session-bound-controls-1'))return;
      const requestId=this.requestId();
      this.send('science-control',{requestId,control:{version:1,missionId:i.missionId,requestId,expectedRevision:i.revision,
        expectedControlRevision:i.controlRevision,expectedSessionId:i.sessionId,action:button.dataset.scienceAction}});
    };
    this.onReply = (data) => this.receive(data);
    this.byId("as-start").onclick = () => {
      if (!this.byId("as-start").hasAttribute("disabled"))
        this.send("start", {
          name: this.byId("as-name").value,
          mode: this.byId("as-mode").value,
          planId: this.mapPlan || null,
        });
    };
    this.byId("as-end").onclick = () => {
      if (
        !this.byId("as-end").hasAttribute("disabled") &&
        this.state &&
        this.state.activeMission
      )
        this.send("end", { missionId: this.state.activeMission.id });
    };
    this.byId("as-marker").onclick = () => {
      if (
        this.state &&
        this.state.activeMission &&
        !this.byId("as-marker").hasAttribute("disabled")
      )
        this.send("marker", {
          missionId: this.state.activeMission.id,
          category: this.byId("as-observation-category").value,
          notes: this.byId("as-observation-note").value,
        });
    };
    this.byId("as-mode").onchange = () => {
      this.byId("as-mode-help").textContent =
        this.byId("as-mode").value === "realistic"
          ? "Evidence-based assessments depend on the companion's capabilities. Any compatible aircraft can record."
          : "All compatible aircraft can record missions.";
    };
    this.byId("as-mission-page").onclick = () => this.page(false);
    this.byId("as-payload-page").onclick = () => this.page(true);
    this.byId("as-map-page").onclick = () => this.page("map");
    this.byId("as-research-page").onclick = () => this.page("research");
    try {
      this.mapRenderer = new window.AeroScienceMovingMap(
        this.byId("as-map-canvas"),
        this.byId("as-map-waypoint-info"),
      );
    } catch (error) {
      this.mapRenderer = null;
      if (typeof console !== "undefined" && console.warn)
        console.warn(
          "AeroScience map initialization failed: " +
            String((error && error.message) || error),
        );
      this.byId("as-map-status").textContent =
        "The map renderer is unavailable in this UI context. Mission and Payload remain available; report the simulator version and Coherent console error.";
      for (const control of this.byId("as-map-view").querySelectorAll(
        "button,select",
      ))
        control.disabled = true;
    }
    this.mapVisible = !this.byId("as-map-view").hidden;
    this.lastMapAt = 0;
    this.routeItems = [];
    this.routeRevision = null;
    this.loadingRevision = null;
    this.mapCursor = null;
    this.planOffset = 0;
    this.plansLoaded=false;
    this.byId("as-plan-map").onclick=()=>this.page("map");
    this.byId("as-map-follow").onclick = () => {
      this.mapRenderer.follow = true;
      if (this.lastMapState) this.mapRenderer.update(this.lastMapState);
    };
    this.byId("as-map-free").onclick = () => {
      this.mapRenderer.follow = false;
      this.byId("as-map-status").textContent =
        "Free Pan  -  drag the map or use arrow keys. Aircraft follow is off.";
    };
    this.byId("as-map-fit").onclick = () => this.mapRenderer.fit();
    this.byId("as-map-in").onclick = () => this.mapRenderer.zoomBy(1.4);
    this.byId("as-map-out").onclick = () => this.mapRenderer.zoomBy(1 / 1.4);
    this.byId("as-map-waypoint").onchange = () => {
      this.mapRenderer.selected = Number(this.byId("as-map-waypoint").value);
      this.mapRenderer.draw();
      this.mapRenderer.describe();
      this.requestMapState();
    };
    const inspect=delta=>{const select=this.byId("as-map-waypoint");if(!select.options.length)return;select.selectedIndex=Math.max(0,Math.min(select.options.length-1,select.selectedIndex+delta));select.onchange();};
    this.byId("as-waypoint-prev").onclick=()=>inspect(-1);this.byId("as-waypoint-next").onclick=()=>inspect(1);this.byId("as-waypoint-target").onclick=()=>{const s=this.byId("as-map-waypoint");if(s.options.length){s.selectedIndex=Math.min(s.options.length-1,this.lastMapState?.nextWaypoint||0);s.onchange();}};
    this.byId("as-map-plan").onchange = () => {
      const select = this.byId("as-map-plan"),
        option = select.options[select.selectedIndex];
      this.mapPlan = select.value || null;
      this.send("select-plan",{planId:this.mapPlan});
      try{sessionStorage.setItem("aeroscience-selected-plan",JSON.stringify({id:this.mapPlan,name:option.textContent,mode:option.getAttribute("data-mode")}));}catch(e){}
      this.mapPlanName = this.mapPlan ? option.textContent : null;
      this.mapPlanMode = this.mapPlan ? option.getAttribute("data-mode") : null;
      this.byId("as-start-plan").textContent = this.mapPlan
        ? "Selected research plan: " +
          this.mapPlanName +
          ". "+(option.getAttribute("data-summary")||"Recording keeps its route, instruments and science mode.")
        : "No saved plan selected. This will be an unplanned recording.";
      this.byId("as-mode").disabled = !!this.mapPlan;
      this.byId("as-mode").value = this.mapPlanMode || "universal";
      this.byId("as-mode").onchange();
      if (this.mapPlan) this.byId("as-name").value = this.mapPlanName;
      this.routeRevision = null;
      this.loadingRevision = null;
      this.mapCursor = null;
      this.requestMapState();
    };
    try{const saved=JSON.parse(sessionStorage.getItem("aeroscience-selected-plan")||"null");if(saved?.id){this.mapPlan=saved.id;this.mapPlanName=saved.name;this.mapPlanMode=saved.mode;}}catch(e){}
    this.byId("as-map-plans").onclick = () => {
      this.planOffset = 0;
      this.send("map-plans", { offset: 0 });
    };
    this.byId("as-map-more-plans").onclick = () => {
      this.planOffset += 10;
      this.send("map-plans", { offset: this.planOffset });
    };
    this.timer = setInterval(() => this.tick(), 1000);
    try {
      Include.addScript("/JS/Services/CommBus.js", () => this.registerBus());
    } catch (error) {
      this.status(
        "Unable to load the SDK CommBus service. Check the MSFS 2024 SDK/runtime version.",
      );
    }
    // Loading order differs between simulator UI contexts. Bound the fallback wait; never create sockets.
    let attempts = 0;
    this.loadTimer = setInterval(() => {
      if (this.listener || attempts++ >= 50) {
        clearInterval(this.loadTimer);
        if (!this.listener)
          this.status("CommBus unavailable. MSFS 2024 SU5+ is required.");
      } else this.registerBus();
    }, 100);
  }
  requestMapState() {
    if (
      !this.mapVisible ||
      !this.mapRenderer ||
      Array.from(this.pending.values()).some((p) => p.op === "map-state")
    )
      return;
    if (Date.now() - this.lastMapAt > 5000) {
      this.mapCursor = null;
      this.mapRenderer.track = [];
    }
    this.send("map-state", {
      planId: this.mapPlan || null,
      after: this.mapCursor,
      waypointIndex:
        this.byId("as-map-waypoint").value === ""
          ? null
          : Number(this.byId("as-map-waypoint").value),
    });
  }
  requestMapChunk() {
    if (
      !this.isConnected ||
      !this.mapRenderer ||
      !this.mapVisible ||
      !this.loadingRevision ||
      Array.from(this.pending.values()).some((p) => p.op === "map-chunk")
    )
      return;
    this.send("map-chunk", {
      planId: this.mapPlan || null,
      revision: this.loadingRevision,
      offset: this.routeItems.length,
    });
  }
  receiveMap(op, map) {
    if (op === "map-plans") {
      const select = this.byId("as-map-plan");
      select.replaceChildren();
      const empty = document.createElement("option");
      empty.value = "";
      empty.textContent = "Unplanned Mission";
      select.appendChild(empty);
      for (const plan of map) {
        const option = document.createElement("option");
        option.value = plan.id;
        option.textContent = plan.name;
        option.setAttribute("data-mode", plan.mode);
        option.setAttribute("data-summary",(Number.isFinite(plan.waypoints)?plan.waypoints+" waypoints":"Route count unavailable")+" / "+(Number.isFinite(plan.patterns)?plan.patterns+" patterns":"pattern count unavailable")+". Instruments: "+(plan.instruments??"Not provided by this companion"));
        select.appendChild(option);
      }
      if (this.mapPlan && !map.some((p) => p.id === this.mapPlan)) {
        const keep = document.createElement("option");
        keep.value = this.mapPlan;
        keep.textContent = this.mapPlanName || "Selected research plan";
        keep.setAttribute("data-mode", this.mapPlanMode || "universal");
        select.appendChild(keep);
      }
      select.value = this.mapPlan || "";
      this.byId("as-map-more-plans").disabled=map.length<10;
      this.plansLoaded=true;
      if(this.mapPlan&&!this.state?.activeMission)select.onchange();
      if(this.mapRenderer)this.byId("as-map-status").textContent =
        "Saved plans loaded via CommBus protocol 1; engine "+(this.state?.version||"unknown")+". An active mission uses its recorded plan snapshot.";
      return;
    }
    if (op === "map-state") {
      this.lastMapAt = Date.now();
      this.byId("as-map-plan").disabled = !!map.missionId;
      const changed =
        map.revision !== this.routeRevision &&
        map.revision !== this.loadingRevision;
      if (changed) {
        this.loadingRevision = map.revision;
        this.routeItems = [];
        this.mapCursor = null;
        this.mapRenderer.route(map.revision, []);
        this.byId("as-map-waypoint").replaceChildren();
        if (map.items === 0) {
          this.routeRevision = map.revision;
          this.loadingRevision = null;
        } else this.requestMapChunk();
      }
      this.mapCursor = changed || map.hasMore ? null : map.nextSequence;
      if (map.hasMore) {
        this.mapRenderer.track = [];
        map.track = [];
      }
      this.lastMapState = map;
      this.mapRenderer.update(map);
      this.byId("as-map-status").textContent =
        (map.name ||
          "No active planned mission  -  select a saved plan or record one from the companion.") +
        "  /  " +
        (this.loadingRevision
          ? "Loading route " + this.routeItems.length + " / " + map.items
          : "Route synchronized  /  " +
            map.waypoints +
            " waypoints  /  next observed target " +
            (map.nextWaypoint < map.waypoints ? map.nextWaypoint + 1 : "end")) +
        ".";
      this.byId("as-map-progress").textContent = map.progressExplanation;
      return;
    }
    if (op === "map-chunk") {
      if (
        map.revision !== this.loadingRevision ||
        map.offset !== this.routeItems.length ||
        map.items.length > 40 ||
        map.nextOffset !== map.offset + map.items.length
      )
        return;
      if (this.routeItems.length + map.items.length > 2000) return;
      this.routeItems.push(...map.items);
      if (map.complete) {
        this.routeRevision = map.revision;
        this.loadingRevision = null;
        this.mapRenderer.route(map.revision, this.routeItems);
        if (this.lastMapState) this.mapRenderer.update(this.lastMapState);
        const select = this.byId("as-map-waypoint");
        select.replaceChildren();
        for (const p of this.routeItems.filter((p) => p.kind === "waypoint")) {
          const o = document.createElement("option");
          o.value = p.index;
          o.textContent = p.index + 1 + ". " + p.name;
          select.appendChild(o);
        }
        this.byId("as-map-status").textContent =
          "Route synchronized: " +
          select.options.length +
          " ordered waypoints. Offline geography; no online tile requests.";
      } else setTimeout(() => this.requestMapChunk(), 100);
    }
  }
  registerBus() {
    if (
      this.listener ||
      typeof RegisterCommBusListener !== "function" ||
      !this.isConnected
    )
      return;
    try {
      this.listener = RegisterCommBusListener(() =>
        this.status("Simulator channel ready; waiting for the Windows engine."),
      );
      this.listener.on("AeroScience.Reply.v1", this.onReply);
      this.tick();
    } catch (error) {
      this.status("Unable to register the SDK CommBus listener.");
    }
  }
  page(payload) {
    this.mapVisible = payload === "map";
    this.byId('as-research-cards').hidden=payload!=="research";
    for(const [id,value]of [['as-mission-page',false],['as-payload-page',true],['as-map-page','map'],['as-research-page','research']])this.byId(id).setAttribute('aria-pressed',String(payload===value));
    this.byId("as-mission-view").hidden = payload !== false;
    this.byId("as-payload-view").hidden = payload !== true;
    this.byId("as-map-view").hidden = !this.mapVisible;
    if (this.mapVisible && this.mapRenderer) {
      this.mapRenderer.resize();
      this.requestMapState();
    }
  }
  status(text) {
    this.byId("as-status").textContent = text;
  }
  requestId() {
    if (
      typeof crypto !== "undefined" &&
      typeof crypto.randomUUID === "function"
    )
      return crypto.randomUUID();
    return "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g, (c) => {
      const r = Math.floor(Math.random() * 16);
      return (c === "x" ? r : (r & 3) | 8).toString(16);
    });
  }
  send(op, fields) {
    if (!this.listener || this.pending.size >= 8) return;
    const requestId = fields?.requestId || this.requestId();
    const payload=JSON.stringify(Object.assign({protocol:1,requestId,op},fields||{}));
    if(payload.length>1024){this.status('Command exceeds the native limit. Use the dashboard.');return;}
    this.pending.set(requestId, {
      op: op,
      sent: Date.now(),
      planId: (fields && fields.planId) || null,
      observationText:op==='marker'?fields?.notes:null,serial:++this.serial,payload,retries:0,researchOffset:fields?.offset??0,
    });
    if (op !== "state" && op !== "research-state" && !op.startsWith("map-")) {
      this.setControls(false);
      this.mutationPending=true;
      this.renderIntelligence(true);
      this.status("Sending mission command...");
    }
    try {
      const result = this.listener.callSimConnect(
        "AeroScience.Command.v1",
        payload,
      );
      if (result && typeof result.catch === "function")
        result.catch(() => {
          if(['science-control','research-control','marker'].includes(op)){const pending=this.pending.get(requestId);if(pending)pending.sent=Date.now()-5001;}else this.pending.delete(requestId);
          this.status(
            "Simulator command delivery failed. Check the engine connection.",
          );
        });
    } catch (error) {
      if(['science-control','research-control','marker'].includes(op)){const pending=this.pending.get(requestId);if(pending)pending.sent=Date.now()-5001;}else this.pending.delete(requestId);
      this.status("Simulator command delivery failed.");
    }
  }
  tick() {
    if(!this.listener){this.registerBus();return;}
    if (
      document.hidden ||
      this.byId("aeroscience-frame").classList.contains("panelInvisible")
    )
      return;
    for (const [id, request] of this.pending)
      if (Date.now() - request.sent > 5000) {
        if(['science-control','research-control','marker'].includes(request.op)&&request.retries===0){
          request.retries++;request.sent=Date.now();
          try{const retry=this.listener.callSimConnect('AeroScience.Command.v1',request.payload);if(retry?.catch)retry.catch(()=>{request.sent=Date.now()-5001;});}catch(error){request.sent=Date.now()-5001;}
          this.byId(request.op==='marker'?'as-observation-status':request.op==='research-control'?'as-research-status':'as-collection-message').textContent='Retrying the identical flight-bound request; no new command target.';continue;
        }
        this.pending.delete(id);
        if(['science-control','research-control','marker'].includes(request.op))this.byId(request.op==='marker'?'as-observation-status':request.op==='research-control'?'as-research-status':'as-collection-message').textContent='No command confirmation after one identical retry. Review synchronized state before issuing a new command.';
        if (request.op.startsWith("map-"))
          this.byId("as-map-status").textContent =
            "Map reply timed out; retrying with the cached route retained.";
        else if (request.op !== "state")
          this.status(
            "No command confirmation. Check mission state before retrying.",
          );
      }
    if (Date.now() - this.lastReply > 6000) {
      this.setControls(false);
      this.status(
        "Windows engine unavailable. Launch AeroScience and check the SimConnect runtime.",
      );
      for (const id of [
        "as-altitude",
        "as-airspeed",
        "as-heading",
        "as-vspeed",
      ])
        this.byId(id).textContent = " - ";
      this.byId("as-mach").textContent = "Mach  - ";
      this.renderResearch(null, false);
      this.renderIntelligence(false);
      this.renderPersonalResearch(false);
      if (this.mapRenderer) {
        this.mapRenderer.update({ aircraft: null, track: [], markers: [] });
        this.byId("as-map-status").textContent =
          "Companion disconnected  -  cached route retained; live aircraft unavailable.";
      }
    }
    this.send("state");
    this.renderPersonalResearch(Date.now()-this.lastReply<=6000);
    this.requestResearch();
    if (this.mapVisible) {
      this.requestMapState();
      this.requestMapChunk();
    }
  }
  setControls(enabled) {
    for (const id of ["as-start", "as-end", "as-marker"]) {
      if (enabled) this.byId(id).removeAttribute("disabled");
      else this.byId(id).setAttribute("disabled", "");
    }
    if(!enabled)for(const button of this.querySelectorAll('[data-science-action]'))button.disabled=true;
  }
  receive(data) {
    try {
      if (typeof data !== "string" || this.utf8Bytes(data) > 8000) return;
      const reply = JSON.parse(data);
      const pendingRequest = this.pending.get(reply.requestId);
      if(reply.protocol===1&&pendingRequest&&['research-state','research-control'].includes(pendingRequest.op)){
        this.pending.delete(reply.requestId);
        if(pendingRequest.serial<this.researchSerial)return;
        this.researchSerial=pendingRequest.serial;
        this.mutationPending=Array.from(this.pending.values()).some(p=>!['state','research-state'].includes(p.op)&&!p.op.startsWith('map-'));
        this.setControls(!this.mutationPending&&Date.now()-this.lastReply<=6000);this.renderIntelligence(Date.now()-this.lastReply<=6000);
        if(!reply.ok){this.byId('as-research-status').textContent=reply.error||'Card command not confirmed. Synchronize before retrying.';this.requestResearch();return;}
        if(pendingRequest.researchOffset!==this.researchOffset){this.requestResearch();return;}
        if(reply.research?.flightId===(this.state?.activeMission?.id||null)){
          if(!this.researchState||reply.research.revision>=this.researchState.revision||reply.research.flightId!==this.researchState.flightId){this.researchState=reply.research;this.renderPersonalResearch(Date.now()-this.lastReply<=6000);}
          if(reply.receipt)this.byId('as-research-status').textContent=reply.receipt.message.replace(/ \[manual:[^\]]+\]/g,'').replace(/ \[detection:[^\]]+\]/g,'').replace(/ \[point:[0-9a-f-]{36}; attempt:[0-9a-f-]{36}\]$/i,'');
        }return;
      }
      if (
        reply.protocol === 1 &&
        pendingRequest &&
        pendingRequest.op.startsWith("map-")
      ) {
        this.pending.delete(reply.requestId);
        if (
          pendingRequest.op !== "map-plans" &&
          pendingRequest.planId !== (this.mapPlan || null)
        ) {
          this.requestMapState();
          return;
        }
        if (reply.ok && reply.map)
          this.receiveMap(pendingRequest.op, reply.map);
        else {
          this.byId("as-map-status").textContent =
            reply.error || "Map synchronization failed; waiting to retry.";
          this.loadingRevision = null;
        }
        return;
      }
      if (
        reply.protocol !== 1 ||
        !this.pending.has(reply.requestId) ||
        !reply.state
      )
        return;
      this.pending.delete(reply.requestId);
      if(pendingRequest.op==='marker'){if(reply.ok&&this.byId('as-observation-note').value===pendingRequest.observationText)this.byId('as-observation-note').value='';this.byId('as-observation-status').textContent=reply.ok?'Pilot Observation saved. Flight and science continue independently.':reply.error||'Observation was not saved; your text is preserved.';}
      if(pendingRequest.serial<this.appliedSerial){if(pendingRequest.op==='science-control')this.byId('as-collection-message').textContent=(reply.error||reply.receipt?.reason||'Earlier request acknowledged')+' · A newer authoritative view is already displayed.';return;}
      if(reply.engineInstance&&reply.engineInstance!==this.engineInstance&&this.seenEngines.has(reply.engineInstance))return;
      if(reply.intelligence&&this.intelligence&&reply.engineInstance===this.engineInstance&&reply.intelligence.missionId===this.intelligence.missionId&&reply.intelligence.revision<this.intelligence.revision)return;
      this.appliedSerial=pendingRequest.serial;
      this.lastReply = Date.now();
      const engineChanged=this.state&&(this.state.version!==reply.state.version||reply.engineInstance&&this.engineInstance!==reply.engineInstance);
      if(reply.engineInstance){this.engineInstance=reply.engineInstance;this.seenEngines.add(reply.engineInstance);}
      this.capabilities=Array.isArray(reply.capabilities)?reply.capabilities:[];
      const candidate=reply.intelligence;
      this.intelligence=this.capabilities.includes('mission-intelligence-1')&&candidate?.version===1&&
        candidate.missionId===(reply.state.activeMission?.id??null)&&Array.isArray(candidate.actions)&&Array.isArray(candidate.metrics)&&Array.isArray(candidate.warnings)&&Number.isFinite(Date.parse(candidate.generatedUtc))?candidate:null;
      this.state = reply.state;
      if(engineChanged)this.plansLoaded=false;
      if("selectedPlanId" in reply.state && !reply.state.activeMission && reply.state.selectedPlanId!==this.mapPlan){
        this.mapPlan=reply.state.selectedPlanId||null;this.plansLoaded=false;
      }
      const mutationPending = Array.from(this.pending.values()).some(
        (x) => x.op !== "state" && x.op !== "research-state" && !x.op.startsWith("map-"),
      );
      this.mutationPending=mutationPending;
      this.setControls(!mutationPending);
      this.render(reply.state);
      this.renderIntelligence(true);
      if(pendingRequest.op==='science-control')this.byId('as-collection-message').textContent=reply.receipt?.reason||reply.error||'Session command confirmed. Review current state.';
      if(!this.plansLoaded&&!Array.from(this.pending.values()).some(p=>p.op==="map-plans")){this.planOffset=0;this.send("map-plans",{offset:0});}
      if (!reply.ok)
        this.status(reply.error || "Mission command was rejected.");
    } catch (error) {
      this.status(
        "Invalid engine reply. Check companion and toolbar versions.",
      );
    }
  }
  render(state) {
    const t = state.telemetry,
      mission = state.activeMission;
    if(mission){this.byId("as-start-plan").textContent="Active recording: its saved plan snapshot is fixed. Plan selection for the next recording is separate.";}

    const fresh = t && Date.now() - Date.parse(t.timestampUtc) < 6000 && Date.now()-Date.parse(t.timestampUtc)>=-2000;
    this.byId("as-source").textContent =
      state.connection.source === "simulated"
        ? "SIMULATED TEST DATA"
        : fresh && state.connection.state === "connected"
          ? "SIMCONNECT TELEMETRY"
          : "MSFS DISCONNECTED";
    this.status(state.recordingStop ? "RECORDING STOPPED  /  " + state.recordingStop.code + ": " + state.recordingStop.message : state.connection.message);
    this.byId("as-aircraft").textContent = fresh
      ? t.aircraft
      : "Awaiting initialized aircraft telemetry";
    const fmt = (v) =>
      Number.isFinite(v) ? Math.round(v).toLocaleString("en-US") : " - ";
    this.byId("as-altitude").textContent = fresh ? fmt(t.altitudeMslFeet) : " - ";
    this.byId("as-airspeed").textContent = fresh
      ? fmt(t.indicatedAirspeedKnots)
      : " - ";
    this.byId("as-mach").textContent =
      "Mach " +
      (fresh && Number.isFinite(t.machNumber) ? t.machNumber.toFixed(2) : " - ");
    this.byId("as-heading").textContent = fresh ? fmt(t.headingDegrees) : " - ";
    this.byId("as-vspeed").textContent = fresh
      ? fmt(t.verticalSpeedFeetPerMinute)
      : " - ";
    this.renderResearch(state.research, fresh);
    if (!mission) this.byId("as-marker").setAttribute("disabled", "");
    this.byId("as-idle").hidden = !!mission;
    this.byId("as-recording").hidden = !mission;
    if (mission) {
      // Prepare the next mission with the safe default, independently of the active mission's mode.
      if(this.renderedMission!==mission.id){
      this.byId("as-mode").value = "universal";
      this.byId("as-mode-help").textContent =
        "All compatible aircraft can record missions.";
      this.byId("as-name").value = "";
      }this.renderedMission=mission.id;
      this.byId("as-active-name").textContent = mission.name;
      this.byId("as-active-plan").textContent=state.research?.planId?"Active saved-plan snapshot (fixed for this recording). Fly the route reference on Map.":"Active unplanned mission: scientific preparation follows actual aircraft movement.";
      this.byId("as-active-mode").textContent =
        mission.mode === "realistic"
          ? "Realistic Mode  /  advanced science features coming in a future release"
          : "Universal Mode  /  Fly Anything";
      this.byId("as-samples").textContent = mission.sampleCount + " samples";
      const seconds = Math.max(
        0,
        Math.floor((Date.now() - Date.parse(mission.startedUtc)) / 1000),
      );
      this.byId("as-duration").textContent = [
        Math.floor(seconds / 3600),
        Math.floor(seconds / 60) % 60,
        seconds % 60,
      ]
        .map((x) => ("0" + x).slice(-2))
        .join(":");
    }
  }
  utf8Bytes(text){let bytes=0;for(const character of text){const cp=character.codePointAt(0);bytes+=cp<=0x7f?1:cp<=0x7ff?2:cp<=0xffff?3:4;}return bytes;}
  renderIntelligence(connected){
    const i=this.intelligence,supported=this.capabilities.includes('session-bound-controls-1')&&i?.version===1;
    const fresh=connected&&supported&&Date.now()-Date.parse(i.generatedUtc)<=6000&&Date.now()-Date.parse(i.generatedUtc)>=-2000;
    for(const button of this.querySelectorAll('[data-science-action]')){button.disabled=!fresh||this.mutationPending||!i.actions.includes(button.dataset.scienceAction);button.title=!supported?'This companion does not support session-bound native controls.':!fresh?'Stale engine state; synchronize before controlling science.':'Backend session and control revisions are checked before applying.';}
    if(!supported){this.byId('as-session-phase').textContent='Science intelligence unsupported by this engine';this.byId('as-session-authority').textContent='Update the companion for multi-session native controls. Legacy Payload pause/resume remains available.';this.byId('as-grade').textContent='Grade and scientific yield unavailable.';this.byId('as-session-identity').textContent='No session identity supplied.';this.byId('as-next-trigger').textContent='Next trigger not assessed.';this.byId('as-science-yield').textContent='Scientific yield not assessed.';this.byId('as-intelligence-warning').textContent='';this.byId('as-intelligence-asof').textContent='';return;}
    this.byId('as-session-phase').textContent=(fresh?'Science: ':'Stale science state: ')+i.phase+(i.sessionSequence?' · Session '+i.sessionSequence:' · No open session');
    this.byId('as-session-authority').textContent='Automation: '+i.authority+(i.manualHold?' · pilot hold; rearm required':'')+(i.continuityHold?' · continuity hold':'');
    this.byId('as-session-identity').textContent='Flight '+(i.missionId||'none')+' · Session '+(i.sessionId||'none')+' · revision '+(i.revision??'Unknown');
    this.byId('as-grade').textContent=i.gradeStatus+' performance'+(Number.isFinite(i.scoreLower)&&Number.isFinite(i.scoreUpper)?' · bounds '+i.scoreLower.toFixed(1)+'–'+i.scoreUpper.toFixed(1)+' / 100':' · score unavailable')+'. Live bounds are not a final letter grade.';
    this.byId('as-intelligence-warning').textContent=i.warnings?.[0]||'See dashboard for instrument/source limitations; no accuracy certification is inferred.';
    this.byId('as-next-trigger').textContent=i.triggerStatus;
    this.byId('as-science-yield').textContent=i.metrics.length?i.metrics.map(m=>m.instrumentId+' · '+m.id+': '+m.status+(['supported','caution'].includes(m.status)&&Number.isFinite(m.numerator)&&m.denominator>0?' '+m.numerator+'/'+m.denominator+' assessed '+m.unit+' ('+(100*m.numerator/m.denominator).toFixed(1)+'%)':' — unassessed')+' · missing '+(m.missing??'Unknown')+'; unassessed '+(m.unassessed??'Unknown')+' · whole-flight scope').join('; '):'Good Data / valid scientific coverage not yet assessed. Flight path is not valid-pixel coverage.';
    this.byId('as-intelligence-asof').textContent='State as of '+i.generatedUtc+' · metrics '+(i.metricsUtc||'Pending')+(i.metricsUtc&&Date.now()-Date.parse(i.metricsUtc)>15000?' · Evidence cutoff is older than 15 s; metrics are not current.':'')+(i.detailsOmitted?' · Additional detail on dashboard.':'');
  }
  requestResearch(){
    if(!this.capabilities.includes('personal-research-controls-1')||!this.state?.activeMission){this.researchState=null;this.researchOffset=0;this.researchFocusedCard=null;this.focusedResearchSummary=null;this.renderPersonalResearch(false);return;}
    if(this.researchState&&this.researchState.flightId!==this.state.activeMission.id){this.researchOffset=0;this.researchFocusedCard=null;this.focusedResearchSummary=null;this.researchPointChoices={};}
    if(Array.from(this.pending.values()).some(p=>['research-state','research-control'].includes(p.op)))return;
    this.send('research-state',{missionId:this.state.activeMission.id,offset:this.researchOffset});
  }
  renderPersonalResearch(fresh){
    const state=this.researchState,host=this.byId('as-research-list');
    const valid=fresh&&Date.now()-Date.parse(state?.generatedUtc)<6000&&state?.flightId===this.state?.activeMission?.id&&state?.recording;
    const pending=Array.from(this.pending.values()).some(p=>p.op==='research-control');
    const summary=this.byId('as-operations-summary');
    const selected=state?.cards.find(c=>c.card.id===this.researchFocusedCard);
    if(selected)this.focusedResearchSummary=selected.card.name+' · '+(selected.openAttempt?'Attempt recording':selected.active?'Active':'Inactive')+(selected.procedure?.nextPointId?' · Next point '+(selected.card.procedure?.points.find(p=>p.id===selected.procedure.nextPointId)?.conditions.testPoint.name||'on dashboard'):'');
    summary.textContent=!valid?'Research state unavailable or stale — no current progress inferred.':(this.focusedResearchSummary?'Focused: '+this.focusedResearchSummary+'. ':'Choose a card on Research. ')+(state.activeCardCount??state.cards.filter(c=>c.active).length)+' active card(s)'+(state.activeCardNames?.length?' · '+state.activeCardNames.join('; '):'')+'. Flight and science remain independent.';
    const signature=JSON.stringify([state?.flightId,state?.revision,this.researchOffset,valid,state?.canBeginAttempt,pending,this.researchFocusedCard]);
    if(this.researchView===signature)return;this.researchView=signature;
    const focusCard=document.activeElement?.dataset?.researchCard,focusAction=document.activeElement?.dataset?.researchAction;
    host.replaceChildren();this.byId('as-research-previous').disabled=!valid||this.researchOffset===0;this.byId('as-research-next').disabled=!valid||state?.nextOffset==null;
    if(!valid){this.byId('as-research-status').textContent=this.state?.activeMission?'Waiting for synchronized personal card state. Flight and science remain independent.':'Choose personal cards in Mission Planner before recording.';return;}
    this.byId('as-research-status').textContent=state.cards.length?'Activate a card, then Begin Attempt / End Attempt. Deactivate Card does not stop flight or science.':'No personal cards selected for this flight.';
    for(const item of state.cards){
      const row=document.createElement('section'),title=document.createElement('h4'),info=document.createElement('p');row.setAttribute('aria-label',item.card.name);row.tabIndex=-1;row.dataset.researchCard=item.card.id;
      const atmospheric=item.card.atmosphericProfile;
      const transect=item.card.atmosphericTransect;
      const survey=item.card.survey;
      const observation=item.card.observationFlight;
      const sensor=item.card.experimentalSensor;
      const point=item.card.flightRegime?.testPoint,rule=item.card.flightRegime;
      const procedure=item.card.procedure;
      const requirements=procedure?(procedure.ordered?'Ordered':'Independent')+' procedure · '+(item.procedure?.points||[]).map(p=>p.completedRepetitions+'/'+p.requiredRepetitions).join(' · ')+' repetitions; verify originals after flight':point?[point.airspeedRequired?rule.airspeedKnots+' ± '+rule.airspeedToleranceKnots+' knots IAS':null,point.altitudeRequired?rule.altitudeMslFeet+' ± '+rule.altitudeToleranceFeet+' feet MSL':null].filter(Boolean).join(' AND ')+' · '+rule.holdSeconds+' continuous seconds':sensor?'Fictional modeled temperature · '+sensor.responseSeconds+' s response · '+sensor.minimumSamples+' original science samples required':observation?observation.minimumObservations+' documented Pilot Observations · notes are not sensor measurements':survey?'Survey: '+survey.measurements.map(m=>({airTemperature:'Air Temperature',terrainElevation:'Terrain Elevation',landSurfaceTemperature:'Land Surface Temperature'})[m]).join(' + ')+' · source-qualified samples required':transect?transect.requiredStations+' distinct sampling locations · '+transect.minimumExtentMetres+' m geographic extent · collect valid temperature readings':atmospheric?atmospheric.requiredBands+' neighboring '+atmospheric.bandWidthFeet+'-foot MSL bands · collect valid temperature readings':item.card.flightRegime.airspeedKnots+' knots IAS · '+item.card.flightRegime.altitudeMslFeet+' feet MSL';
      title.textContent=item.card.name+' · revision '+item.card.revision;info.textContent=(item.openAttempt?'Attempt recording':item.active?'Active':'Inactive')+' · '+(item.detection?item.detection.status+(item.detection.lastOutcome?' — '+({pass:'Pass',fail:'Fail',insufficientEvidence:'Insufficient Evidence'}[item.detection.lastOutcome]||'Unknown'):'')+' · ':'')+requirements+'. '+(item.activationBlocked||'');row.append(title,info);
      if(item.organization){const label=document.createElement('p');label.textContent=item.organization.organizationName+' · issued '+item.organization.issuedRevision+' · '+item.organization.status;row.append(label);}
      const focus=document.createElement('button');focus.textContent=this.researchFocusedCard===item.card.id?'Focused in Toolbar':'Focus Card';focus.onclick=()=>{this.researchFocusedCard=item.card.id;this.researchView=null;this.renderPersonalResearch(true);this.byId('as-research-list').querySelector('[data-research-card="'+item.card.id+'"]')?.focus();};row.append(focus);
      let pointSelect=null;
      if(procedure){const label=document.createElement('label');label.textContent='Test point';pointSelect=document.createElement('select');pointSelect.setAttribute('aria-label','Test point for '+item.card.name);this.researchPointChoices||={};for(const point of procedure.points){const option=document.createElement('option');option.value=point.id;option.textContent=point.conditions.testPoint.name+' · '+point.conditions.holdSeconds+' s · '+point.repetitions+' attempts';pointSelect.append(option);}pointSelect.value=item.openAttempt?.testPointId||this.researchPointChoices[item.card.id]||item.procedure?.nextPointId||procedure.points[0].id;pointSelect.disabled=pending||!!item.openAttempt;pointSelect.onchange=()=>this.researchPointChoices[item.card.id]=pointSelect.value;label.append(pointSelect);row.append(label);}
      for(const [action,label] of [['activate','Activate Card'],['deactivate','Deactivate Card'],['beginAttempt','Begin Attempt'],['endAttempt','End Attempt'],...((item.card.version===7&&point)||procedure?[['enableAutomatic','Enable Automatic Detection'],['disableAutomatic','Disable Automatic Detection']]:[])]){
        const b=document.createElement('button');b.textContent=label;b.dataset.researchCard=item.card.id;b.dataset.researchAction=action;
        b.disabled=pending||action==='activate'&&(item.active||!!item.activationBlocked)||action==='deactivate'&&!item.active||action==='beginAttempt'&&(!item.active||!!item.openAttempt&&!item.openAttempt.automatic||!state.canBeginAttempt)||action==='endAttempt'&&!item.openAttempt||action==='enableAutomatic'&&(!item.active||!!item.openAttempt||!state.canBeginAttempt)||action==='disableAutomatic'&&!item.detection?.enabled;
        b.onclick=()=>{if(b.disabled)return;if(action==='endAttempt'&&this.researchPointChoices)delete this.researchPointChoices[item.card.id];const requestId=this.requestId();this.send('research-control',{requestId,missionId:state.flightId,offset:this.researchOffset,researchControl:{version:1,flightId:state.flightId,requestId,expectedRevision:state.revision,action,cardId:item.card.id,expectedAttemptId:item.openAttempt?.id||null,...(['beginAttempt','enableAutomatic'].includes(action)&&pointSelect?{testPointId:pointSelect.value}:{})}});this.renderPersonalResearch(true);};row.append(b);
      }host.append(row);
    }
    if(focusCard){const choices=Array.from(host.querySelectorAll('button')).filter(b=>b.dataset.researchCard===focusCard&&!b.disabled);const target=choices.find(b=>b.dataset.researchAction===focusAction)||choices[0]||Array.from(host.children).find(r=>r.dataset.researchCard===focusCard);target?.focus();}
  }
  renderResearch(research, fresh) {
    const g = research && research.guidance;
    this.byId("as-guidance").textContent =
      !fresh || !g || g.quality !== "valid"
        ? "Flight Guidance unavailable"
        : "AP: " +
          g.autopilot +
          " | " +
          [g.lateral, g.vertical, g.speed]
            .map(
              (m) =>
                (m.mode || "Unavailable") +
                (Number.isFinite(m.target)
                  ? " " +
                    m.target.toFixed(m.unit === "mach" ? 2 : 0) +
                    " " +
                    m.unit
                  : "") +
                (m.armed ? " (Armed: " + m.armed + ")" : ""),
            )
            .join(" | ");
    const control = this.byId("as-science-pause");
    if (control) {
      control.hidden=this.capabilities.includes('session-bound-controls-1')&&this.intelligence?.version===1;
      control.textContent =
        research && research.intentionallyPaused
          ? "Resume science collection"
          : "Pause science collection";
      control.disabled = !(this.state && this.state.activeMission);
      control.onclick = () => {
        if (!this.state || !this.state.activeMission) return;
        this.send(
          research && research.intentionallyPaused
            ? "science-resume"
            : "science-pause",
          { missionId: this.state.activeMission.id },
        );
      };
    }
    const collection = this.byId("as-science-state");
    if (collection)
      collection.textContent = research
        ? "Collection: " + research.collectionStatus
        : "Science unavailable";
    const holder = this.byId("as-instruments");
    if (!research) {
      holder.textContent = "Scientific source unavailable";
      return;
    }
    if (!this.instrumentNodes) this.instrumentNodes = {};
    if (!holder.querySelector(".as-instrument")) holder.textContent = "";
    for (const instrument of research.instruments || []) {
      let nodes = this.instrumentNodes[instrument.instrumentId];
      if (!nodes || !nodes.row.parentNode) {
        const row = document.createElement("div");
        row.className = "as-instrument";
        const title = document.createElement("strong");
        title.textContent =
          {
            "ambient-temperature": "Ambient temperature",
            "ambient-pressure": "Ambient pressure",
            "ambient-wind": "Ambient wind",
            "ambient-density": "Air density",
            "thermal-imager": "Thermal Imager",
            "multispectral-imager": "Multispectral Imager",
            "hyperspectral-imager": "Hyperspectral Imager",
            "terrain-scanner": "Terrain Scanner (LiDAR / Elevation)",
          }[instrument.instrumentId] || "Science instrument";
        const stateLabel = document.createElement("span");
        stateLabel.className = "as-science-state";
        const toggle = document.createElement("new-push-button");
        const interval = document.createElement("select");
        for (const seconds of [1, 2, 5, 10, 30, 60]) {
          const option = document.createElement("option");
          option.value = seconds * 1000;
          option.textContent = seconds + " s";
          interval.appendChild(option);
        }
        const values = document.createElement("div"),
          status = document.createElement("p");
        values.className = "as-science-values";
        const details = document.createElement("details"),
          summary = document.createElement("summary");
        summary.textContent = "Diagnostics";
        details.appendChild(summary);
        details.appendChild(status);
        const intervalLabel = document.createElement("label");
        intervalLabel.textContent = "Collection interval ";
        intervalLabel.appendChild(interval);
        row.appendChild(title);
        row.appendChild(stateLabel);
        row.appendChild(toggle);
        row.appendChild(intervalLabel);
        row.appendChild(values);
        row.appendChild(details);
        holder.appendChild(row);
        nodes = {
          row: row,
          state: stateLabel,
          toggle: toggle,
          interval: interval,
          values: values,
          status: status,
        };
        this.instrumentNodes[instrument.instrumentId] = nodes;
        toggle.onclick = () => {
          const current = this.state.research.instruments.find(
            (i) => i.instrumentId === instrument.instrumentId,
          );
          this.send("instrument", {
            instrumentId: instrument.instrumentId,
            enabled: !current.enabled,
          });
        };
        interval.onchange = () =>
          this.send("instrument", {
            instrumentId: instrument.instrumentId,
            intervalMs: Number(interval.value),
          });
      }
      nodes.toggle.textContent = instrument.enabled ? "On" : "Off";
      nodes.toggle.setAttribute(
        "aria-label",
        instrument.enabled ? "Disable instrument" : "Enable instrument",
      );
      if (document.activeElement !== nodes.interval)
        nodes.interval.value = instrument.intervalMs;
      nodes.values.textContent = "";
      if(instrument.payloadProfileName){const profile=document.createElement('div');profile.className='as-science-reading';profile.textContent=instrument.payloadProfileName;nodes.values.appendChild(profile);}
      for (const v of instrument.measurements || []) {
        const value = document.createElement("div");
        value.className = "as-science-reading";
        const label =
          {
            temperature: "Temperature",
            pressure: "Pressure",
            "wind-speed": "Wind speed",
            "wind-direction": "Wind direction",
            density: "Density",
          }[v.measurementId] || "Measurement";
        const display = Number.isFinite(v.displayValue)
          ? v.displayValue
          : v.value;
        value.textContent =
          label +
          ": " +
          (fresh &&
          instrument.enabled &&
          instrument.quality === "valid" &&
          v.quality === "valid" &&
          Number.isFinite(display)
            ? display.toFixed(
                v.measurementId === "density" && v.displayUnit === "slug/ft³"
                  ? 6
                  : 2,
              )
            : "Unavailable") +
          " " +
          (v.displayUnit || v.unit);
        nodes.values.appendChild(value);
      }
      nodes.state.textContent = instrument.enabled
        ? instrument.quality
        : "Disabled";
      nodes.row.setAttribute(
        "data-state",
        instrument.enabled ? instrument.quality : "disabled",
      );
      nodes.status.textContent =
        (instrument.enabled ? instrument.quality : "disabled") +
        "  /  " +
        instrument.acquisitionSets +
        " acquisition sets  /  " +
        instrument.observationCount +
        (instrument.instrumentId.indexOf("imager") >= 0 || instrument.instrumentId === "terrain-scanner" ? " saved products  /  " : " measurement values  /  ") +
        instrument.rejectedObservations +
        " rejected  /  " +
        instrument.missingIntervals +
        " unexpected missing  /  actual " +
        (Number.isFinite(instrument.actualAverageIntervalSeconds)
          ? instrument.actualAverageIntervalSeconds.toFixed(2) + " s"
          : "unavailable") +
        "  /  last " +
        (instrument.lastSuccessfulUtc || "none");
    }
  }
  disconnectedCallback() {
    if (this.mapRenderer) this.mapRenderer.destroy();
    clearInterval(this.timer);
    clearInterval(this.loadTimer);
    if (this.listener) {
      this.listener.off("AeroScience.Reply.v1", this.onReply);
      this.listener.unregister();
    }
    this.listener = null;
    this.initialized = false;
    if (super.disconnectedCallback) super.disconnectedCallback();
  }
}
window.customElements.define("ingamepanel-aeroscience", IngamePanelAeroScience);
checkAutoload();
