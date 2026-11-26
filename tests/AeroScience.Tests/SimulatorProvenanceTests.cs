using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.Telemetry;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AeroScience.Tests;

public sealed class SimulatorProvenanceTests : IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"aeroscience-p04-origin",Guid.NewGuid().ToString("N"));
    private MissionStore Store(){var store=new MissionStore(Path.Combine(directory,"missions.db"));store.Initialize();return store;}
    private static SimulatorOrigin Origin(string simulator="xplane12") => new(1,simulator,null,
        simulator=="xplane12"?"aeroscience.xplane.web":"aeroscience.simconnect","fixture","v1",Guid.NewGuid(),"fixture-map")
        {TimestampBasis=SimulatorProvenance.HostReceiptBasis,Limitations=["Synthetic fixture; no installed-simulator acceptance"]};
    private string Scalar(string query)
    {using var db=new SqliteConnection($"Data Source={Path.Combine(directory,"missions.db")};Pooling=False");db.Open();using var cmd=db.CreateCommand();cmd.CommandText=query;return Convert.ToString(cmd.ExecuteScalar(),System.Globalization.CultureInfo.InvariantCulture)!;}
    private void Execute(string query)
    {using var db=new SqliteConnection($"Data Source={Path.Combine(directory,"missions.db")};Pooling=False");db.Open();using var cmd=db.CreateCommand();cmd.CommandText=query;cmd.ExecuteNonQuery();}

    [Fact] public void Schema13UpgradeBacksUpExactOriginalRowsWithoutInferringSource()
    {
        var store=Store();var at=DateTimeOffset.UtcNow.AddDays(-1);var frame=SimulatedTelemetryProvider.Generate(at,0) with{Source=TelemetrySource.Simulator};
        var id=store.Create("Ambiguous historical simulator",frame,at);store.Append(id,frame);store.Finish(id,MissionStatus.Completed,at.AddSeconds(1),"Fixture");
        var original=Scalar("SELECT payload_json FROM telemetry");var detail=JsonSerializer.Serialize(store.Detail(id),WireJson.Options);
        Execute("DROP TABLE mission_simulator_origins; PRAGMA user_version=13");var fingerprint=StorageManager.DatabaseFingerprint(store.DatabasePath);
        store.Initialize();Assert.Equal("14",Scalar("PRAGMA user_version"));
        var backup=Assert.Single(Directory.GetFiles(Path.Combine(directory,"Backups"),"missions-schema13-*.db"));
        Assert.Equal(fingerprint,StorageManager.DatabaseFingerprint(backup));Assert.Equal(original,Scalar("SELECT payload_json FROM telemetry"));
        Assert.Equal(detail,JsonSerializer.Serialize(store.Detail(id),WireJson.Options));Assert.Null(store.SimulatorOrigin(id));
        Assert.Null(store.Detail(id)!.Mission.SimulatorOrigin);Assert.Equal("0",Scalar("SELECT COUNT(*) FROM mission_simulator_origins"));
        store.Initialize();Assert.Single(Directory.GetFiles(Path.Combine(directory,"Backups"),"missions-schema13-*.db"));
    }
    [Fact] public void FutureDatabaseAndOriginVersionsFailClosedWithoutRewritingEvidence()
    {
        var store=Store();var origin=Origin();var at=DateTimeOffset.UtcNow;var id=store.Create("Qualified fixture",null,at,simulatorOrigin:origin);
        var before=StorageManager.DatabaseFingerprint(store.DatabasePath);
        Execute("PRAGMA user_version=15");var future=StorageManager.DatabaseFingerprint(store.DatabasePath);
        Assert.Throws<InvalidOperationException>(()=>store.Initialize());Assert.Equal(future,StorageManager.DatabaseFingerprint(store.DatabasePath));
        Execute("PRAGMA user_version=14");Assert.Equal(before,StorageManager.DatabaseFingerprint(store.DatabasePath));
        var json=JsonSerializer.Serialize(origin with{Version=2},WireJson.Options);
        using var db=new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");db.Open();using var cmd=db.CreateCommand();
        cmd.CommandText="UPDATE mission_simulator_origins SET payload_json=$json,sha256=$hash";cmd.Parameters.AddWithValue("$json",json);
        cmd.Parameters.AddWithValue("$hash",Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json))).ToLowerInvariant());cmd.ExecuteNonQuery();
        Assert.Throws<NotSupportedException>(()=>store.Detail(id));Assert.Throws<NotSupportedException>(()=>store.SimulatorOrigin(id));
    }
    [Fact] public void OriginMigrationIsTransactionalAndRefusesAnUnverifiableBackupBudget()
    {
        var store=Store();Execute("DROP TABLE mission_simulator_origins; PRAGMA user_version=13");var before=StorageManager.DatabaseFingerprint(store.DatabasePath);
        store.EvidenceFreeBytes=()=>0;Assert.Throws<IOException>(()=>store.Initialize());Assert.Equal(before,StorageManager.DatabaseFingerprint(store.DatabasePath));
        store.EvidenceFreeBytes=null;store.EvidenceFault=point=>{if(point=="simulator-origin-migration-before-commit")throw new IOException("Synthetic power-loss boundary");};
        Assert.Throws<IOException>(()=>store.Initialize());Assert.Equal(before,StorageManager.DatabaseFingerprint(store.DatabasePath));Assert.Equal("13",Scalar("PRAGMA user_version"));
        store.EvidenceFault=null;store.Initialize();Assert.Equal("14",Scalar("PRAGMA user_version"));
    }
    [Fact] public void AnOlderSchemaClaimCannotHideRetainedSourceQualifiedEvidence()
    {
        var store=Store();var id=store.Create("Source cannot be downgraded",null,DateTimeOffset.UtcNow,simulatorOrigin:Origin());
        Execute("PRAGMA user_version=13");var before=StorageManager.DatabaseFingerprint(store.DatabasePath);
        Assert.Throws<IOException>(()=>store.Detail(id));Assert.Throws<IOException>(()=>store.SimulatorOrigin(id));
        Assert.Equal(before,StorageManager.DatabaseFingerprint(store.DatabasePath));
    }
    [Theory] [InlineData("msfs2024")] [InlineData("xplane12")]
    public void FrozenOriginRoundTripsAndRejectsAnotherSimulatorOrEpoch(string simulator)
    {
        var store=Store();var origin=Origin(simulator);var fixture=new XPlaneAssemblerFixture();fixture.Seed();fixture.Confirm();var packet=fixture.Assembler.Snapshot()!;
        var context=packet.SampleContext! with{ConnectionEpoch=origin.ConnectionEpoch};
        var sample=packet.Frame! with{SimulatorOrigin=origin,SimulatorContext=simulator=="xplane12"?context:null};
        var id=store.Create("Frozen fixture",sample,sample.TimestampUtc,simulatorOrigin:origin);store.Append(id,sample);
        Assert.Equal(JsonSerializer.Serialize(origin,WireJson.Options),JsonSerializer.Serialize(store.Detail(id)!.Mission.SimulatorOrigin,WireJson.Options));
        Assert.Equal(origin.SimulatorId,Assert.Single(store.List()).SimulatorOrigin!.SimulatorId);
        var before=StorageManager.DatabaseFingerprint(store.DatabasePath);
        Assert.Throws<InvalidOperationException>(()=>store.Append(id,sample with{SimulatorOrigin=origin with{ConnectionEpoch=Guid.NewGuid()}}));
        Assert.Throws<InvalidOperationException>(()=>store.Append(id,sample with{SimulatorOrigin=origin with{SimulatorId=simulator=="xplane12"?"msfs2024":"xplane12"}}));
        Assert.Throws<InvalidOperationException>(()=>store.Append(id,sample with{SimulatorOrigin=null}));
        Assert.Equal(before,StorageManager.DatabaseFingerprint(store.DatabasePath));Assert.Single(store.Samples(id,10,0));
        Execute("UPDATE mission_simulator_origins SET payload_json=payload_json||' '");Assert.Throws<IOException>(()=>store.Detail(id));
    }
    [Fact] public void OptionalMetadataPreservesLegacyJsonAndTelemetryValidity()
    {
        var frame=SimulatedTelemetryProvider.Generate(DateTimeOffset.UtcNow,0);var json=JsonSerializer.Serialize(frame,WireJson.Options);
        Assert.DoesNotContain("simulatorOrigin",json);Assert.DoesNotContain("simulatorContext",json);
        Assert.Equal(json,JsonSerializer.Serialize(JsonSerializer.Deserialize<TelemetryFrame>(json,WireJson.Options),WireJson.Options));
        Assert.False((frame with{Latitude=double.NaN}).IsValid);Assert.True((frame with{GroundspeedKnots=0,AltitudeAglFeet=null}).IsValid);
        Assert.Throws<ArgumentException>(()=>SimulatorProvenance.Validate(Origin() with{Version=2}));
    }
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
