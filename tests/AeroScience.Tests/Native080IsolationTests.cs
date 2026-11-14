using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Missions;
using Xunit;
namespace AeroScience.Tests;

public sealed class Native080IsolationTests
{
    private static ApplicationProfile Profile(string instance="11111111111111111111111111111111")=>new(true,"0.8.0",instance);
    [Fact]public void AttemptNamespacesAreDistinctAndLegacyIdentitiesRemainOriginal()
    {
        var a=Profile();var b=Profile("22222222222222222222222222222222");var legacy=new ApplicationProfile(true);var stable=new ApplicationProfile(false);
        Assert.Equal(5088,a.Port);Assert.Equal(5086,legacy.Port);Assert.Equal(5080,stable.Port);
        Assert.Equal("AeroScience-Validation-v0.7.0",legacy.Folder);Assert.Equal("AeroScience",stable.Folder);
        Assert.Equal("aeroscience-validation-v0-8-0-toolbar",a.ToolbarPackage);Assert.Equal("AeroScienceValidationV080",a.ToolbarFolder);
        Assert.Equal("InGamePanels/aeroscience_validation_v080_panel.spb",a.ToolbarRegistrationPath);
        Assert.NotEqual(a.RegistryKey,b.RegistryKey);Assert.NotEqual(a.ConfigurationDirectory,b.ConfigurationDirectory);Assert.NotEqual(a.SessionCookieName,b.SessionCookieName);
        Assert.NotEqual(a.CommandEvent,legacy.CommandEvent);Assert.NotEqual(a.CommandEvent,stable.CommandEvent);
        Assert.Equal(@"Local\AeroScience.Desktop",ApplicationProfile.DesktopMutex);
        var confined=new PreferenceStore(a.ConfigurationDirectory,a.ConfigurationDirectory);
        Assert.Throws<IOException>(()=>confined.ValidateDirectory(legacy.ConfigurationDirectory));
        Assert.Throws<IOException>(()=>confined.ValidateDirectory(stable.ConfigurationDirectory));
        Assert.Throws<IOException>(()=>confined.ValidateDirectory(b.ConfigurationDirectory));
    }
    [Fact]public void CommandsNeedBothCompiledSourceAndAttemptWithoutChangingLegacyAuthorization()
    {
        var p=Profile();string Command(string? source,string? instance)=>JsonSerializer.Serialize(new{validationSource=source,validationInstance=instance});
        Assert.Null(p.AuthorizeToolbar(Command(ApplicationProfile.SourceCommit,p.Instance)));
        Assert.NotNull(p.AuthorizeToolbar(Command(ApplicationProfile.SourceCommit,null)));
        Assert.NotNull(p.AuthorizeToolbar(Command(ApplicationProfile.SourceCommit,"22222222222222222222222222222222")));
        Assert.NotNull(p.AuthorizeToolbar(Command(new string('f',40),p.Instance)));
        Assert.Null(new ApplicationProfile(true).AuthorizeToolbar(Command(ApplicationProfile.SourceCommit,null)));
        Assert.Throws<InvalidOperationException>(()=>Profile("../other").ConfigurationDirectory);
        Assert.Throws<InvalidOperationException>(()=>new ApplicationProfile(true,"0.9.0").Port);
    }
    [Fact]public void ProfileMarkerCannotAdoptAnotherSourceOrAttempt()
    {
        var directory=Path.Combine(Path.GetTempPath(),"as080-marker-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            var p=Profile();Assert.Throws<IOException>(()=>p.ValidatePrivateMarker(directory));
            void Marker(string source,string instance)=>File.WriteAllText(Path.Combine(directory,"validation-profile.json"),JsonSerializer.Serialize(new{version=1,source_commit=source,instance}));
            Marker(ApplicationProfile.SourceCommit,p.Instance!);p.ValidatePrivateMarker(directory);
            Marker(new string('f',40),p.Instance!);Assert.Throws<IOException>(()=>p.ValidatePrivateMarker(directory));
            Marker(ApplicationProfile.SourceCommit,"22222222222222222222222222222222");Assert.Throws<IOException>(()=>p.ValidatePrivateMarker(directory));
        }finally{Directory.Delete(directory,true);}
    }
    [Fact]public void PrivateToolbarDestinationMustBeCommunityAndCannotOverlapDataOrBundle()
    {
        var root=Path.Combine(Path.GetTempPath(),"as080-dest-"+Guid.NewGuid().ToString("N"));
        try
        {
            var community=Path.Combine(root,"MSFS","Community");var data=Path.Combine(root,"OriginalScience");var bundle=Path.Combine(root,"App","Toolbar");
            Directory.CreateDirectory(community);Directory.CreateDirectory(data);Directory.CreateDirectory(bundle);
            var original=Path.Combine(data,"original.txt");File.WriteAllText(original,"unchanged");
            NativeValidationPaths.ValidateCommunity(community,bundle,[data]);
            Assert.Throws<IOException>(()=>NativeValidationPaths.ValidateCommunity(data,bundle,[]));
            Assert.Throws<IOException>(()=>NativeValidationPaths.ValidateCommunity(community,bundle,[Path.GetDirectoryName(community)!]));
            var inside=Path.Combine(data,"Community");Directory.CreateDirectory(inside);
            Assert.Throws<IOException>(()=>NativeValidationPaths.ValidateCommunity(inside,bundle,[data]));
            Assert.Equal("unchanged",File.ReadAllText(original));Assert.Empty(Directory.GetFiles(community));
        }finally{Directory.Delete(root,true);}
    }
}
