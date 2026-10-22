using System.Text.Json;
using AeroScience.Missions;
using Xunit;

namespace AeroScience.Tests;

public sealed class NasaOriginalHarnessTests
{
    // These are explicitly synthetic regression cubes, never NASA numeric acceptance.
    [Theory][InlineData("classic")][InlineData("ng")]
    public async Task OwnerLocalVerifierUsesFamilyBoundOriginalsWithoutCreatingFlightOrChangingFiles(string family)
    {
        var root=Path.Combine(Path.GetTempPath(),"nasa-owner-harness-"+Guid.NewGuid().ToString("N"));
        try
        {
            var item=NasaFixtureFiles.Item(family);var header=NasaFixtureFiles.Cube(Path.Combine(root,"SYNTHETIC"),item,bands:family=="classic"?224:425);
            var inspection=NasaGuidedImport.Inspect(header,item);var paths=new StorageManager(new PreferenceStore(Path.Combine(root,"settings"))).Initialize();string retained;
            using(var science=new ScienceStore(paths.ScienceLibraryDirectory))
            {science.Initialize();var source=await science.ImportSourceAsync(inspection.Manifest!,inspection.Root);retained=Path.Combine(science.DirectoryPath,source.RelativeDirectory);}
            var original=Directory.GetFiles(retained).ToDictionary(p=>p,p=>File.ReadAllBytes(p));
            var output=Path.Combine(root,"output");Directory.CreateDirectory(output);NasaOriginalAcceptance.Run(retained,family,output);
            using var report=JsonDocument.Parse(File.ReadAllText(Path.Combine(output,"nasa-original-samples.json")));
            Assert.Equal(0,report.RootElement.GetProperty("flightObservationsCreated").GetInt32());
            Assert.Equal(family,report.RootElement.GetProperty("family").GetString());
            Assert.Contains("Not established",report.RootElement.GetProperty("authorization").GetString());
            Assert.Throws<IOException>(()=>NasaOriginalAcceptance.Run(retained,family=="classic"?"ng":"classic",output));
            foreach(var entry in original)Assert.Equal(entry.Value,File.ReadAllBytes(entry.Key));
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
