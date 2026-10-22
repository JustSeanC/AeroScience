using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Xunit;
namespace AeroScience.Tests;

[Collection(ScientificIoCollection.Name)]
public sealed class MissionStartAdvisoryTests:IDisposable
{
    readonly string root=Path.Combine(Path.GetTempPath(),"start-readiness-"+Guid.NewGuid().ToString("N"));
    readonly MissionStore store;readonly ScienceStore science;readonly ScientificAccessSettings settings;readonly AutomaticScientificData automatic;readonly ScientificPlanData plans;
    public MissionStartAdvisoryTests(){var paths=new StorageManager(new PreferenceStore(root)).Initialize();store=new(Path.Combine(paths.MissionDirectory,"missions.db"));store.Initialize();science=new(paths.ScienceLibraryDirectory);science.Initialize();settings=new(root);automatic=new(science,settings);automatic.Pause(true);plans=new(automatic,settings,store);}
    MissionPlan Save()=>store.SavePlan(new(Guid.NewGuid(),"Actual incomplete plan",Waypoints:[new("A",.001,-75.99),new("B",.001,-67.87)],Instruments:[new("terrain-scanner",true),new("thermal-imager",true)]));
    StartMissionRequest Request(MissionPlan p)=>new("Advised flight",PlanId:p.Id,ArmScience:true,ExpectedPlanRevision:ScientificComposites.Identity(p),CheckSelectedPlan:true);
    [Fact]public void FullInventoryIsReadOnlyUnknownBeforeAssessmentAndRevisionBoundAfterwards()
    {
        var p=Save();var before=plans.StartReadiness(p);Assert.Equal("NotAssessed",before.Status);Assert.Null(before.Preparation);Assert.Empty(automatic.Snapshot());
        plans.Prepare(p,true);var r=plans.StartReadiness(p);Assert.Equal("Incomplete",r.Status);Assert.Equal(408,r.Preparation!.Awaiting);Assert.All(r.Instruments!,i=>Assert.Equal(204,i.Total));Assert.InRange(automatic.Snapshot().Length,12,24);
        var changed=store.SavePlan(p with{Name="Changed"});Assert.Equal("Stale",plans.StartReadiness(changed).Status);Assert.Null(plans.StartReadiness(changed).Preparation);Assert.Equal("NotRequired",plans.StartReadiness(p with{Instruments=[]}).Status);
    }
    [Fact]public async Task IncompleteDataDoesNotBlockStartButConcurrentDuplicateStartsDoNotCreateTwoFlights()
    {
        var p=Save();plans.Prepare(p,true);var coordinator=new MissionCoordinator(store,1000);coordinator.SelectPlan(p.Id);int success=0;
        await Task.WhenAll(Enumerable.Range(0,2).Select(_=>Task.Run(()=>{try{coordinator.Start(Request(p));Interlocked.Increment(ref success);}catch(InvalidOperationException e){Assert.Equal("A mission is already recording.",e.Message);}})));
        Assert.Equal(1,success);Assert.Single(store.List(20));Assert.Equal(408,plans.StartReadiness(p).Preparation!.Awaiting);Assert.Equal(p.Id,coordinator.Snapshot().SelectedPlanId);
    }
    [Fact]public void ChangedPlanAndSelectionAreRejectedInsideTheExistingStartBoundary()
    {
        var p=Save();var c=new MissionCoordinator(store,1000);c.SelectPlan(p.Id);store.SavePlan(p with{Name="Edited"});Assert.Contains("saved mission plan changed",Assert.Throws<InvalidOperationException>(()=>c.Start(Request(p))).Message);
        c.SelectPlan(null);Assert.Contains("selected mission plan changed",Assert.Throws<InvalidOperationException>(()=>c.Start(Request(p))).Message);Assert.Empty(store.List(20));
    }
    [Fact]public void UnknownPreparationDoesNotBypassTelemetryOrMaintenanceRequirements()
    {
        var p=Save();var c=new MissionCoordinator(store,1000,requireFreshTelemetry:true);c.SelectPlan(p.Id);Assert.Contains("Live aircraft telemetry",Assert.Throws<InvalidOperationException>(()=>c.Start(Request(p))).Message);
        using(c.EnterMaintenance())Assert.Contains("maintenance",Assert.Throws<InvalidOperationException>(()=>c.Start(Request(p))).Message);Assert.Empty(store.List(20));
    }
    [Fact]public void LegacyWireRequestsOmitNewOptionalBrowserChecks()
    {
        var json=JsonSerializer.Serialize(new StartMissionRequest("Legacy"),WireJson.Options);Assert.DoesNotContain("expectedPlan",json);Assert.DoesNotContain("checkSelectedPlan",json);Assert.NotNull(JsonSerializer.Deserialize<StartMissionRequest>(json,WireJson.Options));
    }
    public void Dispose(){automatic.Dispose();science.Dispose();SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
}
