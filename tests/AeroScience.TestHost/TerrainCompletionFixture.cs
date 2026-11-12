using AeroScience.Missions;
using AeroScience.Shared;

namespace AeroScience.TestHost;

// Test-host-only synthetic archived collection inputs; never published with AeroScience.
internal static class TerrainCompletionFixture
{
    internal sealed record Fixture(Guid MissionId,Guid GeometryId,Guid SourceId,Guid JobId,bool SyntheticFlight,bool SyntheticSource);
    public static async Task<Fixture> Create(MissionStore missions,ScienceStore science,string root,string? genuineRaster=null)
    {
        var utc=DateTimeOffset.UtcNow.AddMinutes(-10);InstrumentConfiguration[] settings=[new("terrain-scanner",true,1000,new(InstrumentProfiles.Generic("terrain-scanner")))];
        var id=missions.Create("SYNTHETIC P05b post-flight corridor",null,utc,instruments:settings);missions.InitializeCollectionEvidence(new(1,id,0,CollectionPhase.Armed,CollectionAuthority.Manual,ControlRevision:0));
        var controller=new ScienceCollectionController(missions,id,()=>settings);
        CollectionCommitResult Control(CollectionAction action,int second){var state=controller.State;return controller.Control(new(1,id,Guid.NewGuid(),state.Revision,action,state.CurrentSessionId),utc.AddSeconds(second));}
        Control(CollectionAction.StartData,0);
        for(var i=0;i<3;i++)
        {
            var p=Geodesy.Direct(new(40,-75),0,i*100);var time=utc.AddSeconds(i);var frame=new TelemetryFrame(time,TelemetrySource.Simulated,"SYNTHETIC P05b browser fixture",p.Latitude,p.Longitude,5000,1000,200,200,400,0,0,0,0,0);
            var packet=new CollectionInputReceipt("synthetic-p05b-"+i,new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Synthetic fixture",time),frame));var state=controller.State;var next=state with{Revision=state.Revision+1};
            var e=new CollectionEvent(1,id,next.Revision,time,time,state.CurrentSessionId,CollectionAction.Unknown,CollectionEventOrigin.Engine,CollectionEventOutcome.Applied,state.Phase,state.Phase,state.Authority,MissionStore.IngressReason,[MissionStore.DescribeCollectionInput(packet)],ExpectedSessionId:state.CurrentSessionId);
            missions.CommitCollectionEvidence(new(next,e,Inputs:[packet],FlightSample:frame,EngineDecisionId:Guid.NewGuid()));
        }
        Control(CollectionAction.EndData,3);missions.Finish(id,MissionStatus.Completed,utc.AddSeconds(3),"Synthetic P05b fixture");
        ScientificSource source;
        if(genuineRaster is null){var folder=Path.Combine(root,"synthetic-p05b",Guid.NewGuid().ToString("N"));var manifest=AeroScience.Tests.RemoteFixtureFiles.Create(folder,RemoteKind.Terrain);source=await science.ImportSourceAsync(manifest);}
        else
        {
            var metadataPath=Path.Combine(Path.GetDirectoryName(genuineRaster)!,"service-metadata.json");if(new FileInfo(metadataPath).Length>1048576)throw new InvalidDataException("Bounded service metadata required.");
            using var metadata=System.Text.Json.JsonDocument.Parse(File.ReadAllText(metadataPath));if(metadata.RootElement.GetProperty("pixelType").GetString()!="F32"||metadata.RootElement.GetProperty("bandCount").GetInt32()!=1)throw new InvalidDataException("USGS single-band float elevation contract changed.");
            source=await science.ImportSourceAsync(new(1,"Genuine USGS 3DEP composite export (bounded P05b acceptance)",RemoteKind.Terrain,"USGS 3DEP composite","USGS public domain; https://www.usgs.gov/information-policies-and-instructions/copyrights-and-credits","https://elevation.nationalmap.gov/arcgis/rest/services/3DEPElevation/ImageServer","bounded-32x32-export",null,"Unknown: composite export does not establish a native vertical reference",[new("elevation",Path.GetFileName(genuineRaster),"geotiff",Unit:"m")],Notes:"Composite numeric delivered grid; native per-pixel resolution and historical survey date unspecified. Synthetic archived flight geometry, genuine source raster.",OriginalMetadataFiles:["request.json","service-metadata.json"]),Path.GetDirectoryName(genuineRaster)!);
            var grid=source.Grids["elevation"];if((long)grid.Width*grid.Height>4096)throw new InvalidDataException("The genuine acceptance probe is limited to 4096 delivered native cells.");
        }
        var geometry=ContinuousTerrainSurveys.Build(science,missions,id);var job=TerrainElevations.Create(science,missions,id,new(geometry.Id,[new(source.Id,source.ManifestSha256,Surface:"dem",LocalDefinitionAcknowledged:true,UnverifiedDatumAcknowledged:genuineRaster is not null)],new(TileSize:4,MaximumScratchBytes:4194304)));
        return new(id,geometry.Id,source.Id,job.Id,true,genuineRaster is null);
    }
}
