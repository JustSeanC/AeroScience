// Optional developer-only OFFLINE draft tool. Never loaded by the application.
// Pin @huggingface/transformers 3.8.1 outside the repository. Download the pinned
// public model files in the review receipt first. Candidate output != human review.
import fs from'node:fs';import crypto from'node:crypto';import{pathToFileURL}from'node:url';
const locale=process.argv[2],model={'es-ES':'opus-mt-en-es','fr-FR':'opus-mt-en-fr','de-DE':'opus-mt-en-de','pt-BR':'opus-mt-en-ROMANCE'}[locale];
if(!model||!process.env.P07_TRANSLATION_TOOLS||!process.env.P07_TRANSLATION_MODELS)throw Error('Set isolated developer tooling/model paths and select an approved candidate locale.');
const{pipeline,env}=await import(pathToFileURL(process.env.P07_TRANSLATION_TOOLS+'/node_modules/@huggingface/transformers/dist/transformers.node.mjs'));
env.allowRemoteModels=false;env.localModelPath=process.env.P07_TRANSLATION_MODELS+'/';
const translate=await pipeline('translation','Xenova/'+model,{dtype:'q8',device:'cpu',session_options:{intraOpNumThreads:1,interOpNumThreads:1}});
const base='src/AeroScience.Engine/wwwroot/i18n/',english=JSON.parse(fs.readFileSync(base+'locales/en-US.json')).messages;
const cachePath='artifacts/p07-drafts-'+locale+'.json',cache=fs.existsSync(cachePath)?JSON.parse(fs.readFileSync(cachePath)):{};
const extras=fs.existsSync('artifacts/p07-extra-native-english.json')?JSON.parse(fs.readFileSync('artifacts/p07-extra-native-english.json')):[];
const inputs=[...new Set([...Object.values(english).flatMap(v=>typeof v==='string'?[v]:Object.values(v)),...extras])];
const hash=s=>crypto.createHash('sha256').update(s).digest('hex');
const pending=inputs.filter(s=>!cache[hash(s)]).sort((a,b)=>a.length-b.length);
// This Marian ROMANCE export corrupts end-of-sequence behavior for differently
// padded inputs. Batch Portuguese only at EXACT encoded lengths; retain the
// confirmed defective-output diagnostics outside the shipped catalogs.
const encodedLengths=new Map();
if(locale==='pt-BR'){for(const text of pending)encodedLengths.set(text,translate.tokenizer('>>pt_br<< '+text).input_ids.dims[1]);pending.sort((a,b)=>encodedLengths.get(a)-encodedLengths.get(b));}
const save=()=>{fs.writeFileSync(cachePath+'.tmp',JSON.stringify(cache,null,2)+'\n');fs.renameSync(cachePath+'.tmp',cachePath);};
function prepare(text){const markers=[];const input=text.replace(/\{([a-z][a-zA-Z0-9]*)\}/g,(whole)=>{const id=String(987650001+markers.length);markers.push([id,whole]);return id;});return{source:text,input,markers};}
function record(item,output,error){
 if(output)for(const[id,original]of item.markers)output=output.replace(new RegExp(id.split('').join('[\\s.,\\u00a0\\u202f]*'),'g'),()=>original);
 const parameters=s=>[...new Set([...s.matchAll(/\{([a-z][a-zA-Z0-9]*)\}/g)].map(m=>m[1]))].sort();
 const repetition=output&&(/(.{2,50}?)\1{3,}/u.test(output)||/(\p{L}{2,})(?:[^\p{L}]+\1){4,}/iu.test(output));
 const valid=!error&&!repetition&&output?.trim()&&JSON.stringify(parameters(output))===JSON.stringify(parameters(item.source))&&!/98765\d{4}/.test(output)&&output.length>item.source.length*.25&&output.length<item.source.length*4+100;
 cache[hash(item.source)]={source:item.source,draft:valid?output.trim():item.source,needsEnglishFallback:!valid,reason:valid?'Unreviewed offline machine draft':'Placeholder/length preservation failed; explicit English fallback'};
 return valid;
}
let done=0;for(let at=0;at<pending.length;){
 let end=Math.min(at+8,pending.length);
 if(locale==='pt-BR'){end=Math.min(at+2,pending.length);const length=encodedLengths.get(pending[at]);for(let i=at+1;i<end;i++)if(encodedLengths.get(pending[i])!==length){end=i;break;}}
 const batch=pending.slice(at,end).map(prepare);at+=batch.length;
 try{
  if(batch.some(item=>item.input.length>900))for(const item of batch){const chunks=item.input.match(/.{1,900}(?:[.!?](?:\s|$)|$)|.{1,900}(?:\s|$)/gs)||[item.input];let result='';for(const chunk of chunks){const[o]=await translate((locale==='pt-BR'?'>>pt_br<< ':'')+chunk,{max_new_tokens:400,num_beams:1});result+=o.translation_text+' ';}record(item,result);}
  else{const limit=locale==='pt-BR'?Math.min(400,encodedLengths.get(batch[0].source)*2+12):400;
   const results=await translate(batch.map(item=>(locale==='pt-BR'?'>>pt_br<< ':'')+item.input),{max_new_tokens:limit,num_beams:1});
   for(const[i,item]of batch.entries())if(!record(item,results[i]?.translation_text)&&locale==='pt-BR'){const[retry]=await translate('>>pt_br<< '+item.input,{max_new_tokens:limit,num_beams:1});record(item,retry.translation_text);}}
 }catch(error){for(const item of batch)record(item,null,true);}
 done+=batch.length;save();if(done%64===0||at>=pending.length)console.log(locale,done,'new drafts;',Object.keys(cache).length,'cached of',inputs.length);
}
console.log(locale,'complete',Object.keys(cache).length);
