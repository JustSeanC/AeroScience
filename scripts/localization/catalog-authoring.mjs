import {readFileSync,writeFileSync} from 'node:fs';
const folder='src/AeroScience.Engine/wwwroot/i18n/';
export const english=JSON.parse(readFileSync(folder+'locales/en-US.json','utf8'));
export const schema=JSON.parse(readFileSync(folder+'message-schema.json','utf8'));
export function register(key,text,{surface,context,parameters=[],critical=false,locations=[]}={}){
  if(english.messages[key]&&JSON.stringify(english.messages[key])!==JSON.stringify(text))throw Error('Conflicting source message '+key);
  english.messages[key]=text;
  schema.messages[key]={...schema.messages[key],context,parameters,surface,critical,maximumLength:4096,locations};
  return key;
}
export function save(){for(const [path,value]of [['locales/en-US.json',english],['message-schema.json',schema]])writeFileSync(folder+path,JSON.stringify(value,null,2)+'\n');}
