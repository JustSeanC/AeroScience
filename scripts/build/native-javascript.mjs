// Build-time only. Simulator packages never load TypeScript, npm, or remote code.
import ts from 'typescript';
import {parse,version as acornVersion} from 'acorn';
import {readFileSync,writeFileSync,readdirSync,lstatSync,realpathSync} from 'node:fs';
import {resolve,relative,join,dirname,isAbsolute,sep} from 'node:path';
import {fileURLToPath} from 'node:url';
import {createHash} from 'node:crypto';
import {nativeCatalogAssets} from './build-locales.mjs';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../..');
const hash=value=>createHash('sha256').update(value).digest('hex');
export function compileNative(source,name='panel.js'){
 parse(source,{ecmaVersion:'latest',sourceType:'script'});
 const result=ts.transpileModule(source,{fileName:name,reportDiagnostics:true,compilerOptions:{target:ts.ScriptTarget.ES2017,module:ts.ModuleKind.None,newLine:ts.NewLineKind.LineFeed,removeComments:false}});
 if(result.diagnostics?.some(d=>d.category===ts.DiagnosticCategory.Error))throw Error('Native JavaScript compilation failed: '+name);
 parse(result.outputText,{ecmaVersion:2017,sourceType:'script'});
 return result.outputText;
}
// One read-only state projection on both surfaces. Generate a classic-script
// wrapper from the canonical ES modules, then independently parse ES2017 output.
export function nativeFlightStatus(){
 const files=['presentation-state.js','current-flight-state.js'];
 const source=files.map(name=>readFileSync(join(root,'src/AeroScience.Engine/wwwroot',name),'utf8')
   .replace(/^import .*;\r?\n/gm,'').replace(/^export /gm,'')).join('\n');
 return 'var AeroScienceFlightStatus = (function () {\n'+source+'\nreturn {present:currentFlightPresentation};\n}());\n';
}
export function compileStage(folder){
 const stage=realpathSync(folder),original=join(root,'msfs-toolbar');
 const underOriginal=relative(original,stage);
 if((!underOriginal||!underOriginal.startsWith('..')&&!isAbsolute(underOriginal))||!lstatSync(join(stage,'AeroScience.xml')).isFile())throw Error('Use a copied SDK staging project; original sources are preserved.');
 const files=[],panels=new Set();
 function visit(dir){for(const item of readdirSync(dir,{withFileTypes:true})){
  const path=join(dir,item.name);if(item.isSymbolicLink())throw Error('Native staging must not contain links.');
  if(item.isDirectory())visit(path);else if(item.isFile()&&item.name.endsWith('.js')){
   const input=readFileSync(path);if(input.length>16*1024*1024)throw Error('Native script exceeds the build size bound.');
   files.push({path,name:relative(stage,path).split(sep).join('/'),input,output:compileNative(input.toString('utf8'),item.name)});
   if(item.name==='AeroScience.I18n.js')panels.add(dir);
  }
 }}
 let panelRoot=stage;
 for(const name of ['PackageSources','html_ui','InGamePanels']){panelRoot=join(panelRoot,name);if(lstatSync(panelRoot).isSymbolicLink())throw Error('Native staging must not contain links.');}
 visit(panelRoot);
 // Discover the actual copied panel directory: private validation deliberately
 // renames it. Validate links/all input first; never generate through a link.
 for(const panel of panels)for(const [name,source]of Object.entries({...nativeCatalogAssets(),'AeroScience.FlightStatus.js':nativeFlightStatus()})){
  const path=join(panel,name),input=Buffer.from(source),file={path,name:relative(stage,path).split(sep).join('/'),input,output:compileNative(source,name)};
  const existing=files.findIndex(f=>f.path===path);if(existing>=0)files[existing]=file;else files.push(file);
 }
 if(!files.length)throw Error('No native panel JavaScript was staged.');
 // Validate the entire set before replacing any generated asset.
 for(const f of files)writeFileSync(f.path,f.output);
 const receipt={target:'ES2017',compiler:'TypeScript '+ts.version,parser:'Acorn '+acornVersion,files:files.map(f=>({path:f.name,sourceSha256:hash(f.input),compiledSha256:hash(f.output)}))};
 writeFileSync(join(stage,'native-javascript.json'),JSON.stringify(receipt,null,2)+'\n');return receipt;
}
if(process.argv[1]&&resolve(process.argv[1])===fileURLToPath(import.meta.url)){
 const receipt=compileStage(process.argv[2]);console.log(JSON.stringify({target:receipt.target,compiled:receipt.files.length}));
}
