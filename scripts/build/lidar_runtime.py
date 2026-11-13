"""Offline, pinned-source native decoder build. Never downloads a runtime."""
import argparse, hashlib, json, os, pathlib, platform, shutil, subprocess

ROOT=pathlib.Path(__file__).resolve().parents[2]
NATIVE=ROOT/'native/lidar'
def digest(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def msvc_environment(bat):
    # cmd.exe parses /c itself. Passing this payload through list2cmdline adds
    # CRT backslash-escaped quotes, which cmd treats as a literal command name.
    # This path comes only from the installed VS tool locator, never source data.
    bat=str(bat)
    if any(c in bat for c in ('"','%','\r','\n')):raise ValueError('Unsupported Visual Studio batch path')
    text=subprocess.check_output('cmd.exe /d /s /c ""'+bat+'" -no_logo -arch=x64 >nul && set"',text=True,stderr=subprocess.STDOUT)
    return dict(line.split('=',1) for line in text.splitlines() if '=' in line and not line.startswith('='))
def build(output):
    policy=json.loads((NATIVE/'source-policy.json').read_text())
    if {p.relative_to(NATIVE/'laszip').as_posix() for p in (NATIVE/'laszip').rglob('*') if p.is_file()}!=set(policy['files']):raise ValueError('Unreviewed file in pinned LASzip source tree')
    for name,expected in policy['files'].items():
        if digest(NATIVE/'laszip'/name)!=expected:raise ValueError('Pinned LASzip source changed: '+name)
    identity=hashlib.sha256(((NATIVE/'source-policy.json').read_text()+digest(NATIVE/'reader.cpp')+digest(pathlib.Path(__file__))).encode()).hexdigest()
    if platform.machine().lower() not in ('x86_64','amd64'):raise ValueError('LiDAR decoder requires verified x64; no unsafe fallback')
    output=output.resolve();output.mkdir(parents=True,exist_ok=True)
    exe=output/('AeroScience.LidarReader.exe' if os.name=='nt' else 'AeroScience.LidarReader')
    stamp=output/'build.json';src=[NATIVE/'reader.cpp']+sorted((NATIVE/'laszip/src').glob('*.cpp'))
    if not exe.exists() or not stamp.exists() or json.loads(stamp.read_text()).get('source')!=identity or json.loads(stamp.read_text()).get('sha256')!=digest(exe):
        if os.name=='nt':
            env=os.environ.copy()
            if not shutil.which('cl'):
                where=pathlib.Path(os.environ.get('ProgramFiles(x86)',r'C:\Program Files (x86)'))/'Microsoft Visual Studio/Installer/vswhere.exe'
                vs=subprocess.check_output([str(where),'-latest','-products','*','-requires','Microsoft.VisualStudio.Component.VC.Tools.x86.x64','-property','installationPath'],text=True).strip()
                bat=pathlib.Path(vs)/'Common7/Tools/VsDevCmd.bat'
                if not bat.is_file():raise ValueError('Install Visual Studio x64 C++ build tools; no downloaded binary is substituted')
                env.update(msvc_environment(bat))
            args=['cl','/nologo','/std:c++17','/O2','/MT','/EHsc','/DWIN32','/DLASZIPDLL_EXPORTS','/DHAVE_UNORDERED_MAP=1','/DAS_LIDAR_ID="'+identity+'"','/I'+str(NATIVE/'laszip/include/laszip'),'/Fe:'+str(exe)]+list(map(str,src))
        else:
            env=os.environ.copy();args=['g++','-std=c++17','-O2','-pthread','-DLASZIPDLL_EXPORTS','-DHAVE_UNORDERED_MAP=1','-DAS_LIDAR_ID="'+identity+'"','-I'+str(NATIVE/'laszip/include/laszip'),'-o',str(exe)]+list(map(str,src))
        subprocess.run(args,cwd=output,env=env,check=True,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=180)
        stamp.write_text(json.dumps({'source':identity,'sha256':digest(exe),'upstreamCommit':policy['commit']}))
    value=json.loads(stamp.read_text())
    if subprocess.check_output([str(exe),'--identity'],timeout=5,text=True).strip()!='AS-LIDAR-1 '+identity:raise ValueError('Native build protocol/identity mismatch')
    cs='namespace AeroScience.Missions; internal static class LidarRuntimeIdentity { internal const string Sha256="'+value['sha256']+'"; internal const string Source="'+identity+'"; }\n'
    generated=output/'LidarRuntimeIdentity.g.cs'
    if not generated.exists() or generated.read_text()!=cs:generated.write_text(cs)
    shutil.copyfile(NATIVE/'laszip/COPYING.txt',output/'LASzip.txt')
    print(json.dumps({'source':identity,'executableBytes':exe.stat().st_size,'upstreamCommit':policy['commit']}))
if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--output',type=pathlib.Path,required=True);a=p.parse_args()
    try:build(a.output)
    except subprocess.CalledProcessError as e:print(e.stdout.decode(errors='replace') if isinstance(e.stdout,bytes) else e.stdout);raise
