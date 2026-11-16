using System.Net;
using System.Text;
using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Missions;
using Xunit;
namespace AeroScience.Tests;

public sealed class Beta080ProfileTests
{
    [Fact] public void BetaTrainIsSeparateFromProductionAndBothPrivateProfiles()
    {
        var beta=new ApplicationProfile(false,Beta080:true);
        var profiles=new[]{new ApplicationProfile(false),new ApplicationProfile(true),new ApplicationProfile(true,"0.8.0",new string('1',32))};
        Assert.True(beta.Isolated);Assert.False(beta.Validation);Assert.False(beta.Native080);
        Assert.Equal("beta",beta.Channel);Assert.Equal("AeroScience v0.8.0 Beta 1",beta.Name);
        Assert.Equal("AeroScience-Beta-v0.8.0",beta.Folder);Assert.Equal(5089,beta.Port);
        Assert.All(profiles,p=>{Assert.NotEqual(p.ConfigurationDirectory,beta.ConfigurationDirectory);Assert.NotEqual(p.RegistryKey,beta.RegistryKey);Assert.NotEqual(p.ToolbarPackage,beta.ToolbarPackage);Assert.NotEqual(p.CommandEvent,beta.CommandEvent);Assert.NotEqual(p.SessionCookieName,beta.SessionCookieName);});
        Assert.Equal(@"Local\AeroScience.Desktop",ApplicationProfile.DesktopMutex);
        Assert.Equal("AeroScience",profiles[0].Folder);Assert.Equal(5080,profiles[0].Port);Assert.Equal(5086,profiles[1].Port);Assert.Equal(5088,profiles[2].Port);
    }
    [Fact] public void BetaCommandsNeedExactSourceVersionAndTheirOriginalCommandAuthority()
    {
        var profile=new ApplicationProfile(false,Beta080:true);
        string Body(string source,string version)=>JsonSerializer.Serialize(new{protocol=1,op="start",requestId="unchanged",betaSource=source,betaVersion=version});
        Assert.Null(profile.AuthorizeToolbar(Body(ApplicationProfile.SourceCommit,ApplicationProfile.BetaVersion)));
        Assert.StartsWith("Beta toolbar source mismatch",profile.AuthorizeToolbar(Body(new string('0',40),ApplicationProfile.BetaVersion)));
        Assert.NotNull(profile.AuthorizeToolbar(Body(ApplicationProfile.SourceCommit,"0.8.0")));
        Assert.NotNull(profile.AuthorizeToolbar("{\"validationSource\":\""+ApplicationProfile.SourceCommit+"\"}"));
        Assert.Null(new ApplicationProfile(false).AuthorizeToolbar("{}"));
        // Profile authorization grants no command/session/revision permission;
        // the existing ToolbarProtocol/controller continues to enforce those.
    }
    [Fact] public void BetaPreferencesCannotSilentlyAdoptProductionStorage()
    {
        var profile=new ApplicationProfile(false,Beta080:true);var preferences=profile.Preferences();
        Assert.ThrowsAny<IOException>(()=>preferences.ValidateDirectory(Path.Combine(Path.GetTempPath(),"AeroScience-production-originals")));
    }
    [Fact] public async Task BetaUpdateChecksAreOptInAndCannotOfferStableOrOldBetaInstallers()
    {
        var directory=Path.Combine(Path.GetTempPath(),"beta080-update-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            var handler=new Releases();using var client=new HttpClient(handler);var updates=new ApplicationUpdates(directory,client,new(false,Beta080:true));
            Assert.Equal(ApplicationProfile.BetaVersion,updates.Get().CurrentVersion);Assert.False(updates.Get().Preferences.Automatic);
            await updates.Check();Assert.Equal(0,handler.Calls);
            var result=await updates.Check(force:true);Assert.Equal(1,handler.Calls);Assert.Equal("v0.8.0-beta.2",result.Available!.Version);
        }finally{Directory.Delete(directory,true);}
    }
    sealed class Releases:HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Calls++;
            object Release(string tag,bool pre)=>new{tag_name=tag,prerelease=pre,draft=false,html_url="https://github.com/JustSeanC/AeroScience/releases/tag/"+Uri.EscapeDataString(tag),assets=new[]{"AeroScience-Setup-"+tag+".exe","AeroScience-Setup-"+tag+".exe.sha256"}.Select(n=>new{name=n,browser_download_url="https://github.com/JustSeanC/AeroScience/releases/download/"+Uri.EscapeDataString(tag)+"/"+n})};
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new[]{Release("v0.8.0",false),Release("v0.7.1-beta.2",true),Release("v0.9.0-beta.1",true),Release("v0.8.0-beta.2",true)}),Encoding.UTF8,"application/json")});
        }
    }
}
