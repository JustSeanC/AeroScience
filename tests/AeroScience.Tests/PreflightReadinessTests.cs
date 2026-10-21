using AeroScience.Shared;
using AeroScience.Missions;
using Xunit;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
namespace AeroScience.Tests;
public sealed class PreflightReadinessTests
{
    internal static readonly DateTimeOffset At=DateTimeOffset.Parse("2026-10-21T01:00:00Z");
    internal static ReadinessEnvironment Environment(bool online=false)=>new([],new(OnlineEnabled:online,PrivacyAccepted:online),true,true,1000000000,1000000000,1000000000,1000000000,[]);
    internal static MissionPlan Plan()=>new(Guid.NewGuid(),"Labeled synthetic survey",MissionTemplateKind.GeographicSurvey,ScienceMode.Realistic,Objective:"Compare retained source temperatures",Waypoints:[new("A",40,-75,Id:Guid.NewGuid()),new("B",40.01,-75,Id:Guid.NewGuid())],SurveyAreas:[new("A",[new(40,-75),new(40.01,-75),new(40.01,-74.99),new(40,-74.99)])],Instruments:[new("thermal-imager",true)],MinimumAltitudeFeet:3000,MaximumAltitudeFeet:4000);
    [Fact] public void ActualInputsYieldDifferentFindingsWithoutChangingDraft()
    {
        var p=Plan();var hash=PreflightReadiness.Hash(p);var r=PreflightReadiness.Evaluate(p,Environment(),At);
        Assert.Contains(r.Checks,c=>c.Id=="design.survey"&&c.Status==ReadinessStatus.Ready);
        Assert.Contains(r.Checks,c=>c.Id.StartsWith("source.")&&c.Status==ReadinessStatus.Unavailable);
        Assert.Contains(r.Checks,c=>c.Id=="instruments.geometry"&&c.Status==ReadinessStatus.NotYetAssessed);
        Assert.Equal(hash,PreflightReadiness.Hash(p));Assert.All(r.Checks,c=>Assert.NotEmpty(c.Reasons));
        var partial=PreflightReadiness.Evaluate(p with{Objective=null,SurveyAreas=[],MinimumAltitudeFeet=5000},Environment(true),At);
        Assert.Contains(partial.Checks,c=>c.Id=="design.objective"&&c.Status==ReadinessStatus.Caution);
        Assert.Contains(partial.Checks,c=>c.Id=="design.validation"&&c.Status==ReadinessStatus.Caution);
        Assert.Contains(partial.Checks,c=>c.Id.StartsWith("source.")&&c.Status==ReadinessStatus.NotYetAssessed);
        Assert.NotEqual(r.PlanSha256,partial.PlanSha256);Assert.NotEqual(r.ContextSha256,partial.ContextSha256);
    }
    [Fact] public void NonGeographicProfileDoesNotRequirePolygonAndCapacityChangesExpireContext()
    {
        var p=Plan() with{Template=MissionTemplateKind.AtmosphericProfile,SurveyAreas=null,Instruments=[new("ambient-wind",true)],AtmosphericSettings=new()};
        var r=PreflightReadiness.Evaluate(p,Environment(),At);
        Assert.Contains(r.Checks,c=>c.Id=="design.survey"&&c.Status==ReadinessStatus.Ready);
        Assert.DoesNotContain(r.Checks,c=>c.Id.StartsWith("source."));
        var changed=PreflightReadiness.Evaluate(p,Environment() with{ScienceStorageAvailable=false},At);
        Assert.NotEqual(r.ContextSha256,changed.ContextSha256);Assert.Equal(At.AddSeconds(60),r.ExpiresUtc);
        Assert.Contains(changed.Checks,c=>c.Id=="storage.science"&&c.Status==ReadinessStatus.Unavailable);
        Assert.Equal(r.PlanSha256,changed.PlanSha256);
    }
    [Fact] public void BrokenAutomationAndMissingSessionStartAreReportedNotSavedOrInferred()
    {
        var p=Plan();var trigger=new CollectionTrigger(Guid.NewGuid(),Guid.NewGuid(),CollectionAction.EndData,CollectionTriggerKind.Waypoint,Guid.NewGuid(),1,20,PlannedSessionKey:"B");
        p=p with{Collection=new(1,p.Id,InitialCollectionPolicy.Armed,[trigger],new(5,6),SurveyAreas:[])};
        var r=PreflightReadiness.Evaluate(p,Environment(),At);
        Assert.Contains(r.Checks,c=>c.Id=="automation.references"&&c.Status==ReadinessStatus.Unavailable);
        Assert.Contains(r.Checks,c=>c.Id.StartsWith("automation.sequence."));Assert.Contains(r.Checks,c=>c.Id=="automation.sessions"&&c.Reasons[0].Contains("never imply End"));
    }
    [Fact] public async Task DraftApiUsesRealServicesAndNeverPersistsOrStartsWork()
    {
        var root=Path.Combine(Path.GetTempPath(),"aeroscience-preflight-"+Guid.NewGuid());
        try
        {
            await using(var factory=new WebApplicationFactory<Program>().WithWebHostBuilder(b=>{b.UseEnvironment("Testing");b.ConfigureAppConfiguration((_,c)=>c.AddInMemoryCollection(new Dictionary<string,string?>{["AeroScience:DataDirectory"]=root,["AeroScience:EnableToolbarBridge"]="false"}));}))
            {
                using var client=factory.CreateClient(new(){BaseAddress=new("http://localhost:5080")});client.DefaultRequestHeaders.Add("Origin","http://localhost:5080");
                var session=await client.PostAsJsonAsync("/api/session",new{});var csrf=(await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("csrf").GetString();client.DefaultRequestHeaders.Add("X-AeroScience-CSRF",csrf);
                var response=await client.PostAsJsonAsync("/api/plans/readiness",Plan(),WireJson.Options);response.EnsureSuccessStatusCode();
                var result=await response.Content.ReadFromJsonAsync<ReadinessAssessment>(WireJson.Options);Assert.Contains(result!.Checks,c=>c.Category=="Scientific sources");
                Assert.Empty((await client.GetFromJsonAsync<MissionPlan[]>("/api/plans",WireJson.Options))!);
                var state=await client.GetFromJsonAsync<EngineSnapshot>("/api/state",WireJson.Options);Assert.Null(state!.ActiveMission);
                Assert.Empty((await client.GetFromJsonAsync<ScientificDataReadiness[]>("/api/science/readiness",WireJson.Options))!);
            }
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
