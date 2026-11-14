/* Simulator-native UI. SDK globals are provided by MSFS, not a normal web browser.
 * See docs/toolbar.md for SDK sources, provenance, and outstanding simulator checks. */
const AEROSCIENCE_TOOLBAR_IDENTITY = { profile: "production", source: null };
class IngamePanelAeroScience extends TemplateElement {
  connectedCallback() {
    super.connectedCallback();
    this.aeroScienceConnected=true;
    this.connectionEpoch=(this.connectionEpoch||0)+1;
    this.initializationAttempts = 0;
    this.onDocumentReady = () => this.initialize();
    if (document.readyState === "loading")
      document.addEventListener("DOMContentLoaded", this.onDocumentReady, {
        once: true,
      });
    else this.initialize();
  }
  initialize() {
    if (this.initialized || !this.aeroScienceConnected) return;
    clearTimeout(this.initializationTimer);
    // TemplateElement may connect while the host document is already ready but
    // its imported panel children are still being parsed. Do not poison startup.
    if (!this.querySelector('[data-aeroscience-template-ready]')) {
      this.setAttribute('data-initialization-state', 'waiting-for-template');
      if ((this.initializationAttempts = (this.initializationAttempts || 0) + 1) < 50)
        this.initializationTimer = setTimeout(() => this.initialize(), 100);
      else {
        this.setAttribute('data-initialization-state', 'template-unavailable');
        const status = this.querySelector('#as-status');
        if (status) status.textContent = 'Toolbar template did not finish loading. Close and reopen this panel; report its title if this continues.';
      }
      return;
    }
    this.initialized = true;
    this.setAttribute('data-initialization-state', 'binding-controls');
    this.pending = new Map();
    this.uiFaults = new Map();
    this.replyFaults = 0;
    this.instrumentNodes={};
    this.listener = null;
    this.state = null;
    this.lastReply = 0;
    this.serial=0;this.appliedSerial=0;this.seenEngines=new Set();this.intelligence=null;this.capabilities=[];this.mutationPending=false;
    this.byId = (id) => this.querySelector("#" + id);
    if(typeof AeroScienceI18n !== 'undefined')AeroScienceI18n.apply(this,null);
    this.byId('as-instruments').textContent='';
    this.status('Connecting to companion...');
    this.busLoadClosed=false;
    this.byId('as-build-identity').textContent = AEROSCIENCE_TOOLBAR_IDENTITY.profile + ' toolbar | source ' + (AEROSCIENCE_TOOLBAR_IDENTITY.source || 'not supplied by this package') + ' | protocol 1';
    this.byId('as-link-detail').textContent = 'Commands: AeroScience.Command.v1 | Replies: AeroScience.Reply.v1 | No matching engine reply yet. Channel registration alone is not an engine connection.';
    this.researchState=null;this.researchOffset=0;this.researchSerial=0;
    this.byId('as-research-previous').onclick=()=>{this.researchOffset=Math.max(0,this.researchOffset-(this.researchState?.pageSize||4));this.requestResearch();};
    this.byId('as-research-next').onclick=()=>{if(this.researchState?.nextOffset!=null){this.researchOffset=this.researchState.nextOffset;this.requestResearch();}};
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
    this.currentPage=!this.byId('as-mission-view').hidden?false:!this.byId('as-payload-view').hidden?true:!this.byId('as-map-view').hidden?'map':'research';
    for(const [id,value]of [['as-mission-page',false],['as-payload-page',true],['as-map-page','map'],['as-research-page','research']])this.byId(id).setAttribute('aria-pressed',String(this.currentPage===value||id==='as-mission-page'&&this.currentPage==='map'));
    const navigation=this.byId('as-mission-page').parentNode;
    navigation.onkeydown=event=>{if(!['ArrowLeft','ArrowRight','Home','End'].includes(event.key))return;const buttons=Array.from(navigation.querySelectorAll('[data-native-section]')).filter(b=>!b.hidden);const at=buttons.indexOf(document.activeElement);if(at<0)return;event.preventDefault();const next=event.key==='Home'?0:event.key==='End'?buttons.length-1:(at+(event.key==='ArrowRight'?1:buttons.length-1))%buttons.length;buttons[next].focus();};
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
    this.planRows=[];
    this.planOptionsSignature=null;
    this.mapPlan=null;
    this.selectedPlan=null;
    this.selectedPlanMissing=false;
    this.planFormId=undefined;
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
      if(select.disabled||!option||this.state?.activeMission)return;
      const id=this.send("select-plan",{planId:select.value||null});
      if(id){this.byId('as-plan-status').textContent='Selecting '+option.textContent+'...';select.disabled=true;}
      else this.renderPlanSelection();
    };
    this.byId("as-map-plans").onclick = () => {
      this.planOffset = 0;
      this.requestPlans();
    };
    this.byId('as-map-previous-plans').onclick=()=>{this.planOffset=Math.max(0,this.planOffset-10);this.requestPlans();};
    this.byId("as-map-more-plans").onclick = () => {
      this.planOffset += 10;
      this.requestPlans();
    };
    this.timer = setInterval(() => this.tick(), 1000);
    try {
      // The SDK does not promise an Include.addScript completion callback.
      // Availability below, not a browser-only callback, owns registration.
      Include.addScript("/JS/Services/CommBus.js");
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
        if (!this.listener){this.busLoadClosed=true;
          this.status("Simulator link unavailable. Reopen this panel; see Connection & Build Details.");
          this.byId('as-link-detail').textContent='The SDK CommBus helper/listener did not become available. No engine reply was received. Check the matching Validation package and supported simulator runtime.';}
      } else this.registerBus();
    }, 100);
    this.setAttribute('data-initialization-state', 'ready');
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
      !this.aeroScienceConnected ||
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
  requestPlans(){
    if(Array.from(this.pending.values()).some(p=>p.op==='map-plans'&&p.researchOffset===this.planOffset))return;
    this.byId('as-plan-status').textContent='Loading saved mission plans...';
    this.send('map-plans',{offset:this.planOffset});
  }
  synchronizePlan(state){
    if(!('selectedPlanId' in state))return; // Older engines cannot establish a saved selection.
    const id=state.selectedPlanId||null;
    if(id!==this.mapPlan){
      this.mapPlan=id;this.selectedPlan=this.planRows.find(p=>p.id===id)||null;
      this.selectedPlanMissing=false;this.plansLoaded=false;
      this.routeRevision=null;this.loadingRevision=null;this.mapCursor=null;
    }
    this.renderPlanSelection();
  }
  renderPlanSelection(){
    const select=this.byId('as-map-plan');
    const signature=JSON.stringify([this.planRows.map(p=>[p.id,p.name]),this.mapPlan,this.selectedPlan?.name]);
    if(signature!==this.planOptionsSignature){
      select.textContent='';
      const add=(value,name)=>{const option=document.createElement('option');option.value=value;option.textContent=name;select.appendChild(option);};
      add('','Unplanned Mission');
      for(const plan of this.planRows)add(plan.id,plan.name);
      if(this.mapPlan&&!this.planRows.some(p=>p.id===this.mapPlan))add(this.mapPlan,this.selectedPlan?.name||'Selected plan (details unavailable)');
      this.planOptionsSignature=signature;
    }
    select.value=this.mapPlan||'';
    const selecting=Array.from(this.pending.values()).some(p=>p.op==='select-plan');
    const fresh=Date.now()-this.lastReply<=6000;
    select.disabled=!fresh||!!this.state?.activeMission||selecting;
    this.byId('as-map-previous-plans').disabled=!fresh||this.planOffset===0;
    this.byId('as-map-more-plans').disabled=!fresh||this.planRows.length<10;
    this.byId('as-plan-map').disabled=!!this.mapPlan&&!this.selectedPlan;
    if(this.state?.activeMission)return;
    const plan=this.selectedPlan;
    this.byId('as-mode').disabled=!!this.mapPlan;
    if(plan&&this.planFormId!==plan.id){this.byId('as-name').value=plan.name;this.byId('as-mode').value=plan.mode;this.byId('as-mode').onchange();this.planFormId=plan.id;}
    if(plan&&this.byId('as-mode').value!==plan.mode){this.byId('as-mode').value=plan.mode;this.byId('as-mode').onchange();}
    if(!this.mapPlan&&this.planFormId){this.byId('as-name').value='';this.byId('as-mode').value='universal';this.byId('as-mode').onchange();this.planFormId=null;}
    this.byId('as-start-plan').textContent=this.selectedPlanMissing?'Selected plan is no longer available. Choose another plan or Unplanned Mission.':this.mapPlan
      ?plan?'Selected: '+plan.name+' | '+plan.waypoints+' waypoints | '+plan.patterns+' patterns. Instruments: '+(plan.instruments||'None enabled')+'.':'Loading selected plan details...'
      :'Unplanned Mission: record wherever you fly.';
    if(!fresh||this.uiFaults.has('flight')||this.mutationPending||selecting||this.mapPlan&&!plan)this.byId('as-start').setAttribute('disabled','');
    else this.byId('as-start').removeAttribute('disabled');
  }
  receiveMap(op, map, selectedPlan) {
    this.updateView(op,()=>this.receiveMapView(op,map,selectedPlan));
  }
  receiveMapView(op, map, selectedPlan) {
    if (op === "map-plans") {
      this.planRows=map;
      const selected=map.find(p=>p.id===this.mapPlan)||selectedPlan;
      if(selected?.id===this.mapPlan){this.selectedPlan=selected;this.selectedPlanMissing=false;}
      else if(this.mapPlan&&selectedPlan===null){this.selectedPlan=null;this.selectedPlanMissing=true;}
      this.plansLoaded=true;
      this.renderPlanSelection();
      this.byId('as-plan-status').textContent=map.length?'Saved plans '+(this.planOffset+1)+'-'+(this.planOffset+map.length)+'. Selection is saved by the companion.':this.planOffset?'No more saved plans. Use Previous plans.':'No saved mission plans. Create one in the desktop Flight Planner.';
      return;
    }
    if (op === "map-state") {
      this.lastMapAt = Date.now();
      const changed =
        map.revision !== this.routeRevision &&
        map.revision !== this.loadingRevision;
      if (changed) {
        this.loadingRevision = map.revision;
        this.routeItems = [];
        this.mapCursor = null;
        this.mapRenderer.route(map.revision, []);
        this.byId("as-map-waypoint").textContent='';
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
        select.textContent='';
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
      this.busLoadClosed ||
      typeof RegisterCommBusListener !== "function" ||
      !this.aeroScienceConnected
    )
      return;
    let candidate=null;const epoch=this.connectionEpoch;
    try {
      candidate = RegisterCommBusListener(() => {if(this.aeroScienceConnected&&epoch===this.connectionEpoch&&!this.lastReply)this.status("Simulator channel ready; waiting for the Windows engine.");});
      candidate.on("AeroScience.Reply.v1", this.onReply);
      this.listener=candidate;
      this.tick();
    } catch (error) {
      // A partially registered listener must not permanently poison retries.
      try{candidate?.unregister();}catch{}
      this.listener=null;
      this.status("Unable to register the SDK CommBus listener.");
    }
  }
  page(payload) {
    if(payload==='research'&&this.byId('as-research-page').hidden)payload=false;
    const changed=this.currentPage!==payload;this.currentPage=payload;
    this.mapVisible = payload === "map";
    this.byId('as-research-cards').hidden=payload!=="research";
    for(const [id,value]of [['as-mission-page',false],['as-payload-page',true],['as-map-page','map'],['as-research-page','research']])this.byId(id).setAttribute('aria-pressed',String(payload===value||id==='as-mission-page'&&payload==='map'));
    this.byId("as-mission-view").hidden = payload !== false;
    this.byId("as-payload-view").hidden = payload !== true;
    this.byId("as-map-view").hidden = !this.mapVisible;
    if(changed){const view=this.byId(this.state?.recordingStop?'as-status':payload===true?'as-payload-view':payload==='research'?'as-research-cards':payload==='map'?'as-map-view':'as-mission-view');this.byId('as-scroll').scrollTop=view.offsetTop;}
    if (this.mapVisible && this.mapRenderer) {
      this.mapRenderer.resize();
      this.requestMapState();
    }
  }
  status(text) {
    this.byId("as-status").textContent = this.nativeMessage(text);
  }
  nativeMessage(text){
    // Normalize generated punctuation around names, never the names themselves.
    const names=[this.state?.telemetry?.aircraft,this.state?.activeMission?.name,...(this.planRows||[]).map(p=>p.name),...(this.researchState?.cards||[]).reduce((all,c)=>all.concat([c.card.name,...(c.card.procedure?.points||[]).map(p=>p.conditions.testPoint.name)]),[])].filter(Boolean).sort((a,b)=>b.length-a.length);
    let rest=String(text||''),result='';
    const format=s=>s.replace(/ · /g,' | ').replace(/[—–]/g,'-').replace(/…/g,'...').replace(/±/g,'+/-');
    while(rest){let index=-1,name='';for(const item of names){const at=rest.indexOf(item);if(at>=0&&(index<0||at<index)){index=at;name=item;}}if(index<0){result+=format(rest);break;}result+=format(rest.slice(0,index))+name;rest=rest.slice(index+name.length);}
    return result;
  }
  flightPresentation(connected){
    const i=this.intelligence;
    if(typeof AeroScienceFlightStatus==='undefined'||typeof AeroScienceFlightStatus.present!=='function')return{fresh:false,science:'unknown',scienceActions:[],sessionId:null,counts:null,connection:'unknown'};
    return AeroScienceFlightStatus.present({version:i?.version,generatedUtc:i?.generatedUtc,flight:this.state,
      collection:{state:{missionId:i?.missionId,phase:i?.phase,currentSessionId:i?.sessionId,continuityHold:i?.continuityHold},
        currentSession:i?.sessionId?{id:i.sessionId,sequence:i.sessionSequence}:null,fault:i?.collectionFault,
        actions:(i?.actions||[]).map(action=>({action,available:true}))},retainedScience:i?.retainedScience},this.state,
      {stale:!connected||Date.now()-this.lastReply>6000});
  }
  text(key,fallback,values){return typeof AeroScienceI18n==='undefined'?fallback:AeroScienceI18n.text(key,fallback,values);}
  renderCollectionBanner(connected){
    const i=this.intelligence,flight=this.state?.activeMission,model=this.flightPresentation(connected);
    const active=model.science==='collecting';
    const phase=!connected?'Stale status / companion unavailable':i?.collectionFault||this.state?.research?.scienceStorageError?'Scientific storage unavailable':!model.fresh?'Science status unavailable':!flight?'No active mission':model.connection==='disconnected'?'Disconnected / unavailable':model.connection==='stale'?'Telemetry stale / unavailable':model.connection==='paused'?'Simulator paused / science unconfirmed':model.science==='unknown'?'Science status unavailable':model.science==='armed'?'Armed / Not started':model.science==='betweenSessions'?'Session ended':this.nativeLabel(model.science);
    this.byId('as-collection-banner').setAttribute('data-active',String(active));
    this.byId('as-root').dataset.recording=connected?(flight?'active':'inactive'):'unknown';this.byId('as-root').dataset.science=model.science;this.byId('as-root').dataset.sessionId=model.sessionId||'';this.byId('as-root').dataset.connection=model.connection;
    this.byId('as-collection-headline').textContent=this.text(active?'toolbar.collectionActive':'toolbar.collectionInactive',active?'SCIENCE COLLECTION ACTIVE':'SCIENCE COLLECTION INACTIVE');
    this.byId('as-collection-summary').textContent='Flight recording: '+(connected?(flight?'Active':'Inactive'):'Unavailable')+' | Science: '+(active?'Collecting':phase);
  }
  updateView(component,update){
    try{update();this.uiFaults.delete(component);}
    catch(error){
      const kind=['TypeError','RangeError','ReferenceError','SyntaxError'].includes(error?.name)?error.name:'Error';
      this.uiFaults.set(component,kind);
      const target={'map-plans':'as-plan-status','map-state':'as-map-status','map-chunk':'as-map-status',research:'as-operations-summary',science:'as-session-phase',payload:'as-science-state'}[component];
      if(target)this.byId(target).textContent='This view could not update. See Connection & Build Details.';
      if(component==='science')for(const button of this.querySelectorAll('[data-science-action]'))button.disabled=true;
      if(component==='flight')this.setControls(false);
      if(component==='research')this.researchView=null;
      if(component==='plan selection'){this.byId('as-map-plan').disabled=true;this.byId('as-start').setAttribute('disabled','');}
      if(component==='research'||component==='payload')for(const button of this.byId(component==='research'?'as-research-cards':'as-payload-view').querySelectorAll('button,select,new-push-button'))button.setAttribute('disabled','');
    }
    // Only fixed component names and exception classes; never message/stack/payload text.
    this.byId('as-ui-detail').textContent=this.uiFaults.size?'Display faults: '+Array.from(this.uiFaults).map(([name,kind])=>name+' / '+kind).join('; '):'Display components synchronized.';
  }
  replyFault(code){
    this.replyFaults++;
    this.byId('as-reply-detail').textContent='Reply rejected: '+code+' | count '+this.replyFaults+'. Last valid state retained; freshness checks still apply.';
    this.status('Companion reply could not be read. See Connection & Build Details.');
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
    if(this.utf8Bytes(payload)>1024){this.status('Command exceeds the native limit. Use the dashboard.');return;}
    this.pending.set(requestId, {
      op: op,
      sent: Date.now(),
      planId: (fields && fields.planId) || null,
      observationText:op==='marker'?fields?.notes:null,serial:++this.serial,payload,retries:0,researchOffset:fields?.offset??0,selectionAtSend:this.mapPlan,engineAtSend:this.engineInstance,instrumentId:fields?.instrumentId,intervalMs:fields?.intervalMs,missionAtSend:this.state?.activeMission?.id||null,
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
          if(op==='instrument'){const nodes=this.instrumentNodes[fields?.instrumentId];if(nodes){nodes.pending=null;nodes.editing=true;nodes.feedback.textContent='Command delivery failed. The interval is not confirmed.';}}
          this.mutationPending=Array.from(this.pending.values()).some(p=>!['state','research-state'].includes(p.op)&&!p.op.startsWith('map-'));
          this.status(
            "Simulator command delivery failed. Check the engine connection.",
          );
        });
    } catch (error) {
      if(['science-control','research-control','marker'].includes(op)){const pending=this.pending.get(requestId);if(pending)pending.sent=Date.now()-5001;}else this.pending.delete(requestId);
      this.status("Simulator command delivery failed.");
      return;
    }
    return requestId;
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
        if(request.op==='instrument'){const nodes=this.instrumentNodes[request.instrumentId];if(nodes){nodes.pending=null;nodes.editing=true;nodes.feedback.textContent='No confirmation received. Review the confirmed interval before trying again.';}}
        if(['science-control','research-control','marker'].includes(request.op))this.byId(request.op==='marker'?'as-observation-status':request.op==='research-control'?'as-research-status':'as-collection-message').textContent='No command confirmation after one identical retry. Review synchronized state before issuing a new command.';
        if (request.op.startsWith("map-"))
          this.byId(request.op==='map-plans'?'as-plan-status':"as-map-status").textContent =
            "Map reply timed out; retrying with the cached route retained.";
        else if (request.op !== "state")
          this.status(
            "No command confirmation. Check mission state before retrying.",
          );
      }
    if (Date.now() - this.lastReply > 6000) {
      this.setControls(false);
      this.byId('as-map-plan').disabled=true;
      this.byId('as-link-detail').textContent = this.lastReply ? 'Last matching engine reply: ' + new Date(this.lastReply).toISOString() + '. Controls are unavailable until fresh synchronized state arrives.' : 'No matching engine reply. Check that the dashboard and toolbar have the same profile and source shown in Connection & Build Details.';
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
        this.updateView('map-state',()=>this.mapRenderer.update({ aircraft: null, track: [], markers: [] }));
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
    if(typeof data!=='string'){this.replyFault('non-text message');return;}
    if(this.utf8Bytes(data)>8000){this.replyFault('message exceeds 8000 UTF-8 bytes');return;}
    let reply;
    try{reply=JSON.parse(data);}catch(error){this.replyFault('malformed JSON');return;}
    if(!reply||typeof reply!=='object'||Array.isArray(reply)){this.replyFault('invalid reply envelope');return;}
    const pending=this.pending.get(reply.requestId);
    if(!pending)return; // Unsolicited, duplicate and late replies have no authority.
    if(reply.protocol!==1){this.setControls(false);this.replyFault('unsupported protocol');return;}
    if(typeof reply.ok!=='boolean'){this.replyFault('missing command outcome');return;}
    if(typeof reply.error==='string'&&reply.error.indexOf('Validation toolbar source mismatch')===0){
      this.pending.clear();this.lastReply=0;this.setControls(false);this.renderIntelligence(false);
      this.byId('as-map-plan').disabled=true;this.byId('as-reply-detail').textContent='Validation source mismatch. No state accepted.';this.status(reply.error);return;
    }
    if(!pending.op.startsWith('map-')&&!['research-state','research-control'].includes(pending.op)&&
      (!reply.state||typeof reply.state.connection?.message!=='string'||!reply.state.connection.source||
       reply.state.activeMission!=null&&typeof reply.state.activeMission.id!=='string')){
      this.replyFault('missing or invalid flight state');return;
    }
    if(reply.ok&&pending.op==='map-plans'&&(!Array.isArray(reply.map)||reply.map.length>10||reply.map.some(p=>!p||typeof p.id!=='string'||typeof p.name!=='string'))){this.replyFault('invalid saved-plan page');return;}
    this.updateView('reply processing',()=>this.processReply(reply));
  }
  processReply(reply){
      const pendingRequest = this.pending.get(reply.requestId);
      if(reply.protocol===1&&pendingRequest&&['research-state','research-control'].includes(pendingRequest.op)){
        this.pending.delete(reply.requestId);
        if(pendingRequest.serial<this.researchSerial)return;
        this.researchSerial=pendingRequest.serial;
        this.mutationPending=Array.from(this.pending.values()).some(p=>!['state','research-state'].includes(p.op)&&!p.op.startsWith('map-'));
        this.setControls(!this.mutationPending&&Date.now()-this.lastReply<=6000);this.renderIntelligence(Date.now()-this.lastReply<=6000);
        if(!reply.ok){this.byId('as-research-status').textContent=this.nativeMessage(reply.error||'Card command not confirmed. Synchronize before retrying.');this.requestResearch();return;}
        if(pendingRequest.researchOffset!==this.researchOffset){this.requestResearch();return;}
        if(reply.research?.flightId===(this.state?.activeMission?.id||null)){
          if(!this.researchState||reply.research.revision>=this.researchState.revision||reply.research.flightId!==this.researchState.flightId){this.researchState=reply.research;this.renderPersonalResearch(Date.now()-this.lastReply<=6000);}
          if(reply.receipt)this.byId('as-research-status').textContent=this.nativeMessage(reply.receipt.message.replace(/ \[manual:[^\]]+\]/g,'').replace(/ \[detection:[^\]]+\]/g,'').replace(/ \[point:[0-9a-f-]{36}; attempt:[0-9a-f-]{36}\]$/i,''));
        }return;
      }
      if (
        reply.protocol === 1 &&
        pendingRequest &&
        pendingRequest.op.startsWith("map-")
      ) {
        this.pending.delete(reply.requestId);
        if(pendingRequest.engineAtSend!==this.engineInstance||pendingRequest.op==='map-plans'&&pendingRequest.selectionAtSend!==this.mapPlan){if(pendingRequest.op==='map-plans')this.plansLoaded=false;return;}
        if (
          pendingRequest.op !== "map-plans" &&
          pendingRequest.planId !== (this.mapPlan || null)
        ) {
          this.requestMapState();
          return;
        }
        if(pendingRequest.op==='map-plans'&&pendingRequest.researchOffset!==this.planOffset)return;
        if (reply.ok && reply.map)
          this.receiveMap(pendingRequest.op, reply.map,reply.selectedPlan);
        else {
          this.byId(pendingRequest.op==='map-plans'?'as-plan-status':"as-map-status").textContent =
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
      if(pendingRequest.op==='instrument'&&pendingRequest.intervalMs!=null){
        const nodes=this.instrumentNodes[pendingRequest.instrumentId],confirmed=reply.state.research?.instruments?.find(i=>i.instrumentId===pendingRequest.instrumentId);
        if(nodes){nodes.pending=null;nodes.editing=false;nodes.confirmAfter=this.serial;nodes.draft=null;
          const accepted=reply.ok&&confirmed?.intervalMs===pendingRequest.intervalMs&&pendingRequest.engineAtSend===this.engineInstance&&pendingRequest.missionAtSend===(this.state?.activeMission?.id||null);
          nodes.feedback.textContent=accepted?'Confirmed: '+confirmed.intervalMs/1000+' s.':this.nativeMessage(reply.error||'Interval was not confirmed. Review current settings.');
          if(confirmed){nodes.interval.value=confirmed.intervalMs;nodes.confirmed.textContent='Companion interval: '+confirmed.intervalMs/1000+' s';}
        }
      }
      this.mutationPending=Array.from(this.pending.values()).some(p=>!['state','research-state'].includes(p.op)&&!p.op.startsWith('map-'));
      this.setControls(!this.mutationPending&&Date.now()-this.lastReply<=6000);
      if(pendingRequest.op==='select-plan')this.byId('as-plan-status').textContent=reply.ok?'Mission plan selection confirmed.':reply.error||'Plan selection was not confirmed. Refresh plans.';
      if(pendingRequest.op==='marker'){if(reply.ok&&this.byId('as-observation-note').value===pendingRequest.observationText)this.byId('as-observation-note').value='';this.byId('as-observation-status').textContent=reply.ok?'Pilot Observation saved. Flight and science continue independently.':reply.error||'Observation was not saved; your text is preserved.';}
      if(pendingRequest.serial<this.appliedSerial){if(pendingRequest.op==='science-control')this.byId('as-collection-message').textContent=(reply.error||reply.receipt?.reason||'Earlier request acknowledged')+' | A newer authoritative view is already displayed.';return;}
      if(reply.engineInstance&&reply.engineInstance!==this.engineInstance&&this.seenEngines.has(reply.engineInstance))return;
      if(reply.intelligence&&this.intelligence&&reply.engineInstance===this.engineInstance&&reply.intelligence.missionId===this.intelligence.missionId&&reply.intelligence.revision<this.intelligence.revision)return;
      this.appliedSerial=pendingRequest.serial;
      this.lastReply = Date.now();
      this.byId('as-link-detail').textContent = 'Matching engine reply received ' + new Date(this.lastReply).toISOString() + '. Aircraft telemetry and scientific collection have their own status below.';
      const engineChanged=this.state&&(this.state.version!==reply.state.version||reply.engineInstance&&this.engineInstance!==reply.engineInstance);
      if(reply.engineInstance){this.engineInstance=reply.engineInstance;this.seenEngines.add(reply.engineInstance);}
      this.capabilities=Array.isArray(reply.capabilities)?reply.capabilities:[];
      const candidate=reply.intelligence;
      this.intelligence=this.capabilities.includes('mission-intelligence-1')&&candidate?.version===1&&
        candidate.missionId===(reply.state.activeMission?.id??null)&&Array.isArray(candidate.actions)&&Array.isArray(candidate.metrics)&&Array.isArray(candidate.warnings)&&Number.isFinite(Date.parse(candidate.generatedUtc))?candidate:null;
      this.state = reply.state;
      // Only accepted, correlated, current-engine replies can select a catalog.
      if(typeof AeroScienceI18n !== 'undefined'){
        if(this.presentationEngine!==this.engineInstance){this.presentationEngine=this.engineInstance;this.presentationRevision=-1;}
        const metadata=this.capabilities.includes('presentation-1')?reply.presentation:null;
        const revision=metadata&&Number.isFinite(metadata.revision)?metadata.revision:0;
        if(!metadata||revision>=(this.presentationRevision==null?-1:this.presentationRevision)){
          AeroScienceI18n.apply(this,metadata);if(metadata)this.presentationRevision=revision;
        }
      }
      if(engineChanged){this.plansLoaded=false;this.selectedPlan=null;this.planRows=[];this.planFormId=undefined;this.mapPlan=null;}
      const mutationPending = Array.from(this.pending.values()).some(
        (x) => x.op !== "state" && x.op !== "research-state" && !x.op.startsWith("map-"),
      );
      this.mutationPending=mutationPending;
      this.setControls(!mutationPending);
      this.updateView('flight',()=>this.render(reply.state));
      this.renderIntelligence(true);
      this.updateView('plan selection',()=>this.synchronizePlan(reply.state));
      if(pendingRequest.op==='science-control')this.byId('as-collection-message').textContent=reply.receipt?.reason||reply.error||'Session command confirmed. Review current state.';
      if(!this.plansLoaded&&!Array.from(this.pending.values()).some(p=>p.op==="map-plans")){this.planOffset=0;this.requestPlans();}
      if (!reply.ok)
        this.status(reply.error || "Mission command was rejected.");
  }
  render(state) {
    const t = state.telemetry,
      mission = state.activeMission;
    if(mission){this.byId("as-start-plan").textContent="Active recording: its saved plan snapshot is fixed. Plan selection for the next recording is separate.";}

    const fresh = t && t.isValid!==false&&!t.isPaused&&(state.connection.state==='connected'||state.connection.source==='simulated'&&!state.connection.state) && Date.now() - Date.parse(t.timestampUtc) < 6000 && Date.now()-Date.parse(t.timestampUtc)>=-2000;
    this.byId("as-source").textContent =
      state.connection.source === "simulated"
        ? "SIMULATED TEST DATA"
        : fresh && state.connection.state === "connected"
          ? "SIMCONNECT TELEMETRY"
          : state.connection.state==='connected'?'AIRCRAFT TELEMETRY UNAVAILABLE':"MSFS DISCONNECTED";
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
      this.planFormId=undefined;
      this.byId("as-mode").value = "universal";
      this.byId("as-mode-help").textContent =
        "All compatible aircraft can record missions.";
      this.byId("as-name").value = "";
      }this.renderedMission=mission.id;
      this.byId("as-active-name").textContent = mission.name;
      this.byId("as-active-plan").textContent=state.research?.planId?"Active saved-plan snapshot (fixed for this recording). Fly the route reference on Map.":"Active unplanned mission: scientific preparation follows actual aircraft movement.";
      this.byId("as-active-mode").textContent =
        mission.mode === "realistic"
          ? "Realistic Mode"
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
  nativeLabel(value){
    const labels={unknown:'Unavailable',unavailable:'Unavailable',notGraded:'Not graded',betweenSessions:'Between sessions',insufficientEvidence:'Insufficient evidence',notAssessed:'Not assessed',valid:'Valid',invalid:'Invalid',stale:'Stale',missing:'Missing',armed:'Armed',collecting:'Collecting',paused:'Paused',stopped:'Stopped',awaitingSource:'Waiting for source',acquisitionGap:'Acquisition gap',sourceDisconnected:'Source disconnected',waiting:'Waiting',manual:'Manual',automatic:'Automatic',unsupported:'Unsupported',provisional:'Provisional',final:'Final',revised:'Revised',supported:'Supported',caution:'Caution',disabled:'Disabled',idle:'Idle',enabled:'Enabled',detecting:'Detecting',holding:'Holding',completed:'Complete',insufficient:'Insufficient evidence'};
    return labels[value]||'Unavailable';
  }
  nativeUnit(unit){return String(unit||'').replace(/°/g,'deg').replace(/³/g,'^3').replace(/²/g,'^2').replace(/µ/g,'u');}
  renderIntelligence(connected){
    this.renderCollectionBanner(connected);
    this.updateView('science',()=>this.renderIntelligenceView(connected));
  }
  renderIntelligenceView(connected){
    const i=this.intelligence,supported=this.capabilities.includes('session-bound-controls-1')&&i?.version===1;
    const fresh=connected&&supported&&Date.now()-Date.parse(i.generatedUtc)<=6000&&Date.now()-Date.parse(i.generatedUtc)>=-2000;
    const model=this.flightPresentation(connected),phase=i?.phase,preferred=phase==='collecting'?'pauseData':phase==='paused'?'resumeData':'startData';
    const primary=model.scienceActions.includes(preferred)?preferred:model.scienceActions.includes('rearmAutomation')?'rearmAutomation':null;
    for(const button of this.querySelectorAll('[data-science-action]')){const action=button.dataset.scienceAction;button.disabled=!fresh||this.mutationPending||!model.scienceActions.includes(action)||!i.actions.includes(action);button.classList.toggle('as-primary',action===primary);button.hidden=!this.state?.activeMission||(action==='startData'?!!i?.sessionId:action==='pauseData'?phase!=='collecting':action==='resumeData'?phase!=='paused':action==='endData'?!i?.sessionId:!i?.manualHold&&!i?.continuityHold&&i?.authority!=='automatic');button.title=!supported?'This companion does not support session-bound native controls.':!fresh?'Stale engine state; synchronize before controlling science.':'Backend session and control revisions are checked before applying.';}
    const counts=model.counts,age=Date.now()-Date.parse(counts?.checkedUtc),validCounts=counts&&age>=-2000&&age<=6000&&['originalObservations','derivedObservations','remoteProducts'].every(key=>Number.isSafeInteger(counts[key])&&counts[key]>=0);
    this.byId('as-retained-science').textContent=validCounts?this.text('toolbar.retained','Saved science counts supplied by companion.',{original:counts.originalObservations,derived:counts.derivedObservations,products:counts.remoteProducts}):this.text('toolbar.countsUnavailable','Saved scientific counts unavailable; no count inferred from flight samples.');
    this.byId('as-retained-science').dataset.originalObservations=validCounts?String(counts.originalObservations):'';this.byId('as-retained-science').dataset.remoteProducts=validCounts?String(counts.remoteProducts):'';
    if(!supported){this.byId('as-session-phase').textContent='Science status unavailable (unsupported companion)';this.byId('as-session-authority').textContent='Update the companion for science session controls.';this.byId('as-grade').textContent='No score available.';this.byId('as-session-identity').textContent='No session identity supplied.';this.byId('as-next-trigger').textContent='Next trigger not assessed.';this.byId('as-science-yield').textContent='Scientific yield not assessed.';this.byId('as-intelligence-warning').textContent='';this.byId('as-intelligence-asof').textContent='';return;}
    this.byId('as-session-phase').textContent=(i.phase==='unknown'?'Science status unavailable':(fresh?'Science: ':'Last science status: ')+this.nativeLabel(i.phase))+(i.sessionSequence?' | Session '+i.sessionSequence:!i.sessionId&&i.phase==='armed'?' | Science not started':'')+(!fresh?' | Status is stale':'');
    this.byId('as-session-authority').textContent='Collection control: '+this.nativeLabel(i.authority)+(i.manualHold?' | pilot hold; rearm required':'')+(i.continuityHold?' | continuity hold':'');
    this.byId('as-session-identity').textContent='Flight '+(i.missionId||'none')+' | Session '+(i.sessionId||'none')+' | revision '+(i.revision??'Unknown');
    this.byId('as-grade').textContent=this.nativeLabel(i.gradeStatus)+(Number.isFinite(i.scoreLower)&&Number.isFinite(i.scoreUpper)?' | score bounds '+i.scoreLower.toFixed(1)+'-'+i.scoreUpper.toFixed(1)+' / 100':' | No score available')+'.';
    this.byId('as-intelligence-warning').textContent=this.nativeMessage(i.warnings?.[0]||'');
    this.byId('as-next-trigger').textContent=this.nativeMessage(i.triggerStatus);
    this.byId('as-science-yield').textContent=i.metrics.length?i.metrics.map(m=>m.instrumentId+' | '+m.id+': '+m.status+(['supported','caution'].includes(m.status)&&Number.isFinite(m.numerator)&&m.denominator>0?' '+m.numerator+'/'+m.denominator+' assessed '+m.unit+' ('+(100*m.numerator/m.denominator).toFixed(1)+'%)':' - unassessed')+' | missing '+(m.missing??'Unknown')+'; unassessed '+(m.unassessed??'Unknown')+' | whole-flight scope').join('; '):'Good Data / valid scientific coverage not yet assessed. Flight path is not valid-pixel coverage.';
    this.byId('as-intelligence-asof').textContent='State as of '+i.generatedUtc+' | metrics '+(i.metricsUtc||'Pending')+(i.metricsUtc&&Date.now()-Date.parse(i.metricsUtc)>15000?' | Evidence cutoff is older than 15 s; metrics are not current.':'')+(i.detailsOmitted?' | Additional detail on dashboard.':'');
  }
  requestResearch(){
    if(!this.capabilities.includes('personal-research-controls-1')||!this.state?.activeMission){this.researchState=null;this.researchOffset=0;this.researchFocusedCard=null;this.focusedResearchSummary=null;this.renderPersonalResearch(false);return;}
    if(this.researchState&&this.researchState.flightId!==this.state.activeMission.id){this.researchOffset=0;this.researchFocusedCard=null;this.focusedResearchSummary=null;this.researchPointChoices={};}
    if(Array.from(this.pending.values()).some(p=>['research-state','research-control'].includes(p.op)))return;
    this.send('research-state',{missionId:this.state.activeMission.id,offset:this.researchOffset});
  }
  renderPersonalResearch(fresh){
    this.updateView('research',()=>this.renderPersonalResearchView(fresh));
  }
  renderPersonalResearchView(fresh){
    const state=this.researchState,host=this.byId('as-research-list');
    const relevant=!!this.state?.activeMission&&state?.flightId===this.state.activeMission.id&&(state.totalCards>0||state.cards?.length>0||state.activeCardCount>0);
    const cardTab=this.byId('as-research-page');cardTab.hidden=!relevant;
    if(!relevant&&this.currentPage==='research'){this.page(false);cardTab===document.activeElement&&this.byId('as-mission-page').focus();}
    const valid=fresh&&Date.now()-Date.parse(state?.generatedUtc)<6000&&state?.flightId===this.state?.activeMission?.id&&state?.recording;
    const pending=Array.from(this.pending.values()).some(p=>p.op==='research-control');
    const summary=this.byId('as-operations-summary');
    summary.hidden=!relevant;
    const selected=state?.cards.find(c=>c.card.id===this.researchFocusedCard);
    if(selected)this.focusedResearchSummary=selected.card.name+' | '+(selected.openAttempt?'Attempt recording':selected.active?'Active':'Inactive')+(selected.procedure?.nextPointId?' | Next point '+(selected.card.procedure?.points.find(p=>p.id===selected.procedure.nextPointId)?.conditions.testPoint.name||'on dashboard'):'');
    summary.textContent=!valid?this.state?.activeMission?'Research status unavailable.':'Personal research cards: prepare in the desktop Flight Planner.':(this.focusedResearchSummary?'Focused: '+this.focusedResearchSummary+'. ':'Research: ')+(state.activeCardCount??state.cards.filter(c=>c.active).length)+' active card(s)'+(state.activeCardNames?.length?' | '+state.activeCardNames.join('; '):'')+'.';
    // A telemetry render can run after offset changes but before that page reply.
    // Same-revision pages still carry distinct authoritative card identities.
    const signature=JSON.stringify([state?.flightId,state?.revision,this.researchOffset,state?.cards?.map(item=>[item.card.id,item.card.revision]),state?.nextOffset,state?.pageSize,valid,state?.canBeginAttempt,pending,this.researchFocusedCard]);
    if(this.researchView===signature)return;this.researchView=signature;
    const focusCard=document.activeElement?.dataset?.researchCard,focusAction=document.activeElement?.dataset?.researchAction;
    host.textContent='';this.byId('as-research-previous').disabled=!valid||this.researchOffset===0;this.byId('as-research-next').disabled=!valid||state?.nextOffset==null;
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
      const requirements=procedure?(procedure.ordered?'Ordered':'Independent')+' procedure | '+(item.procedure?.points||[]).map(p=>p.completedRepetitions+'/'+p.requiredRepetitions).join(' | ')+' repetitions; verify originals after flight':point?[point.airspeedRequired?rule.airspeedKnots+' +/- '+rule.airspeedToleranceKnots+' knots IAS':null,point.altitudeRequired?rule.altitudeMslFeet+' +/- '+rule.altitudeToleranceFeet+' feet MSL':null].filter(Boolean).join(' AND ')+' | '+rule.holdSeconds+' continuous seconds':sensor?'Fictional modeled temperature | '+sensor.responseSeconds+' s response | '+sensor.minimumSamples+' original science samples required':observation?observation.minimumObservations+' documented Pilot Observations | notes are not sensor measurements':survey?'Survey: '+survey.measurements.map(m=>({airTemperature:'Air Temperature',terrainElevation:'Terrain Elevation',landSurfaceTemperature:'Land Surface Temperature'})[m]).join(' + ')+' | source-qualified samples required':transect?transect.requiredStations+' distinct sampling locations | '+transect.minimumExtentMetres+' m geographic extent | collect valid temperature readings':atmospheric?atmospheric.requiredBands+' neighboring '+atmospheric.bandWidthFeet+'-foot MSL bands | collect valid temperature readings':item.card.flightRegime.airspeedKnots+' knots IAS | '+item.card.flightRegime.altitudeMslFeet+' feet MSL';
      title.textContent=item.card.name+' | revision '+item.card.revision;info.textContent=(item.openAttempt?'Attempt recording':item.active?'Active':'Inactive')+' | '+(item.detection?item.detection.status+(item.detection.lastOutcome?' - '+({pass:'Pass',fail:'Fail',insufficientEvidence:'Insufficient Evidence'}[item.detection.lastOutcome]||'Unknown'):'')+' | ':'')+requirements+'. '+(item.activationBlocked||'');info.textContent=this.nativeMessage(info.textContent);row.appendChild(title);row.appendChild(info);
      if(item.organization){const label=document.createElement('p');label.textContent=item.organization.organizationName+' | issued '+item.organization.issuedRevision+' | '+item.organization.status;row.appendChild(label);}
      const focus=document.createElement('button');focus.textContent=this.researchFocusedCard===item.card.id?'Focused in Toolbar':'Focus Card';focus.onclick=()=>{this.researchFocusedCard=item.card.id;this.researchView=null;this.renderPersonalResearch(true);this.byId('as-research-list').querySelector('[data-research-card="'+item.card.id+'"]')?.focus();};row.appendChild(focus);
      let pointSelect=null;
      if(procedure){const label=document.createElement('label');label.textContent='Test point';pointSelect=document.createElement('select');pointSelect.setAttribute('aria-label','Test point for '+item.card.name);this.researchPointChoices||={};for(const point of procedure.points){const option=document.createElement('option');option.value=point.id;option.textContent=point.conditions.testPoint.name+' | '+point.conditions.holdSeconds+' s | '+point.repetitions+' attempts';pointSelect.appendChild(option);}pointSelect.value=item.openAttempt?.testPointId||this.researchPointChoices[item.card.id]||item.procedure?.nextPointId||procedure.points[0].id;pointSelect.disabled=pending||!!item.openAttempt;pointSelect.onchange=()=>this.researchPointChoices[item.card.id]=pointSelect.value;label.appendChild(pointSelect);row.appendChild(label);}
      for(const [action,label] of [['activate','Activate Card'],['deactivate','Deactivate Card'],['beginAttempt','Begin Attempt'],['endAttempt','End Attempt'],...((item.card.version===7&&point)||procedure?[['enableAutomatic','Enable Automatic Detection'],['disableAutomatic','Disable Automatic Detection']]:[])]){
        const b=document.createElement('button');b.textContent=label;b.dataset.researchCard=item.card.id;b.dataset.researchAction=action;
        b.disabled=pending||action==='activate'&&(item.active||!!item.activationBlocked)||action==='deactivate'&&!item.active||action==='beginAttempt'&&(!item.active||!!item.openAttempt&&!item.openAttempt.automatic||!state.canBeginAttempt)||action==='endAttempt'&&!item.openAttempt||action==='enableAutomatic'&&(!item.active||!!item.openAttempt||!state.canBeginAttempt)||action==='disableAutomatic'&&!item.detection?.enabled;
        b.onclick=()=>{if(b.disabled)return;if(action==='endAttempt'&&this.researchPointChoices)delete this.researchPointChoices[item.card.id];const requestId=this.requestId();this.send('research-control',{requestId,missionId:state.flightId,offset:this.researchOffset,researchControl:{version:1,flightId:state.flightId,requestId,expectedRevision:state.revision,action,cardId:item.card.id,expectedAttemptId:item.openAttempt?.id||null,...(['beginAttempt','enableAutomatic'].includes(action)&&pointSelect?{testPointId:pointSelect.value}:{})}});this.renderPersonalResearch(true);};row.appendChild(b);
      }host.appendChild(row);
    }
    if(focusCard){const choices=Array.from(host.querySelectorAll('button')).filter(b=>b.dataset.researchCard===focusCard&&!b.disabled);const target=choices.find(b=>b.dataset.researchAction===focusAction)||choices[0]||Array.from(host.children).find(r=>r.dataset.researchCard===focusCard);target?.focus();}
  }
  renderResearch(research, fresh) {
    this.updateView('payload',()=>this.renderResearchView(research,fresh));
  }
  renderResearchView(research, fresh) {
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
        ? this.text('toolbar.acquisitionState','Instrument acquisition status: '+this.nativeLabel(research.collectionStatus),{status:this.nativeLabel(research.collectionStatus)})
        : "Science unavailable";
    const holder = this.byId("as-instruments");
    if (!research) {
      holder.textContent = "Scientific source unavailable";
      return;
    }
    if (!this.instrumentNodes) this.instrumentNodes = {};
    if (!holder.querySelector(".as-instrument")) holder.textContent = "";
    let disabled=holder.querySelector('[data-disabled-instruments]');
    if(!disabled){disabled=document.createElement('details');disabled.dataset.disabledInstruments='';const title=document.createElement('summary'),body=document.createElement('div');body.className='as-disabled-instruments';disabled.appendChild(title);disabled.appendChild(body);holder.appendChild(disabled);}
    const remaining=new Set(Object.keys(this.instrumentNodes));let disabledCount=0;
    for (const instrument of research.instruments || []) {
      remaining.delete(instrument.instrumentId);
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
            "terrain-scanner": this.text("toolbar.terrainScanner","Terrain Scanner (DEM Elevation)"),
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
          status = document.createElement("p"),acquisitionSummary=document.createElement('p'),sourceSummary=document.createElement('p');
        acquisitionSummary.className=sourceSummary.className='as-note';
        values.className = "as-science-values";
        const details = document.createElement("details"),
          summary = document.createElement("summary");
        summary.textContent = "Diagnostics";
        details.appendChild(summary);
        details.appendChild(status);
        details.appendChild(values);
        const intervalLabel = document.createElement("label");
        intervalLabel.textContent = "Collection interval ";
        intervalLabel.appendChild(interval);
        interval.setAttribute('aria-label',title.textContent+' collection interval');
        const apply=document.createElement('button'),cancel=document.createElement('button'),confirmed=document.createElement('p'),feedback=document.createElement('p');
        apply.textContent='Apply interval';apply.type='button';apply.setAttribute('aria-label','Apply '+title.textContent+' interval');feedback.setAttribute('role','status');
        cancel.textContent='Use confirmed interval';cancel.type='button';cancel.hidden=true;cancel.setAttribute('aria-label','Use confirmed '+title.textContent+' interval');
        row.appendChild(title);
        row.appendChild(stateLabel);
        row.appendChild(acquisitionSummary);row.appendChild(sourceSummary);
        row.appendChild(toggle);
        row.appendChild(intervalLabel);
        row.appendChild(apply);row.appendChild(cancel);row.appendChild(confirmed);row.appendChild(feedback);
        row.appendChild(details);
        holder.insertBefore(row,disabled);
        nodes = {
          row: row,
          state: stateLabel,
          toggle: toggle,
          interval: interval,
          values: values,
          status: status,
          acquisitionSummary,sourceSummary,
          apply,cancel,confirmed,feedback,editing:false,draft:null,pending:null,confirmAfter:null,
        };
        this.instrumentNodes[instrument.instrumentId] = nodes;
        toggle.onclick = () => {
          if(toggle.hasAttribute("disabled")||nodes.pending||!this.state?.research)return;
          const current = this.state.research.instruments.find(
            (i) => i.instrumentId === instrument.instrumentId,
          );
          this.send("instrument", {
            instrumentId: instrument.instrumentId,
            enabled: !current.enabled,
          });
        };
        const edit=()=>{nodes.editing=true;cancel.hidden=false;};interval.onmousedown=edit;interval.onkeydown=edit;
        interval.onchange=()=>{nodes.editing=true;nodes.draft=Number(interval.value);feedback.textContent='Choose Apply interval to confirm this change.';apply.disabled=false;};
        apply.onclick=()=>{
          const value=Number(interval.value);if(apply.disabled||nodes.pending||![1000,2000,5000,10000,30000,60000].includes(value))return;
          nodes.draft=value;nodes.pending=this.send('instrument',{instrumentId:instrument.instrumentId,intervalMs:value,missionId:this.state?.activeMission?.id||null,checkMission:true});
          feedback.textContent=nodes.pending?'Applying interval...':'Command unavailable. Synchronize with the companion.';apply.disabled=true;
        };
        cancel.onclick=()=>{if(nodes.pending)return;const current=this.state?.research?.instruments.find(i=>i.instrumentId===instrument.instrumentId);if(current){nodes.editing=false;nodes.draft=null;interval.value=current.intervalMs;feedback.textContent='Using confirmed interval: '+current.intervalMs/1000+' s.';cancel.hidden=true;apply.disabled=true;}};
      }
      nodes.toggle.textContent = instrument.enabled ? "On" : "Off";
      nodes.toggle.setAttribute(
        "aria-label",
        instrument.enabled ? "Disable instrument" : "Enable instrument",
      );
      // The dashboard can retain a supported interval outside the toolbar's quick choices.
      // Keep that authoritative value visible without offering arbitrary new frequencies.
      const retained=Array.from(nodes.interval.options).find(o=>o.dataset.retained==='true');
      if(retained&&Number(retained.value)!==instrument.intervalMs&&!nodes.editing&&!nodes.pending)retained.remove();
      if(!Array.from(nodes.interval.options).some(o=>Number(o.value)===instrument.intervalMs)){
        const option=document.createElement('option');option.value=instrument.intervalMs;option.textContent=instrument.intervalMs/1000+' s (confirmed)';option.dataset.retained='true';nodes.interval.appendChild(option);
      }
      if(nodes.confirmAfter!=null&&this.appliedSerial>nodes.confirmAfter)nodes.confirmAfter=null;
      if(!nodes.editing&&!nodes.pending&&nodes.confirmAfter==null)nodes.interval.value=instrument.intervalMs;
      nodes.confirmed.textContent='Companion interval: '+instrument.intervalMs/1000+' s';
      nodes.acquisitionSummary.textContent=this.text('toolbar.acquisitionSummary','Acquisition diagnostics unavailable.',{recorded:instrument.observationCount??'Unknown',rejected:instrument.rejectedObservations??'Unknown',missed:instrument.missingIntervals??'Unknown',interval:Number.isFinite(instrument.actualAverageIntervalSeconds)?instrument.actualAverageIntervalSeconds.toFixed(2):'Unavailable'});
      nodes.sourceSummary.textContent=this.nativeMessage(instrument.message||this.text('toolbar.sourceUnknown','Source/quality details unavailable; review the dashboard.'));
      nodes.apply.disabled=!fresh||!!nodes.pending||nodes.confirmAfter!=null||Number(nodes.interval.value)===instrument.intervalMs;
      nodes.interval.disabled=!fresh||!!nodes.pending||nodes.confirmAfter!=null;
      nodes.cancel.hidden=!nodes.editing&&!nodes.pending;nodes.cancel.disabled=!fresh||!!nodes.pending;
      nodes.values.textContent = "";
      if(instrument.payloadProfileName){const profile=document.createElement('div');profile.className='as-science-reading';profile.textContent=this.nativeMessage(instrument.payloadProfileName);nodes.values.appendChild(profile);}
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
          this.nativeUnit(v.displayUnit || v.unit);
        nodes.values.appendChild(value);
      }
      nodes.state.textContent = instrument.enabled
        ? this.nativeLabel(instrument.quality)
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
      const target=instrument.enabled?holder:disabled.lastChild;if(!instrument.enabled)disabledCount++;
      if(nodes.row.parentNode!==target){const focus=nodes.row.contains(document.activeElement)?document.activeElement:null;if(focus&&!instrument.enabled)disabled.open=true;if(target===holder)holder.insertBefore(nodes.row,disabled);else target.appendChild(nodes.row);if(focus)focus.focus();}
    }
    for(const id of remaining){const row=this.instrumentNodes[id].row;if(row.parentNode)row.parentNode.removeChild(row);delete this.instrumentNodes[id];}
    disabled.hidden=!disabledCount;disabled.firstChild.textContent=this.text('toolbar.disabledInstruments','Disabled instruments',{count:disabledCount});
  }
  disconnectedCallback() {
    this.aeroScienceConnected=false;
    this.connectionEpoch=(this.connectionEpoch||0)+1;
    clearTimeout(this.initializationTimer);
    document.removeEventListener("DOMContentLoaded", this.onDocumentReady);
    this.pending?.clear();
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
