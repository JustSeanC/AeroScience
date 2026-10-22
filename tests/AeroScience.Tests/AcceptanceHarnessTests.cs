using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;

namespace AeroScience.Tests;

// Synthetic regression only: the recorders refuse these unpinned files as genuine evidence.
[Collection(ScientificIoCollection.Name)]
public sealed class AcceptanceHarnessTests
{
    [Fact]
    public async Task GuidedHarnessReleasesOriginalOwnerThenReopensExactProductAndBuildsWorkspace()
    {
        var root=Path.Combine(Path.GetTempPath(),"acceptance-lifecycle-"+Guid.NewGuid().ToString("N"));
        try
        {
            var originals=Path.Combine(root,"SYNTHETIC-originals");RemoteFixtureFiles.Create(originals,RemoteKind.Hyperspectral);
            File.AppendAllText(Path.Combine(originals,"cube.hdr"),"data units = reflectance\n");
            var output=Path.Combine(root,"run");Directory.CreateDirectory(output);
            var paths=new StorageManager(new PreferenceStore(output)).Initialize();
            var missions=new MissionStore(Path.Combine(output,"missions.db"));missions.Initialize();
            using var science=new ScienceStore(paths.ScienceLibraryDirectory);science.Initialize();missions.ScienceLibrary=science;
            using(var competitor=new ScienceStore(paths.ScienceLibraryDirectory))Assert.Throws<IOException>(()=>competitor.Initialize());
            await GuidedEnviAcceptance.Run(Path.Combine(originals,"cube.hdr"),output,science,missions,workspace:true);
            using var saved=JsonDocument.Parse(File.ReadAllText(Path.Combine(output,"guided-envi-acceptance.json")));
            Assert.True(saved.RootElement.GetProperty("offlineReopen").GetBoolean());
            Assert.True(saved.RootElement.GetProperty("duplicateReused").GetBoolean());
            Assert.True(saved.RootElement.GetProperty("externalOriginalsPreserved").GetBoolean());
            Assert.Equal("not tested",saved.RootElement.GetProperty("windowsMsfs").GetString());
            Assert.True(File.Exists(Path.Combine(output,"workspace-acceptance.json")));
            using var third=new ScienceStore(paths.ScienceLibraryDirectory);third.Initialize();
            Assert.Single(third.Sources());Assert.Single(third.Products());
        }
        finally { if(Directory.Exists(root))Directory.Delete(root,true); }
    }
}
