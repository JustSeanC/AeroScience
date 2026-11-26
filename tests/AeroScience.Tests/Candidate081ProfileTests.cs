using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Missions;
using Xunit;

namespace AeroScience.Tests;
public sealed class Candidate081ProfileTests
{
    private static ApplicationProfile Candidate(string instance="1234567890abcdef1234567890abcdef")=>new(true,"0.8.1",instance,Candidate081:true);
    [Fact] public void IdentitySeparatesEveryWritableAndNativeChannelButRetainsDesktopExclusion()
    {
        var p=Candidate();Assert.True(p.Native081);Assert.False(p.Native080);Assert.True(p.Isolated);Assert.Equal(5091,p.Port);
        Assert.Equal(Path.Combine("AeroScience-Candidate-v0.8.1",p.Instance!),p.Folder);
        foreach(var old in new[]{new ApplicationProfile(false),new(true),new(true,"0.8.0",p.Instance),new(false,Beta080:true),Candidate(new string('a',32))})
        {
            Assert.NotEqual(old.ConfigurationDirectory,p.ConfigurationDirectory);Assert.NotEqual(old.RegistryKey,p.RegistryKey);Assert.NotEqual(old.SessionCookieName,p.SessionCookieName);
            if(!old.Candidate081){Assert.NotEqual(old.Port,p.Port);Assert.NotEqual(old.ToolbarPackage,p.ToolbarPackage);Assert.NotEqual(old.CommandEvent,p.CommandEvent);}
        }
        Assert.Equal(@"Local\AeroScience.Desktop",ApplicationProfile.DesktopMutex);
        Assert.Throws<InvalidOperationException>(()=>Candidate(new string('0',32)).Folder);
        Assert.Throws<InvalidOperationException>(()=>new ApplicationProfile(true,"0.8.0",p.Instance,Candidate081:true).Native081);
    }
    [Fact] public void MissingOrForeignMarkerFailsBeforeAnyPreferenceOrDatabaseCanBeCreated()
    {
        var root=Path.Combine(Path.GetTempPath(),"candidate-marker-"+Guid.NewGuid().ToString("N"));var p=Candidate();
        Directory.CreateDirectory(root);
        try{
            Assert.Throws<IOException>(()=>p.ValidatePrivateMarker(root));Assert.Empty(Directory.GetFiles(root));
            var marker=Path.Combine(root,"candidate-profile.json");
            foreach(var value in new[]{new{version=1,source_commit="wrong",instance=p.Instance!},new{version=1,source_commit=ApplicationProfile.SourceCommit,instance=new string('a',32)}}){PreferenceStore.WriteAtomic(marker,value);Assert.Throws<IOException>(()=>p.ValidatePrivateMarker(root));}
            PreferenceStore.WriteAtomic(marker,new{version=1,source_commit=ApplicationProfile.SourceCommit,instance=p.Instance});p.ValidatePrivateMarker(root);
            Assert.False(File.Exists(Path.Combine(root,"settings.json")));Assert.False(File.Exists(Path.Combine(root,"missions.db")));
        }finally{Directory.Delete(root,true);}
    }
    [Fact] public void CommandsMustMatchCandidateSourceAndAttemptBeforeOriginalAuthority()
    {
        var p=Candidate();string Body(string instance)=>JsonSerializer.Serialize(new{validationSource=ApplicationProfile.SourceCommit,validationInstance=instance});
        Assert.Null(p.AuthorizeToolbar(Body(p.Instance!)));Assert.NotNull(p.AuthorizeToolbar(Body(new string('a',32))));Assert.NotNull(p.AuthorizeToolbar("{}"));
        Assert.NotNull(p.AuthorizeToolbar(JsonSerializer.Serialize(new{betaSource=ApplicationProfile.SourceCommit,betaVersion=ApplicationProfile.BetaVersion})));
    }
    [Fact] public async Task CandidateCannotFetchOrOfferPublicInstallersEvenWithAnExplicitCheck()
    {
        var root=Path.Combine(Path.GetTempPath(),"candidate-updates-"+Guid.NewGuid().ToString("N"));
        try{
            Directory.CreateDirectory(root);PreferenceStore.WriteAtomic(Path.Combine(root,"updates.json"),new UpdateSnapshot("0.7.0",new(true,true),new("v0.9.0","https://github.com/JustSeanC/AeroScience/releases/tag/v0.9.0","AeroScience-Setup-v0.9.0.exe",false)));
            using var client=new HttpClient(new NoNetwork());var updates=new ApplicationUpdates(root,client,Candidate());Assert.Null(updates.Get().Available);Assert.False(updates.Get().Preferences.Automatic);
            var state=await updates.Check(true);Assert.Equal("0.8.1-candidate",state.CurrentVersion);Assert.Null(state.Available);Assert.False(state.Preferences.Automatic);Assert.Contains("develop/v0.8.1",state.Message);
        }finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    private sealed class NoNetwork:HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t)=>throw new InvalidOperationException("Candidate must not contact release services.");}
}
