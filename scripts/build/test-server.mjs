import { spawn } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { resolve, join } from 'node:path';
const data = mkdtempSync(join(tmpdir(), 'aeroscience-browser-'));
const child = spawn(process.env.DOTNET || 'dotnet', [resolve('tests/AeroScience.TestHost/bin/Release/net10.0/AeroScience.TestHost.dll')], {
  cwd: resolve('src/AeroScience.Engine'), stdio: 'inherit',
  env: { ...process.env, AeroScience__DataDirectory: data, AeroScience__SamplingIntervalMs: '250', AeroScience__EnableLan: 'false', AeroScience__Port: '5080' }
});
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => child.kill(signal));
child.on('error', error => { console.error(error.message); rmSync(data, { recursive: true, force: true }); process.exit(1); });
child.on('exit', code => { rmSync(data, { recursive: true, force: true }); process.exit(code ?? 0); });
