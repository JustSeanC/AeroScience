// Maintainer-only deterministic derivatives. No resvg runtime is distributed in AeroScience.
import {Resvg} from '@resvg/resvg-js';
import {readFileSync,writeFileSync,mkdirSync,existsSync} from 'node:fs';
import {createHash} from 'node:crypto';
import {resolve,relative,dirname} from 'node:path';
const root=resolve(import.meta.dirname,'../..'),check=process.argv.includes('--check');
const digest=bytes=>createHash('sha256').update(bytes).digest('hex');
const version=JSON.parse(readFileSync(resolve(root,'node_modules/@resvg/resvg-js/package.json'))).version;
if(version!=='2.6.2')throw Error('Branding requires pinned @resvg/resvg-js 2.6.2; use npm ci.');
const sources=['assets/branding/orbital-arc-primary.svg','assets/branding/orbital-arc-toolbar-white.svg'];
const expected=['82d6b379fba3fac2914d6021c97dfee022694c22d1f4ceb55d218b829b15d12c','6cf7219c0648b693c44066cf103d6152dad076b79e0ab9435aeb022bd22371d0'];
const sourceBytes=sources.map((p,i)=>{const bytes=readFileSync(resolve(root,p));if(digest(bytes)!==expected[i])throw Error('Approved source changed: '+p);return bytes;});
const output=new Map();
const folder='src/AeroScience.Engine/wwwroot/branding/';
const sizes=[16,24,32,48,64,128,180,192,256,512];
for(const [variant,source]of [['primary',sourceBytes[0]],['toolbar',sourceBytes[1]]])for(const size of sizes){
 const renderer=new Resvg(source,{fitTo:{mode:'width',value:size},font:{loadSystemFonts:false}});
 const rendered=renderer.render();
 if(rendered.width!==size||rendered.height!==size)throw Error("Unexpected derivative proportions.");
 const pixels=rendered.pixels;let transparent=false,visible=false;for(let n=3;n<pixels.length;n+=4){transparent ||= pixels[n]===0;visible ||= pixels[n]>0;}
 if(!transparent||!visible)throw Error("Branding transparency or visible artwork was lost.");
 output.set(`${folder}${variant}-${size}.png`,Buffer.from(rendered.asPng()));
}
const icoSizes=[16,24,32,48,64,128,256];const images=icoSizes.map(n=>output.get(`${folder}primary-${n}.png`));
const directory=Buffer.alloc(6+16*images.length);directory.writeUInt16LE(1,2);directory.writeUInt16LE(images.length,4);
let offset=directory.length;images.forEach((bytes,i)=>{const p=6+16*i,n=icoSizes[i];directory[p]=n===256?0:n;directory[p+1]=directory[p];directory.writeUInt16LE(1,p+4);directory.writeUInt16LE(32,p+6);directory.writeUInt32LE(bytes.length,p+8);directory.writeUInt32LE(offset,p+12);offset+=bytes.length;});
output.set(folder+'aeroscience.ico',Buffer.concat([directory,...images]));
output.set('src/AeroScience.Engine/wwwroot/icon.svg',sourceBytes[0]);
output.set('msfs-toolbar/PackageSources/html_ui/icons/toolbar/ICON_AEROSCIENCE.svg',sourceBytes[1]);
const manifest={version:1,renderer:{package:'@resvg/resvg-js',version,license:'MPL-2.0',scope:'maintainer-only; rendered project artwork contains no renderer runtime'},sources:Object.fromEntries(sources.map((p,i)=>[p,expected[i]])),files:Object.fromEntries([...output].map(([path,bytes])=>[path,{sha256:digest(bytes),bytes:bytes.length}]))};
output.set(folder+'manifest.json',Buffer.from(JSON.stringify(manifest,null,2)+'\n'));
for(const [path,bytes]of output){const file=resolve(root,path);if(check){if(!existsSync(file)||!readFileSync(file).equals(bytes))throw Error('Branding asset missing or not reproducible: '+relative(root,file));}else{mkdirSync(dirname(file),{recursive:true});writeFileSync(file,bytes);}}
console.log(`${check?'Verified':'Generated'} ${output.size} faithful branding assets with pinned resvg ${version}. Actual Windows/MSFS rendering remains separate.`);
