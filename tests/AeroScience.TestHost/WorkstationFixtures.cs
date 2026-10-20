using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.Tests;
using Microsoft.Data.Sqlite;

// Synthetic UI/load inputs only. Never compiled into the companion or genuine-data
// acceptance harness. Bulk catalog seeding is not recorder/durability acceptance.
public sealed record WorkstationFixtureRequest(int Acquisitions=144,int Observations=720);
public static class WorkstationFixtures
{
    public static async Task<object> Create(WorkstationFixtureRequest request,ScienceStore science,MissionStore missions,MissionCoordinator coordinator,EngineFixturePaths paths)
    {
        var count=Math.Clamp(request.Acquisitions,3,10000);var observationCount=Math.Clamp(request.Observations,0,30000);
        if(coordinator.Snapshot().ActiveMission is {} active)coordinator.End(active.Id);
        var originals=new List<RemoteProductData>();var missionIds=new List<Guid>();
        foreach(var kind in new[]{RemoteKind.Terrain,RemoteKind.Thermal,RemoteKind.Multispectral})
        {
            var folder=Path.Combine(paths.Root,"workstation-inputs",Guid.NewGuid().ToString("N"));
            var manifestPath=RemoteFixtureFiles.Create(folder,kind);var manifest=JsonSerializer.Deserialize<ScientificSourceManifest>(File.ReadAllText(manifestPath),WireJson.Options)!;
            await science.ImportSourceAsync(manifest,folder,reuseExisting:true);
            var instrument=RemoteAcquisition.Instruments.Single(p=>p.Value==kind).Key;
            var mission=coordinator.Start(new("SYNTHETIC WORKSTATION FIXTURE — "+kind,Instruments:[new(instrument,true)]));missionIds.Add(mission.Id);
            var utc=DateTimeOffset.UtcNow;var frame=new TelemetryFrame(utc,TelemetrySource.Simulated,"SYNTHETIC UI/load fixture; no MSFS",40,-75,1000,328.0839895,140,150,160,0,0,0,0,0);
            coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"SYNTHETIC UI/load fixture",utc),frame));
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));while(science.Products(mission.Id).Length==0)await Task.Delay(10,timeout.Token);
            originals.Add(science.ReadProduct(science.Products(mission.Id).First().Id));coordinator.End(mission.Id);
        }
        var primary=missions.Detail(missionIds[0])!.Mission;
        var datasets=originals.ToDictionary(data=>data.Product.InstrumentId,data=>science.EnsureDataset(primary,new(data.Product.InstrumentId,true),missions.Research(primary.Id)));
        // Real JSON files + exact hashes + normal R-tree insert triggers. One test-only
        // transaction avoids turning CI fixture generation into a disk-flush benchmark.
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=science.DatabasePath,Pooling=false}.ToString()))
        {
            db.Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
            for(var index=3;index<count;index++)
            {
                var original=originals[index%originals.Count];var id=Guid.NewGuid();var flight=original.Product.FlightPacketUtc.AddSeconds(index);
                var product=original.Product with{Id=id,MissionId=primary.Id,DatasetId=datasets[original.Product.InstrumentId].Id,FlightPacketUtc=flight,RelativePath=$"Products/{primary.Id:N}/{id:N}.json",Bytes=0,Sha256="",Provenance="SYNTHETIC UI/load fixture; repeated saved support, not a real flight or provider observation"};
                var data=original with{Product=product,Evidence=original.Evidence is {} evidence?evidence with{Aircraft=evidence.Aircraft with{TimestampUtc=flight}}:null};
                var bytes=JsonSerializer.SerializeToUtf8Bytes(data,WireJson.Options);var path=Path.Combine(science.DirectoryPath,product.RelativePath);Directory.CreateDirectory(Path.GetDirectoryName(path)!);await File.WriteAllBytesAsync(path,bytes);
                product=product with{Bytes=bytes.LongLength,Sha256=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()};
                cmd.CommandText="INSERT INTO remote_products VALUES($id,$dataset,$mission,$instrument,$utc,$json)";cmd.Parameters.Clear();
                foreach(var (key,value) in new[]{("$id",product.Id.ToString()),("$dataset",product.DatasetId.ToString()),("$mission",primary.Id.ToString()),("$instrument",product.InstrumentId),("$utc",product.FlightPacketUtc.ToString("O")),("$json",JsonSerializer.Serialize(product,WireJson.Options))})cmd.Parameters.AddWithValue(key,value);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        var temperature=science.EnsureDataset(primary,new("ambient-temperature",true),missions.Research(primary.Id));var wind=science.EnsureDataset(primary,new("ambient-wind",true),missions.Research(primary.Id));
        var start=DateTimeOffset.UtcNow.AddHours(-1);var observations=Enumerable.Range(0,observationCount).Select(index=>new ScienceObservation(0,index%2==0?temperature.Id:wind.Id,primary.Id,index%2==0?"ambient-temperature":"ambient-wind",start.AddSeconds(index),index%2==0?"temperature":"wind-speed",15+(index%33)*.1,index%2==0?"degC":"kt",ScienceProvenance.SimulatorReported,"SYNTHETIC recorded environmental fixture",40+(index%40)*.00001,-75+(index/40)*.00001,1000,index%17==0?ObservationQuality.Stale:ObservationQuality.Valid,start.AddSeconds(index))).ToArray();
        science.Append(observations);
        return new{synthetic=true,scope="UI/load only; not provider, recorder, Windows or MSFS acceptance",missionId=primary.Id,missionIds,geographicAcquisitions=count,primaryAcquisitions=count-2,atmosphericValues=observationCount,products=originals.Select(data=>data.Product.Id)};
    }
}
public sealed record EngineFixturePaths(string Root);
