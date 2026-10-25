using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.Telemetry;
using Xunit;

namespace AeroScience.Tests;

[Collection(ScientificIoCollection.Name)]
public sealed class ScientificHdfReaderTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-hdf-reader-"+Guid.NewGuid().ToString("N"));
    private readonly Guid library=Guid.NewGuid();
    private static string Fixtures=>Path.Combine(AppContext.BaseDirectory,"HdfFixtures");
    private static ScientificHdfReader Reader()=>new(new(Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),"../../..",OperatingSystem.IsWindows()?"dotnet.exe":"dotnet")),["exec","--runtimeconfig",Path.Combine(AppContext.BaseDirectory,"AeroScience.Tests.runtimeconfig.json"),"--depsfile",Path.Combine(AppContext.BaseDirectory,"AeroScience.Tests.deps.json"),typeof(EngineApplication).Assembly.Location]));
    public ScientificHdfReaderTests(){Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"library.json"),$"{{\"formatVersion\":1,\"libraryId\":\"{library}\"}}");}
    private HdfOriginal Original(string name)
    {var relative="Downloads/"+Guid.NewGuid().ToString("N")+"/original.nc";var path=Path.Combine(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.Copy(Path.Combine(Fixtures,name),path);using var data=File.OpenRead(path);return new(root,library,relative,data.Length,Convert.ToHexStringLower(SHA256.HashData(data)));}
    private static HdfReadRequest Request(HdfOriginal source,string dataset="reflectance",ulong[]? count=null)=>new(1,source,[new(dataset,[0,0,0],count??[1,1,5],["units","_FillValue","missing_attribute"])],["scene_id"]);
    private static async Task<string> ResolveProtocolInterpreter(HdfWorkerCommand? launcher=null)
    {
        // WindowsApps may supply an activation/manager alias rather than CPython.
        // Resolve it in the normal build environment, then test the real executable
        // under the production reader's unchanged, scrubbed child environment.
        launcher??=new(OperatingSystem.IsWindows()?"python":"python3",[]);
        var start=new ProcessStartInfo(launcher.FileName){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in launcher.Arguments)start.ArgumentList.Add(argument);
        foreach(var argument in new[]{"-I","-S","-c","import sys,json;print(json.dumps(sys.executable,ensure_ascii=True),flush=True)"})start.ArgumentList.Add(argument);
        using var child=Process.Start(start)??throw new IOException("Python protocol fixture could not start.");
        void Stop(){try{if(!child.HasExited)child.Kill(entireProcessTree:true);}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}}
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));using var cancel=deadline.Token.Register(Stop);
        async Task Drain(){var bytes=new byte[1024];var total=0;int n;while((n=await child.StandardError.BaseStream.ReadAsync(bytes,deadline.Token))>0)if((total+=n)>8192)throw new IOException("Python fixture startup diagnostics exceeded their bound.");}
        var diagnostics=Drain();
        try
        {
            child.StandardInput.Close();
            var output=await ScientificHdfReader.Line(child.StandardOutput.BaseStream,8192,deadline.Token);
            await child.WaitForExitAsync(deadline.Token);await diagnostics;
            Assert.True(child.ExitCode==0,"Python protocol fixture interpreter resolution failed.");
            var executable=JsonSerializer.Deserialize<string>(output);
            Assert.True(executable is not null&&Path.IsPathFullyQualified(executable)&&File.Exists(executable),"Python protocol fixture requires an existing absolute interpreter path.");
            return executable!;
        }
        finally{Stop();try{await child.WaitForExitAsync();await diagnostics;}catch(Exception error)when(error is IOException or OperationCanceledException or InvalidOperationException){}}
    }
    [Fact]public async Task ActualWorkerReturnsExactNativeWindowsMetadataHashesAndEnforcedBoundsWithoutImporting()
    {
        var source=Original("synthetic-emit-reflectance.nc");var before=Directory.GetFiles(root,"*",SearchOption.AllDirectories);var result=await Reader().ReadAsync(Request(source));
        Assert.Equal(source.Sha256,result.OriginalSha256);Assert.Equal(source.Bytes,result.OriginalBytes);Assert.Equal(library,result.LibraryId);
        var window=Assert.Single(result.Windows);Assert.Equal("Float32",window.NumericType);Assert.Equal(new ulong[]{2,3,5},window.Dimensions);
        var values=MemoryMarshal.Cast<byte,float>(window.LittleEndianValues!).ToArray();Assert.Equal(new[]{0f,.01f,.02f,.03f,.04f},values);Assert.Equal("Original",window.Attributes["units"].State);Assert.Equal("Missing",window.Attributes["missing_attribute"].State);
        Assert.Equal(128L*1024*1024,result.Boundary.ManagedHeapBytes);Assert.Equal(256L*1024*1024,result.Boundary.ProcessMemoryBytes);Assert.InRange(result.Boundary.ReadBytes,1,32L*1024*1024);Assert.InRange(result.Boundary.WorkingSetBytes,1,256L*1024*1024);Assert.Contains(OperatingSystem.IsWindows()?"Windows job":"Linux RLIMIT",result.Boundary.Kind);
        Assert.Equal(before,Directory.GetFiles(root,"*",SearchOption.AllDirectories));using var file=File.Open(Path.Combine(root,source.RelativePath),FileMode.Open,FileAccess.ReadWrite,FileShare.None);Assert.Equal(source.Sha256,Convert.ToHexStringLower(SHA256.HashData(file)));
    }
    [Theory]
    [InlineData("external-traversal.nc")][InlineData("external-absolute.nc")][InlineData("external-windows.nc")][InlineData("external-unc.nc")][InlineData("external-url.nc")][InlineData("soft-cycle.nc")][InlineData("external-raw.nc")][InlineData("virtual.nc")]
    public async Task ExternalSymbolicRawAndVirtualStorageFailBeforeResolution(string fixture)
    {
        var source=Original(fixture);var error=await Assert.ThrowsAsync<ScientificHdfException>(()=>Reader().ReadAsync(new(1,source,[new("unsafe")])));Assert.Equal("ExternalStorage",error.Code);
        Assert.DoesNotContain(root,error.Message);using var unlocked=File.Open(Path.Combine(root,source.RelativePath),FileMode.Open,FileAccess.ReadWrite,FileShare.None);
    }
    [Fact]public async Task PreviouslyDemonstratedExternalFileCanaryIsStillRejectedByTheProductionWorker()
    {
        var source=Original("previous-canary-reflectance.nc");var error=await Assert.ThrowsAsync<ScientificHdfException>(()=>Reader().ReadAsync(Request(source)));Assert.Equal("ExternalStorage",error.Code);
    }
    [Theory][InlineData("synthetic-emit-oversized.nc","reflectance")][InlineData("deflate-expansion.nc","unsafe")]
    public async Task DeclaredOversizedChunksAndActualDeflateExpansionAreRejectedWithoutReturningValues(string file,string dataset)
    {
        var source=Original(file);var request=file=="deflate-expansion.nc"?new HdfReadRequest(1,source,[new(dataset,[0,0],[1,1])]):Request(source,dataset);var clock=Stopwatch.StartNew();
        var error=await Assert.ThrowsAsync<ScientificHdfException>(()=>Reader().ReadAsync(request));Assert.Equal("Limit",error.Code);Assert.True(clock.Elapsed<TimeSpan.FromSeconds(15));
        using var original=File.Open(Path.Combine(root,source.RelativePath),FileMode.Open,FileAccess.ReadWrite,FileShare.None);Assert.Equal(source.Sha256,Convert.ToHexStringLower(SHA256.HashData(original)));
    }
    [Fact]public async Task UnknownVersionsUnsafePathsWrongOwnershipAndChangedHashesCannotProduceResults()
    {
        var source=Original("synthetic-emit-reflectance.nc");var request=Request(source);
        await Assert.ThrowsAsync<NotSupportedException>(()=>Reader().ReadAsync(request with{Version=2}));
        foreach(var path in new[]{"../original.nc","Downloads/../outside.nc","/etc/passwd","Sources/alias\\outside.nc","Sources/a:file.nc"})await Assert.ThrowsAsync<ArgumentException>(()=>Reader().ReadAsync(request with{Original=source with{RelativePath=path}}));
        await Assert.ThrowsAsync<ArgumentException>(()=>Reader().ReadAsync(request with{Windows=[new("../reflectance")]}));
        Assert.Equal("Ownership",(await Assert.ThrowsAsync<ScientificHdfException>(()=>Reader().ReadAsync(request with{Original=source with{LibraryId=Guid.NewGuid()}}))).Code);
        Assert.Equal("Integrity",(await Assert.ThrowsAsync<ScientificHdfException>(()=>Reader().ReadAsync(request with{Original=source with{Sha256=new('0',64)}}))).Code);
        Assert.Equal("Format",(await Assert.ThrowsAsync<ScientificHdfException>(()=>Reader().ReadAsync(request with{Windows=[new("reflectance",[0,0,0],[1,1,6])]}))).Code);
    }
    [Fact]public async Task TruncatedMetadataFailsAndReleasesHandlesForImmediateOwnerAccess()
    {
        var source=Original("synthetic-emit-reflectance.nc");var path=Path.Combine(root,source.RelativePath);var bytes=File.ReadAllBytes(path);File.WriteAllBytes(path,bytes[..31]);source=source with{Bytes=31,Sha256=Convert.ToHexStringLower(SHA256.HashData(bytes[..31]))};
        await Assert.ThrowsAsync<ScientificHdfException>(()=>Reader().ReadAsync(Request(source)));using var file=File.Open(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
    }
    [Fact]public async Task NumericEndianFillNanAndUnsignedFlagsArePreservedWithoutScientificInterpretation()
    {
        var source=Original("numeric-types.nc");var result=await Reader().ReadAsync(new(1,source,[new("double_be",[0],[4]),new("signed",[0],[4]),new("flags",[0],[4])]));
        var doubles=MemoryMarshal.Cast<byte,double>(result.Windows[0].LittleEndianValues!).ToArray();Assert.Equal(1.25,doubles[0]);Assert.Equal(-9999,doubles[1]);Assert.True(double.IsNaN(doubles[2]));Assert.Equal(2.5,doubles[3]);
        Assert.Equal(new[]{-1,0,int.MaxValue,int.MinValue},MemoryMarshal.Cast<byte,int>(result.Windows[1].LittleEndianValues!).ToArray());Assert.Equal(new byte[]{0,128,255,8},result.Windows[2].LittleEndianValues);Assert.Equal("UInt8",result.Windows[2].NumericType);
        Assert.Contains("No radiometric, QA, geolocation",result.Method);
    }
    [Fact]public async Task FilesystemSymlinkOrWindowsJunctionCannotEscapeTheApprovedOriginalRoot()
    {
        var source=Original("synthetic-emit-reflectance.nc");var folder=Path.GetDirectoryName(Path.Combine(root,source.RelativePath))!;var target=folder+"-held";Directory.Move(folder,target);
        if(OperatingSystem.IsWindows())
        {
            var command=new ProcessStartInfo("cmd.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var argument in new[]{"/c","mklink","/J",folder,target})command.ArgumentList.Add(argument);
            using var child=Process.Start(command)!;await child.WaitForExitAsync();Assert.True(child.ExitCode==0,await child.StandardError.ReadToEndAsync());
        }
        else Directory.CreateSymbolicLink(folder,target);
        try{var error=await Assert.ThrowsAsync<ScientificHdfException>(()=>Reader().ReadAsync(Request(source)));Assert.Equal("Ownership",error.Code);Assert.True(File.Exists(Path.Combine(target,"original.nc")));}
        finally{Directory.Delete(folder);Directory.Move(target,folder);}
    }
    [Fact]public async Task ChildProtocolOutputAndInheritedSecretsAreBoundedAndDeadlineCannotStallTheHost()
    {
        var interpreter=await ResolveProtocolInterpreter();
        var source=Original("synthetic-emit-reflectance.nc");const string key="AEROSCIENCE_READER_TEST_SECRET";var previous=Environment.GetEnvironmentVariable(key);Environment.SetEnvironmentVariable(key,"synthetic-environment-canary-not-a-secret");
        ScientificHdfReader Fake(string script)=>new(new(interpreter,["-I","-S","-c",script]));
        try
        {
            var error=await Assert.ThrowsAsync<ScientificHdfException>(()=>Fake("import os,json;print(json.dumps({'phase':'error','code':'SecretLeak' if os.environ.get('"+key+"') else 'Limit'}),flush=True)").ReadAsync(Request(source)));Assert.Equal("Limit",error.Code);
            var oversized=await Assert.ThrowsAsync<ScientificHdfException>(()=>Fake("import sys;sys.stdin.readline();sys.stdout.write('x'*(8*1024*1024+1));sys.stdout.flush()").ReadAsync(Request(source)));Assert.Equal("Protocol",oversized.Code);
            var clock=Stopwatch.StartNew();var timeout=await Assert.ThrowsAsync<ScientificHdfException>(()=>Fake("import sys,json,time;sys.stdin.readline();print(json.dumps({'phase':'verified'}),flush=True);time.sleep(40)").ReadAsync(Request(source)));Assert.Equal("Deadline",timeout.Code);Assert.InRange(clock.Elapsed.TotalSeconds,14,25);
        }
        finally{Environment.SetEnvironmentVariable(key,previous);}
        Assert.Single((await Reader().ReadAsync(Request(source))).Windows);
    }
    [Fact]public async Task EnvironmentDependentProtocolLauncherIsResolvedBeforeWorkerEnvironmentIsScrubbed()
    {
        var interpreter=await ResolveProtocolInterpreter();var source=Original("synthetic-emit-reflectance.nc");
        const string key="AEROSCIENCE_READER_TEST_LAUNCHER";var previous=Environment.GetEnvironmentVariable(key);
        var bootstrap="import os,sys,subprocess;sys.stdin.readline();sys.exit(37) if not os.environ.get('"+key+"') else subprocess.run([sys.executable]+sys.argv[1:],check=True)";
        var launcher=new HdfWorkerCommand(interpreter,["-I","-S","-c",bootstrap]);
        var probe="import os,json;print(json.dumps({'phase':'error','code':'SecretLeak' if os.environ.get('"+key+"') else 'Limit'}),flush=True)";
        Environment.SetEnvironmentVariable(key,"synthetic-launcher-activation-canary");
        try
        {
            // Reproduce the owner failure without depending on a Store installation.
            var unresolved=new ScientificHdfReader(launcher with{Arguments=[..launcher.Arguments,"-I","-S","-c",probe]});
            Assert.Equal("ResourceOrFormat",(await Assert.ThrowsAsync<ScientificHdfException>(()=>unresolved.ReadAsync(Request(source)))).Code);
            var resolved=await ResolveProtocolInterpreter(launcher);Assert.Equal(interpreter,resolved);
            var reader=new ScientificHdfReader(new(resolved,["-I","-S","-c",probe]));
            Assert.Equal("Limit",(await Assert.ThrowsAsync<ScientificHdfException>(()=>reader.ReadAsync(Request(source)))).Code);
        }
        finally{Environment.SetEnvironmentVariable(key,previous);}
    }
    [Fact]public async Task CancellationDisposesTheActualWorkerAndRecordingRemainsIndependent()
    {
        var source=Original("synthetic-emit-reflectance.nc");var preferences=new PreferenceStore(Path.Combine(root,"synthetic-flight"));var paths=new StorageManager(preferences).Initialize();var missions=new MissionStore(Path.Combine(paths.MissionDirectory,"missions.db"));missions.Initialize();var recorder=new MissionCoordinator(missions,1000);var mission=recorder.Start(new("Synthetic recording alongside reader",ScienceMode.Universal));
        using var cancel=new CancellationTokenSource();var task=Reader().ReadAsync(Request(source),cancel.Token);cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>task);
        for(var i=0;i<3;i++){var time=DateTimeOffset.UtcNow.AddSeconds(i);recorder.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Synthetic test flight",time),SimulatedTelemetryProvider.Generate(time,i)));}
        Assert.Equal(3,missions.Detail(mission.Id)!.Mission.SampleCount);recorder.End(mission.Id);
        using(var file=File.Open(Path.Combine(root,source.RelativePath),FileMode.Open,FileAccess.ReadWrite,FileShare.None)){}
        var result=await Reader().ReadAsync(Request(source));Assert.Single(result.Windows);
    }
    [Theory][InlineData(true)][InlineData(false)]
    public async Task PendingRequestPipeCancellationIsDistinctFromUnexpectedWorkerExitAndReleasesTheReader(bool cancelRequest)
    {
        var source=Original("synthetic-emit-reflectance.nc");var interpreter=await ResolveProtocolInterpreter();
        var marker=Path.Combine(root,"protocol-worker.pid");
        // Fill the Windows anonymous-input pipe while the real child deliberately
        // does not read. The owner failure occurred during this request/kill race.
        var attributes=Enumerable.Range(0,16).Select(i=>new string('a',79)+(char)('A'+i)).ToArray();
        var request=new HdfReadRequest(1,source,Enumerable.Range(0,16).Select(_=>new HdfWindow(new string('d',500),[0],[1],attributes)).ToArray());
        var bytes=JsonSerializer.SerializeToUtf8Bytes(request,ScientificHdfReader.Json);
        Assert.InRange(bytes.Length,16384,ScientificHdfReader.MaximumRequest);
        var script="import os,pathlib,time;p=pathlib.Path("+JsonSerializer.Serialize(marker)+");s=p.with_suffix('.pending');s.write_text(str(os.getpid()));os.replace(s,p);"+(cancelRequest?"time.sleep(40)":"raise SystemExit(37)");
        using var cancellation=new CancellationTokenSource();
        var reader=new ScientificHdfReader(new(interpreter,["-I","-S","-c",script]));
        var read=reader.ReadAsync(request,cancellation.Token);
        try
        {
            if(cancelRequest)
            {
                using var startup=new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while(!File.Exists(marker))await Task.Delay(10,startup.Token);
                // Atomic publication avoids observing a half-written PID marker.
                var pid=int.Parse(await File.ReadAllTextAsync(marker,startup.Token));
                using var child=Process.GetProcessById(pid);Assert.False(read.IsCompleted);
                cancellation.Cancel();
                var error=await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>read.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(cancellation.Token,error.CancellationToken);Assert.True(child.HasExited);
            }
            else
            {
                await Assert.ThrowsAnyAsync<IOException>(()=>read.WaitAsync(TimeSpan.FromSeconds(15)));
                Assert.False(cancellation.IsCancellationRequested);
            }
        }
        finally{cancellation.Cancel();try{await read;}catch(Exception e)when(e is IOException or OperationCanceledException){}}
        using(var original=File.Open(Path.Combine(root,source.RelativePath),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            Assert.Equal(source.Sha256,Convert.ToHexStringLower(SHA256.HashData(original)));
        // A cancelled/failed transfer must release the global slot and actual file.
        Assert.Single((await Reader().ReadAsync(Request(source))).Windows);
    }
    public void Dispose(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
}
