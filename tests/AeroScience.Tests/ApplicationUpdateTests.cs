using System.Net;
using System.Text.Json;
using AeroScience.Engine;
using Xunit;

namespace AeroScience.Tests;

public sealed class ApplicationUpdateTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-updates-"+Guid.NewGuid());
    [Theory][InlineData("0.3.1","0.3.0",1)][InlineData("0.3.1-alpha.10","0.3.1-alpha.2",1)][InlineData("0.3.1-alpha","0.3.1",-1)][InlineData("v0.3.0+build1","0.3.0",0)][InlineData("0.3.1-1","0.3.1-alpha",-1)]
    public void VersionsUseSemanticOrdering(string a,string b,int sign)=>Assert.Equal(sign,Math.Sign(ReleaseVersions.Compare(a,b)));
    [Fact]public async Task SafeReleaseSelectionCachesChecksSupportsChannelsAndNeverDownloadsAnInstaller()
    {
        var handler=new Handler();using var http=new HttpClient(handler);var service=new ApplicationUpdates(root,http);
        service.Configure(new(true,true)); // Explicit opt-in; stable installations default to stable releases.
        var update=await service.Check();Assert.Equal("v9.0.0-alpha.10",update.Available!.Version);Assert.Equal(1,handler.Calls);Assert.Equal("api.github.com",handler.LastHost);Assert.Equal("/repos/JustSeanC/AeroScience/releases",handler.LastPath);
        await service.Check();Assert.Equal(1,handler.Calls);Assert.NotNull(new ApplicationUpdates(root,http).Get().Available);
        service.RemindLater();Assert.True(service.Get().RemindAfterUtc>DateTimeOffset.UtcNow);
        service.Configure(new(true,false));Assert.Null((await service.Check()).Available);Assert.Equal(2,handler.Calls);
        service.Configure(new(false,true));await service.Check();Assert.Equal(2,handler.Calls);await service.Check(true);Assert.Equal(3,handler.Calls);
    }
    [Theory][InlineData(503,false)][InlineData(200,true)]public async Task OfflineMalformedMetadataAndMissingAssetsAreNonBlocking(int code,bool malformed)
    {
        var handler=new Handler{Code=code,Malformed=malformed};using var http=new HttpClient(handler);var service=new ApplicationUpdates(root,http);var result=await service.Check();Assert.Null(result.Available);Assert.Contains("unavailable",result.Message!);
        handler.Code=200;handler.Malformed=false;handler.BadAssets=true;Assert.Null((await service.Check(true)).Available);
    }
    [Fact]public void CachedReleaseCannotRedirectUsersToAnUntrustedInstaller()
    {
        Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"updates.json"),JsonSerializer.Serialize(new UpdateSnapshot("0.2.2",new(),new("v0.4.0","https://example.invalid/download","AeroScience-Setup-v0.4.0.exe",false))));
        Assert.Null(new ApplicationUpdates(root).Get().Available);
    }
    [Fact]public async Task MalformedStoredPreferencesDisableChecksWithoutBlockingTheApplication()
    {
        Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"updates.json"),"{\"Preferences\":null}");var service=new ApplicationUpdates(root);var snapshot=await service.Check();Assert.False(snapshot.Preferences.Automatic);Assert.Null(snapshot.Available);
    }
    [Fact]public async Task StableDefaultsSelectVerifiedStableAssetsAndPreserveAnExplicitPrereleasePreference()
    {
        var handler=new Handler{IncludeStable=true};using var http=new HttpClient(handler);var service=new ApplicationUpdates(root,http);
        Assert.False(service.Get().Preferences.IncludePrereleases);
        var stable=await service.Check();Assert.Equal("v8.0.0",stable.Available!.Version);Assert.False(stable.Available.Prerelease);
        Assert.Equal(1,handler.Calls);Assert.Equal("api.github.com",handler.LastHost); // Metadata only; no installer download.
        service.Configure(new(true,true));
        var reopened=new ApplicationUpdates(root,http);Assert.True(reopened.Get().Preferences.IncludePrereleases);
        Assert.Equal("v9.0.0-alpha.10",(await reopened.Check()).Available!.Version);
        Assert.True(ReleaseVersions.Compare("v0.5.0","v0.4.2")>0);
        Assert.Equal(0,ReleaseVersions.Compare("v0.5.0",ApplicationUpdates.CurrentVersion));
    }
    private sealed class Handler:HttpMessageHandler
    {
        public int Code=200,Calls;public bool Malformed,BadAssets,IncludeStable;public string? LastHost,LastPath;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Calls++;LastHost=request.RequestUri!.Host;LastPath=request.RequestUri.AbsolutePath;Assert.Contains("AeroScience/",request.Headers.UserAgent.ToString());
            object Release(string tag)=>new{draft=false,prerelease=tag.Contains('-'),tag_name=tag,html_url="https://github.com/JustSeanC/AeroScience/releases/tag/"+tag,
                assets=BadAssets?Array.Empty<object>():new[]{new{name="AeroScience-Setup-"+tag+".exe",browser_download_url="https://github.com/JustSeanC/AeroScience/releases/download/"+tag+"/AeroScience-Setup-"+tag+".exe"},new{name="AeroScience-Setup-"+tag+".exe.sha256",browser_download_url="https://github.com/JustSeanC/AeroScience/releases/download/"+tag+"/AeroScience-Setup-"+tag+".exe.sha256"}}};
            var releases=new List<object>{Release("v9.0.0-alpha.2"),Release("v9.0.0-alpha.10")};if(IncludeStable)releases.Add(Release("v8.0.0"));
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)Code){Content=new StringContent(Malformed?"not json":JsonSerializer.Serialize(releases))});
        }
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
