using System.Text.Json;
using System.Text.Json.Nodes;
using AeroScience.Missions;
using AeroScience.Organization;
using AeroScience.Shared;
using Xunit;

namespace AeroScience.Tests;

public sealed class ResearchTemplateTests:IDisposable
{
    readonly string root=Path.Combine(Path.GetTempPath(),"AeroScience-P24-"+Guid.NewGuid());
    readonly MissionStore store;
    public ResearchTemplateTests(){Directory.CreateDirectory(root);store=new(Path.Combine(root,"missions.db"));store.Initialize();}
    public void Dispose()=>Directory.Delete(root,true);
    public static IEnumerable<object[]> Types()=>OrganizationTemplates.All().Select(t=>new object[]{t.Reference.Id});
    static PortableResearchTemplate Example(string kind)=>PortableResearchTemplates.Create("Shared experiment","A reusable definition, not evidence",OrganizationTemplates.All().Single(t=>t.Reference.Id==kind).Definition,"Pilot supplied name");
    static string Json(object? value)=>JsonSerializer.Serialize(value,WireJson.Options);
    [Theory,MemberData(nameof(Types))]
    public void AllSupportedTypesRoundTripWithoutHistoryOrAuthority(string kind)
    {
        var t=Example(kind);Assert.Equal(Json(t),Json(PortableResearchTemplates.Read(Json(t))));
        var card=store.ImportResearchTemplate(new(Json(t),"My copy"));var other=store.ImportResearchTemplate(new(Json(t),"Another copy"));
        Assert.NotEqual(card.Id,other.Id);Assert.Equal(1,card.Revision);Assert.Null(card.OrganizationAssignment);
        Assert.Equal(Json(t.Definition),Json(PortableResearchTemplates.Definition(card)));
        Assert.Equal(t.Attribution,card.TemplateProvenance!.Attribution);Assert.Empty(store.ResearchAttempts(card.Id));Assert.Empty(store.ResearchFlights(card.Id));
        var exported=store.ExportResearchTemplate(card.Id,new(1,"A reusable definition, not evidence"));Assert.Equal(t.Sha256,PortableResearchTemplates.Hash(exported with{Name=t.Name,Sha256=""}));
        foreach(var key in new[]{"organizationAssignment","claimId","connectionId","flightId","attempts","receipt","password","telemetry","sourceFiles"})Assert.DoesNotContain('"'+key+'"',Json(exported),StringComparison.OrdinalIgnoreCase);
        var reopened=new MissionStore(store.DatabasePath);reopened.Initialize();Assert.Equal(Json(card),Json(reopened.ResearchCard(card.Id)));
    }
    [Fact]public void CloneIsIndependentAndEditingPreservesAttributionAndFrozenCompoundCriteria()
    {
        var t=Example(FlightRegimeEvaluation.ProcedureTemplate);var original=store.ImportResearchTemplate(new(Json(t),"Original"));var clone=store.CloneResearchTemplate(original.Id,new(1,"Clone"));
        Assert.NotEqual(original.Id,clone.Id);Assert.Equal(Json(original.Procedure),Json(clone.Procedure));
        var edit=store.SaveResearchCard(new("Edited clone",null,clone.Id,1,Procedure:clone.Procedure! with{Ordered=false}));
        Assert.Equal(clone.TemplateProvenance,edit.TemplateProvenance);Assert.Equal(Json(original),Json(store.ResearchCard(original.Id,1)));Assert.True(store.ResearchCard(clone.Id,1).Procedure!.Ordered);
        Assert.False(edit.Procedure!.Ordered);Assert.Equal(2,edit.Procedure.Points[1].Repetitions);
    }
    [Theory]
    [InlineData("{\"format\":\"AeroScience.ResearchTemplate\",\"version\":1,\"version\":2}")]
    [InlineData("{\"script\":\"alert(1)\"}")]
    [InlineData("[]")]
    [InlineData("null")]
    public void MalformedOrExecutableStructuresFailClosed(string json)=>Assert.Throws<ArgumentException>(()=>PortableResearchTemplates.Read(json));
    [Theory]
    [InlineData("version","2")]
    [InlineData("futureCriteria","{\"logic\":\"arbitrary()\"}")]
    [InlineData("flightHistory","[]")]
    [InlineData("credentials","\"secret\"")]
    [InlineData("sha256","\"changed\"")]
    public void UnknownFieldsAndFutureVersionsAreRejectedWithoutModification(string key,string value)
    {var json=JsonNode.Parse(Json(Example("flight-regime")))!;json[key]=JsonNode.Parse(value);var original=json.ToJsonString();Assert.Throws<ArgumentException>(()=>PortableResearchTemplates.Read(original));Assert.Equal(original,json.ToJsonString());}
    [Fact]public void NestedUnknownCriteriaAndUnsupportedSignalsCannotBeSilentlyStripped()
    {
        var json=JsonNode.Parse(Json(Example(FlightRegimeEvaluation.ProcedureTemplate)))!;json["definition"]!["procedure"]!["points"]![0]!["conditions"]!["testPoint"]!["mach"]=.8;
        Assert.Throws<ArgumentException>(()=>PortableResearchTemplates.Read(json.ToJsonString()));
        json=JsonNode.Parse(Json(Example("vertical-atmospheric-profile")))!;json["definition"]!["atmosphericProfile"]!["newSource"]="secret";Assert.Throws<ArgumentException>(()=>PortableResearchTemplates.Read(json.ToJsonString()));
    }
    [Fact]public void BoundsNestingModelVersionsAndIntegrityAreEnforced()
    {
        Assert.Throws<ArgumentException>(()=>PortableResearchTemplates.Read(new string(' ',16385)));
        Assert.Throws<ArgumentException>(()=>PortableResearchTemplates.Read(new string('[',18)+new string(']',18)));
        var t=Example("experimental-sensor-checkout");Assert.Throws<ArgumentException>(()=>PortableResearchTemplates.Create("Bad","",t.Definition with{ExperimentalSensor=t.Definition.ExperimentalSensor! with{Gain=double.NaN}}));
        Assert.Throws<ArgumentException>(()=>PortableResearchTemplates.Create("Bad","",t.Definition with{ExperimentalSensor=t.Definition.ExperimentalSensor! with{Version=2}}));
        Assert.Throws<ArgumentException>(()=>PortableResearchTemplates.Read(Json(t with{Attribution="different"})));
        foreach(var text in new[]{"<script>alert(1)</script>","file:///private/source.tif",@"C:\private\secret.json","javascript:alert(1)","#!/bin/sh"})Assert.Throws<ArgumentException>(()=>PortableResearchTemplates.Create("Bad",text,t.Definition));
    }
    [Fact]public void MissingVersionOrCriteriaCannotBecomeExampleDefaults()
    {var json=JsonNode.Parse(Json(Example("vertical-atmospheric-profile")))!;json["definition"]!["atmosphericProfile"]!.AsObject().Remove("version");Assert.Throws<ArgumentException>(()=>PortableResearchTemplates.Read(json.ToJsonString()));}
    [Fact]public void PortableIntegrityAndCapabilityOrderingDoNotDependOnTheComputersLanguage()
    {
        var prior=System.Globalization.CultureInfo.CurrentCulture;
        try{var expected=OrganizationTemplates.All().Select(t=>PortableResearchTemplates.Create(t.Name,t.Description,t.Definition).Sha256).ToArray();foreach(var culture in new[]{"tr-TR","sv-SE","en-US"}){System.Globalization.CultureInfo.CurrentCulture=System.Globalization.CultureInfo.GetCultureInfo(culture);Assert.Equal(expected,OrganizationTemplates.All().Select(t=>PortableResearchTemplates.Create(t.Name,t.Description,t.Definition).Sha256));}}
        finally{System.Globalization.CultureInfo.CurrentCulture=prior;}
    }
    [Fact]public void ImportedSharingMetadataRemainsFrozenButDoesNotInflateNativePackets()
    {
        var t=PortableResearchTemplates.Create(new string('漢',100),new string('漢',1500),Example("flight-regime").Definition,new string('漢',300));var card=store.ImportResearchTemplate(new(Json(t),t.Name));var plan=store.CreateResearchPlan(card.Id,card.Revision);var coordinator=new MissionCoordinator(store,1000);var flight=coordinator.Start(new("Imported native card",PlanId:plan.Id));
        var encoded=new ToolbarCommandHandler(coordinator).Handle(Json(new ToolbarCommand(1,Guid.NewGuid(),"research-state",MissionId:flight.Id)));using var reply=JsonDocument.Parse(encoded);Assert.True(reply.RootElement.GetProperty("ok").GetBoolean(),encoded);Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(encoded),1,8000);Assert.DoesNotContain("templateProvenance",encoded);Assert.Equal(card.TemplateProvenance,Assert.Single(store.ResearchOperations(flight.Id).Cards).Card.TemplateProvenance);coordinator.End(flight.Id);
    }
}
