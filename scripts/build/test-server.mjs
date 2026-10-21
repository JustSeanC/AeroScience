import { spawn } from 'node:child_process';
import { mkdtempSync, rmSync, mkdirSync, readdirSync, copyFileSync, readFileSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { resolve, join } from 'node:path';
const data = mkdtempSync(join(tmpdir(), 'aeroscience-browser-'));
const child = spawn(process.env.DOTNET || 'dotnet', [resolve('artifacts/browser-test-host/AeroScience.TestHost.dll')], {
  cwd: resolve('src/AeroScience.Engine'), stdio: 'inherit',
  env: { ...process.env, AeroScience__DataDirectory: data, AeroScience__SamplingIntervalMs: '250', AeroScience__EnableLan: 'false', AeroScience__Port: '5080' }
});
const diagnostic=resolve('artifacts/browser-server');
function captureLogs() {
  const logs=join(data,'logs');
  if(!existsSync(logs))return [];
  mkdirSync(diagnostic,{recursive:true});
  const files=readdirSync(logs).filter(f=>f.endsWith('.log'));
  for(const file of files)copyFileSync(join(logs,file),join(diagnostic,file));
  return files.map(file=>join(logs,file));
}
// Playwright may kill the whole process group; retain logs before that happens.
const diagnosticTimer=setInterval(captureLogs,5000);diagnosticTimer.unref();
let stopping=false;
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => {stopping=true;child.kill(signal);});
child.on('error', error => { console.error(error.message); rmSync(data, { recursive: true, force: true }); process.exit(1); });
child.on('exit', (code,signal) => {
  clearInterval(diagnosticTimer);
  for(const file of captureLogs())if(!stopping)console.error(readFileSync(file,'utf8').split('\n').slice(-60).join('\n'));
  if(!stopping)console.error(`Browser host exited unexpectedly: code=${code}, signal=${signal}`);
  rmSync(data, { recursive: true, force: true }); process.exit(code ?? 1);
});
