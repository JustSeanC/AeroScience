using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.Telemetry;
using Microsoft.Data.Sqlite;
using Xunit;
namespace AeroScience.Tests;

public sealed class Persistence081Tests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-p05-"+Guid.NewGuid().ToString("N"));
    private MissionStore Store(){var store=new MissionStore(Path.Combine(root,"Missions","missions.db"));store.Initialize();return store;}
    private static SimulatorOrigin Origin(string id)=>new(1,id,null,id=="xplane12"?"aeroscience.xplane.web":"aeroscience.simconnect","fixture",id=="xplane12"?"v1":null,Guid.NewGuid(),"fixture-map"){TimestampBasis=SimulatorProvenance.HostReceiptBasis};
    private static void Sql(string path,string sql){using var db=new SqliteConnection($"Data Source={path};Pooling=False");db.Open();using var cmd=db.CreateCommand();cmd.CommandText=sql;cmd.ExecuteNonQuery();}
    private static string Scalar(string path,string sql){using var db=new SqliteConnection($"Data Source={path};Pooling=False");db.Open();using var cmd=db.CreateCommand();cmd.CommandText=sql;return Convert.ToString(cmd.ExecuteScalar(),System.Globalization.CultureInfo.InvariantCulture)!;}
    private static (Guid Id,string Payload) Legacy(MissionStore store)
    {
        var at=DateTimeOffset.UtcNow.AddDays(-1);var f=SimulatedTelemetryProvider.Generate(at,0) with{Source=TelemetrySource.Simulator};
        var id=store.Create("Unknown historical simulator",f,at);store.Append(id,f);store.Finish(id,MissionStatus.Completed,at,"Synthetic fixture");
        return(id,JsonSerializer.Serialize(f,WireJson.Options));
    }
    [Theory][InlineData(7)][InlineData(13)]
    public void OlderValidSequencesPreserveOriginalPayloadsAndRetainVerifiedPreupgradeBackup(int version)
    {
        var store=Store();var original=Legacy(store);
        if(version==7)EvidenceTestDatabase.RemoveSchemaEight(store.DatabasePath);else Sql(store.DatabasePath,"DROP TABLE mission_simulator_origins;PRAGMA user_version=13");
        var before=StorageManager.DatabaseFingerprint(store.DatabasePath);store.Initialize();
        Assert.Equal("14",Scalar(store.DatabasePath,"PRAGMA user_version"));Assert.Null(store.Detail(original.Id)!.Mission.SimulatorOrigin);
        Assert.Equal(original.Payload,Scalar(store.DatabasePath,"SELECT payload_json FROM telemetry"));
        var backup=Assert.Single(Directory.GetFiles(Path.Combine(root,"Missions","Backups"),$"missions-schema{version}-*.db"));
        Assert.Equal(before,StorageManager.DatabaseFingerprint(backup));
    }
    [Fact] public void InterruptedOlderUpgradeKeepsOriginalEvidenceAndCanRecoverFromVerifiedCopy()
    {
        var store=Store();var original=Legacy(store);EvidenceTestDatabase.RemoveSchemaEight(store.DatabasePath);
        var before=StorageManager.DatabaseFingerprint(store.DatabasePath);
        store.EvidenceFault=point=>{if(point=="simulator-origin-migration-before-commit")throw new IOException("Synthetic interruption");};
        Assert.Throws<IOException>(()=>store.Initialize());Assert.Equal(original.Payload,Scalar(store.DatabasePath,"SELECT payload_json FROM telemetry"));
        Assert.Equal("13",Scalar(store.DatabasePath,"PRAGMA user_version"));
        var backup=Assert.Single(Directory.GetFiles(Path.Combine(root,"Missions","Backups"),"missions-schema7-*.db"));Assert.Equal(before,StorageManager.DatabaseFingerprint(backup));
        var restored=Path.Combine(root,"Restored");Directory.CreateDirectory(restored);File.Copy(backup,Path.Combine(restored,"missions.db"));
        var recovered=new MissionStore(Path.Combine(restored,"missions.db"));recovered.Initialize();Assert.Equal(original.Payload,Scalar(recovered.DatabasePath,"SELECT payload_json FROM telemetry"));Assert.Null(recovered.Detail(original.Id)!.Mission.SimulatorOrigin);
        store.EvidenceFault=null;store.Initialize();Assert.Null(store.Detail(original.Id)!.Mission.SimulatorOrigin);
    }
    [Fact] public async Task SQLiteWriterContentionWaitsWithoutPartiallyCreatingOrigins()
    {
        var store=Store();var original=Legacy(store);Sql(store.DatabasePath,"DROP TABLE mission_simulator_origins;PRAGMA user_version=13");
        using var db=new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");db.Open();using var tx=db.BeginTransaction();
        using(var cmd=db.CreateCommand()){cmd.Transaction=tx;cmd.CommandText="UPDATE missions SET name=name";cmd.ExecuteNonQuery();}
        var task=Task.Run(()=>store.Initialize());try{await Task.Delay(150);Assert.False(task.IsCompleted);}finally{tx.Rollback();}
        await task.WaitAsync(TimeSpan.FromSeconds(20));Assert.Equal("14",Scalar(store.DatabasePath,"PRAGMA user_version"));Assert.Equal(original.Payload,Scalar(store.DatabasePath,"SELECT payload_json FROM telemetry"));
    }
    [Theory][InlineData("msfs2024")][InlineData("xplane12")]
    public async Task BackupsAndNavigationExportsKeepSourceAndDoNotPretendToBeFullScientificEvidence(string simulator)
    {
        var store=Store();var origin=Origin(simulator);var at=DateTimeOffset.UtcNow;var f=SimulatedTelemetryProvider.Generate(at,0) with{Source=TelemetrySource.Simulator,SimulatorOrigin=origin};
        if(simulator=="xplane12") {var fixture=new XPlaneAssemblerFixture();fixture.Seed();fixture.Confirm();var packet=fixture.Assembler.Snapshot()!;f=packet.Frame! with{SimulatorOrigin=origin,SimulatorContext=packet.SampleContext! with{ConnectionEpoch=origin.ConnectionEpoch}};}
        var id=store.Create("Source round trip",f,f.TimestampUtc,simulatorOrigin:origin);store.Append(id,f);store.Finish(id,MissionStatus.Completed,f.TimestampUtc,"Fixture");
        var before=StorageManager.DatabaseFingerprint(store.DatabasePath);var backupRoot=Path.Combine(root,"Backup");Directory.CreateDirectory(backupRoot);
        var backup=DataBackup.Create(store.DatabasePath,backupRoot,false);Assert.Equal(before,StorageManager.DatabaseFingerprint(backup));
        var recovered=new MissionStore(backup);recovered.Initialize();Assert.Equal(origin,recovered.Detail(id)!.Mission.SimulatorOrigin);Assert.Equal(f,Assert.Single(recovered.Samples(id,10,0)));
        using var output=new MemoryStream();await store.ExportJsonAsync(id,output);using var json=JsonDocument.Parse(output.ToArray());var proof=json.RootElement.GetProperty("simulatorOriginEvidence");
        Assert.Equal(origin,JsonSerializer.Deserialize<SimulatorOrigin>(proof.GetProperty("origin"),WireJson.Options));
        Assert.Equal(proof.GetProperty("sha256").GetString(),Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(proof.GetProperty("origin").GetRawText()))));
        using var csv=new MemoryStream();await ResearchExports.CsvAsync(store,id,"flight",csv,default);Assert.Contains("simulator_origin_json",System.Text.Encoding.UTF8.GetString(csv.ToArray()));Assert.Contains(simulator,System.Text.Encoding.UTF8.GetString(csv.ToArray()));
        foreach(var kind in new[]{"gpx","kml"}){using var file=new MemoryStream();if(kind=="gpx")await ResearchExports.GpxAsync(store,id,file,default);else await ResearchExports.KmlAsync(store,id,file,default);var text=System.Text.Encoding.UTF8.GetString(file.ToArray());Assert.Contains(simulator,text);Assert.Contains("Navigation/summary export only",text);}
    }
    [Fact] public void FlightDeletionCascadesOnlyItsOriginsWhileScienceOnlyOwnershipRemainsSeparate()
    {
        var store=Store();var id=store.Create("Owned origin",null,DateTimeOffset.UtcNow,simulatorOrigin:Origin("msfs2024"));store.Finish(id,MissionStatus.Completed,DateTimeOffset.UtcNow,"Fixture");
        Assert.Equal("1",Scalar(store.DatabasePath,"SELECT COUNT(*) FROM mission_simulator_origins"));
        Assert.Throws<InvalidOperationException>(()=>store.ClearFlightData("wrong"));Assert.NotNull(store.Detail(id));
        store.ClearFlightData("DELETE FLIGHT DATA");Assert.Equal("0",Scalar(store.DatabasePath,"SELECT COUNT(*) FROM mission_simulator_origins"));Assert.Empty(store.List());
    }
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
}
