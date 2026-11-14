import {presentationTextNode} from './i18n.js';
import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import { newIdentity } from "./workstation-ui.js";
const node=(tag,text)=>{const e=document.createElement(tag);if(text!==undefined)setPresentationText(e,text);return e;};
// Mounted inside the existing instrument editor, also used by saved plans/presets.
export function calibrationSettings(parent,config,catalog,api){
 const info=catalog?.instruments?.find(i=>i.specification.definition.id===config.instrumentId);
 if(!info)return ()=>config.calibration??null;
 const details=node('details'),body=node('div');details.className='calibration-settings';details.dataset.calibrationInstrument=config.instrumentId;
 details.append(node('summary',localizedCopy("ui.calibrationSettings.messageCalibrationProvenance")),body);parent.append(details);
 body.append(node('p',info.reference.limitations.join(' ')));
 body.append(node('p',localizedCopy("ui.calibrationSettings.messageReferenceStatusReferenceSpecificationR",{value1:(String(info.reference.status)),value2:(String(info.specification.revision)),value3:(String(info.specificationSha256))})));
 const label=node('label',localizedCopy("ui.calibrationSettings.messageCalibrationDefinition")),choice=node('select');setPresentationText(choice,localizedCopy("ui.calibrationSettings.messageCalibrationDefinition"),"aria-label");label.append(choice);body.append(label);
 let selected=structuredClone(config.calibration??null),dirty=false;
 const status=node('p'),provenance=node('div'),editor=node('div');editor.hidden=true;status.setAttribute('role','status');body.append(status,provenance);
 function options(){
  choice.replaceChildren();const none=node('option',localizedCopy("ui.calibrationSettings.messageOriginalValuesReferenceOnly"));none.value='';choice.append(none);
  const definitions=(catalog.definitions??[]).map(r=>r.definition).filter(d=>d.instrumentId===config.instrumentId);
  if(selected&&!definitions.some(d=>d.id===selected.id&&d.revision===selected.revision))definitions.push(selected);
  for(const d of definitions){const option=node('option',`${d.id} · r${d.revision} · ${d.status}`);option.value=`${d.id}:${d.revision}`;choice.append(option);}
  choice.value=selected?`${selected.id}:${selected.revision}`:'';
  setPresentationText(status,selected?localizedCopy("ui.calibrationSettings.messageRevision",{value1:(String(selected.status)),value2:(String(selected.revision)),value3:(String(selected.adjustment?'Separate user-adjusted scalar channel; original values remain recorded.':'Metadata only; no numeric adjustment.'))}):localizedCopy("ui.calibrationSettings.messageCalibrationAccuracyIsUnknownOriginalValuesRemain"));
  provenance.replaceChildren();
  if(selected){
   provenance.append(node('p',localizedCopy("ui.calibrationSettings.messageAuthorDefined",{value1:(String(selected.author??'Unknown')),value2:(String(selected.definedUtc??'Unknown'))})));
   for(const p of selected.parameters??[])provenance.append(node('p',`${p.id}: ${p.value} ${p.unit} · ${p.basis}`));
   provenance.append(node('p',(selected.limitations??[]).join(' ')));
  }
  for(const reference of new Set([...(info.specification.references??[]),...(selected?.references??[])])){
   try{const url=new URL(reference);if(!['http:','https:'].includes(url.protocol))continue;const a=node('a',localizedCopy("ui.calibrationSettings.messageSourceReference"));a.href=url.href;a.target='_blank';a.rel='noopener noreferrer';provenance.append(a,presentationTextNode(' '));}catch{}
  }
  return definitions;
 }
 let available=options();choice.onchange=()=>{selected=structuredClone(available.find(d=>`${d.id}:${d.revision}`===choice.value)??null);dirty=false;editor.hidden=true;available=options();};
 const create=node('button',localizedCopy("ui.calibrationSettings.messageCreateOrReviseUserCalibration"));create.type='button';create.className='secondary';create.disabled=!catalog.canConfigure;body.append(create,editor);
 if(!catalog.canConfigure)body.append(node('p',localizedCopy("ui.calibrationSettings.messageCreateCalibrationDefinitionsOnTheCompanionPC")));
 if(catalog.warning)body.append(node('p',catalog.warning));
 function field(label,value='',type='text'){
  const wrap=node('label',label),input=node(type==='textarea'?'textarea':'input');if(type!=='textarea')input.type=type;
  if(type==='number')input.step='any';input.value=value??'';setPresentationText(input,label,"aria-label");wrap.append(input);editor.append(wrap);input.oninput=()=>{dirty=true;};return input;
 }
 create.onclick=()=>{
  editor.replaceChildren();editor.hidden=false;dirty=true;
  const prior=selected?.status==='userDefined'?selected:null;
  const id=prior?.id??'user.'+newIdentity();
  const revision=Math.max(0,...(catalog.definitions??[]).filter(r=>r.definition.id===id).map(r=>r.definition.revision))+1;
  editor.append(node('p',localizedCopy("ui.calibrationSettings.messageNewImmutableRevisionSaveThisRevision",{value1:(String(revision))})));
  const author=field(localizedCopy("ui.calibrationSettings.messageCalibrationAuthor"),prior?.author),basis=field(localizedCopy("ui.calibrationSettings.messageBasisProvenance"),prior?.parameters?.[0]?.basis??prior?.limitations?.[1]??'', 'textarea');
  const reference=field(localizedCopy("ui.calibrationSettings.messagePublicReferenceURLOptional"),prior?.references?.[0]);
  const channels=info.channels.filter(c=>c.supportsAdjustment),applyLabel=node('label'),apply=node('input');apply.type='checkbox';apply.checked=!!prior?.adjustment;apply.disabled=!channels.length;
  applyLabel.append(apply,' Add a separate user-adjusted scalar channel');editor.append(applyLabel);
  const channelLabel=node('label',localizedCopy("ui.calibrationSettings.messageOriginalScalarChannel")),channel=node('select');setPresentationText(channel,localizedCopy("ui.calibrationSettings.messageOriginalScalarChannel"),"aria-label");
  for(const c of channels){const option=node('option',`${c.measurementId} (${c.unit})`);option.value=c.measurementId;channel.append(option);}channel.value=prior?.adjustment?.measurementId??channels[0]?.measurementId??'';channelLabel.append(channel);editor.append(channelLabel);
  const gain=field(localizedCopy("ui.calibrationSettings.messageGainDimensionless"),prior?.parameters?.find(p=>p.id==='gain')?.value??1,'number');
  const offset=field(localizedCopy("ui.calibrationSettings.messageOffsetOriginalChannelUnit"),prior?.parameters?.find(p=>p.id==='offset')?.value??0,'number');
  const minimum=field(localizedCopy("ui.calibrationSettings.messageMinimumOriginalValueOptional"),prior?.adjustment?.minimumInput,'number'),maximum=field(localizedCopy("ui.calibrationSettings.messageMaximumOriginalValueOptional"),prior?.adjustment?.maximumInput,'number');
  const from=field(localizedCopy("ui.calibrationSettings.messageValidFromUTCOptionalISO8601"),prior?.adjustment?.validFromUtc),until=field(localizedCopy("ui.calibrationSettings.messageValidUntilUTCOptionalISO8601"),prior?.adjustment?.validUntilUtc);
  function toggle(){for(const f of [channel,gain,offset,minimum,maximum,from,until])f.disabled=!apply.checked;dirty=true;}apply.onchange=toggle;toggle();
  if(!channels.length)editor.append(node('p',localizedCopy("ui.calibrationSettings.messageExternalSourcePixelsCannotBeRecalibratedHere")));
  const save=node('button',localizedCopy("ui.calibrationSettings.messageSaveCalibrationRevision"));save.type='button';save.className='secondary';editor.append(save);
  save.onclick=async()=>{
   save.disabled=true;
   try{
    if(!author.value.trim()||!basis.value.trim())throw Error(textForUI("ui.calibrationSettings.messageProvideTheAuthorAndBasisForThis"));
    const c=channels.find(c=>c.measurementId===channel.value);
    if(apply.checked&&(!c||gain.value===''||offset.value===''))throw Error(textForUI("ui.calibrationSettings.messageProvideFiniteGainAndOffsetValuesFor"));
    const utc=value=>{if(!value.trim())return null;if(!value.trim().endsWith('Z')||!Number.isFinite(Date.parse(value)))throw Error(textForUI("ui.calibrationSettings.messageUseUTCDatesEndingInZOr"));return new Date(value).toISOString();};
    const definition={version:1,id,revision,instrumentId:config.instrumentId,status:'userDefined',author:author.value.trim(),definedUtc:new Date().toISOString(),
     parameters:apply.checked?[{id:'gain',value:Number(gain.value),unit:'1',basis:basis.value.trim()},{id:'offset',value:Number(offset.value),unit:c.unit,basis:basis.value.trim()}]:[],
     references:reference.value.trim()?[reference.value.trim()]:[],limitations:[textForUI("ui.calibrationSettings.messageUserDefinedConfigurationNoCertificationOrMeasurementAccuracy"),basis.value.trim()],
     adjustment:apply.checked?{method:'user-affine-1',measurementId:c.measurementId,unit:c.unit,minimumInput:minimum.value===''?null:Number(minimum.value),maximumInput:maximum.value===''?null:Number(maximum.value),validFromUtc:utc(from.value),validUntilUtc:utc(until.value)}:null};
    const result=await api('/api/instruments/calibration',definition);selected=result.definition;
    catalog.definitions??=[];catalog.definitions.push(result);dirty=false;editor.hidden=true;available=options();
   }catch(error){setPresentationText(status,error.message);}finally{save.disabled=false;}
  };
 };
 return ()=>{if(dirty)throw Error(textForUI("ui.calibrationSettings.messageSaveTheCalibrationRevisionOrReselectAn"));return structuredClone(selected);};
}
