// Build-time only. Simulator packages never load TypeScript, npm, or remote code.
import ts from 'typescript';
import {parse,version as acornVersion} from 'acorn';
import {readFileSync,writeFileSync,readdirSync,lstatSync,realpathSync} from 'node:fs';
import {resolve,relative,join,dirname,isAbsolute,sep} from 'node:path';
import {fileURLToPath} from 'node:url';
import {createHash} from 'node:crypto';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../..');
const hash=value=>createHash('sha256').update(value).digest('hex');
export function compileNative(source,name='panel.js'){
 parse(source,{ecmaVersion:'latest',sourceType:'script'});
 const result=ts.transpileModule(source,{fileName:name,reportDiagnostics:true,compilerOptions:{target:ts.ScriptTarget.ES2017,module:ts.ModuleKind.None,newLine:ts.NewLineKind.LineFeed,removeComments:false}});
 if(result.diagnostics?.some(d=>d.category===ts.DiagnosticCategory.Error))throw Error('Native JavaScript compilation failed: '+name);
 parse(result.outputText,{ecmaVersion:2017,sourceType:'script'});
 return result.outputText;
}
export function compileStage(folder){
 const stage=realpathSync(folder),original=join(root,'msfs-toolbar');
 const underOriginal=relative(original,stage);
 if((!underOriginal||!underOriginal.startsWith('..')&&!isAbsolute(underOriginal))||!lstatSync(join(stage,'AeroScience.xml')).isFile())throw Error('Use a copied SDK staging project; original sources are preserved.');
 const files=[];
 function visit(dir){for(const item of readdirSync(dir,{withFileTypes:true})){
  const path=join(dir,item.name);if(item.isSymbolicLink())throw Error('Native staging must not contain links.');
  if(item.isDirectory())visit(path);else if(item.isFile()&&item.name.endsWith('.js')){
   const input=readFileSync(path);if(input.length>16*1024*1024)throw Error('Native script exceeds the build size bound.');
   files.push({path,name:relative(stage,path).split(sep).join('/'),input,output:compileNative(input.toString('utf8'),item.name)});
  }
 }}
 visit(join(stage,'PackageSources/html_ui/InGamePanels'));
 if(!files.length)throw Error('No native panel JavaScript was staged.');
 // Validate the entire set before replacing any generated asset.
 for(const f of files)writeFileSync(f.path,f.output);
 const receipt={target:'ES2017',compiler:'TypeScript '+ts.version,parser:'Acorn '+acornVersion,files:files.map(f=>({path:f.name,sourceSha256:hash(f.input),compiledSha256:hash(f.output)}))};
 writeFileSync(join(stage,'native-javascript.json'),JSON.stringify(receipt,null,2)+'\n');return receipt;
}
if(process.argv[1]&&resolve(process.argv[1])===fileURLToPath(import.meta.url)){
 const receipt=compileStage(process.argv[2]);console.log(JSON.stringify({target:receipt.target,compiled:receipt.files.length}));
}
