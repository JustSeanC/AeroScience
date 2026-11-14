using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;

namespace AeroScience.Tests;

[Collection(ScientificIoCollection.Name)]
public sealed class SubmissionSupportingTests
{
    sealed record LegacyCapabilities(Guid OrganizationId,Guid PilotId,SubmissionPolicy Policy,OrganizationDesktopPermission Permission);
    static ScientificArtifact Flight(TerrainSurveyFixture f)
    {f.Control(CollectionAction.StartData,0);f.Pose(0);f.Pose(1,100);f.Finish(2);return f.Build();}
    static async Task<(ScientificArtifact Product,ScientificSource Source)> Terrain(TerrainSurveyFixture f,ScientificArtifact geometry)
    {
        var source=await f.Science.ImportSourceAsync(RemoteFixtureFiles.Create(Path.Combine(f.Root,"synthetic-reference-dem"),RemoteKind.Terrain));
        var job=TerrainElevations.Create(f.Science,f.Missions,f.Id,new(geometry.Id,[new(source.Id,source.ManifestSha256,Surface:"dem",LocalDefinitionAcknowledged:true)],new(TileSize:4,MaximumScratchBytes:4194304)));
        f.Science.ControlTerrainJob(job.Id,new(Guid.NewGuid(),job.Revision,"start"));return(TerrainElevations.Process(f.Science,f.Missions,job.Id),source);
    }
    internal static SubmissionDocument Document(SubmissionSupportingProduct product)
    {
        var card=new PersonalResearchCard(1,Guid.NewGuid(),1,"SYNTHETIC supporting reference contract","terrain-temperature-survey",null,new(2026,1,1,12,0,0,TimeSpan.Zero),Survey:SurveyResearchDefinition.Default);
        var policy=SubmissionEvidenceProtocol.DefaultPolicy(Guid.NewGuid(),card.CreatedUtc) with{Mode=SubmissionPolicyMode.AllowIncompleteSubmission};
        var binding=new SubmissionCardBinding(Guid.NewGuid(),policy.OpportunityId,Guid.NewGuid(),1,new('a',64),card.Id,1,PreflightReadiness.Hash(card),card.CreatedUtc,card.CreatedUtc.AddSeconds(2),0);
        var definition=new SubmissionEvidenceData(Definition:card);var data=new SubmissionEvidenceData(SupportingProduct:product);
        SubmissionEvidenceReference Ref(string key,SubmissionEvidenceKind kind,SubmissionEvidenceData value,string original,string source,long records)=>new(key,kind,card.Id,Guid.Empty,product.FlightId,original,source,PreflightReadiness.Hash(value),SubmissionEvidenceProtocol.Bytes(value),records,binding.StartUtc,binding.EndUtc,"Synthetic contract reference","Reference-only fixture; not original numeric science.");
        SubmissionEvidenceItem[] items=[new(Ref("definition:"+card.Id,SubmissionEvidenceKind.Definition,definition,card.Id.ToString(),PreflightReadiness.Hash(card),1),definition),new(Ref($"supporting:{card.Id}:{product.Id}",SubmissionEvidenceKind.SupportingProductReference,data,product.Id.ToString(),product.Sha256,0),data)];
        return new(new(2,Guid.NewGuid(),"Explicit reference fixture","https://research.example",Guid.NewGuid(),"Synthetic organization",binding.CampaignId,"Synthetic campaign",Guid.NewGuid(),product.FlightId,[Guid.NewGuid()],[binding],[policy],items.Select(i=>i.Reference).ToArray(),SubmissionEvidenceProtocol.HistoryDisclosure,SubmissionEvidenceProtocol.PrivacyDisclosure),items);
    }
    static SubmissionDocument Replace(SubmissionDocument document,SubmissionSupportingProduct product,int version=2,long records=0)
    {
        var old=document.Evidence.Single(i=>i.Data.SupportingProduct is not null);var data=new SubmissionEvidenceData(SupportingProduct:product);var item=old with{Reference=old.Reference with{ContentSha256=PreflightReadiness.Hash(data),ContentBytes=SubmissionEvidenceProtocol.Bytes(data),Records=records},Data=data};
        var items=document.Evidence.Select(i=>i==old?item:i).ToArray();return document with{Manifest=document.Manifest with{Version=version,Items=items.Select(i=>i.Reference).ToArray()},Evidence=items};
    }
    [Fact] public async Task TerrainReferencePreservesExactOriginalAndDerivedHashesWithoutCopyingNumericEvidence()
    {
        using var f=new TerrainSurveyFixture();var g=Flight(f);var (a,s)=await Terrain(f,g);var before=PreflightReadiness.Hash(MissionEvaluationArchive.Build(f.Missions,f.Science,f.Id));
        var p=SubmissionSupportingProducts.Read(f.Science,f.Missions,f.Id,a.Id);var source=Assert.Single(p.Sources);
        Assert.True(p.ReferenceOnly);Assert.False(p.GrantsOriginalScienceCredit);Assert.Equal(a.Sha256,p.Sha256);Assert.Equal(g.Sha256,p.GeometrySha256);Assert.Equal(s.ManifestSha256,source.ManifestSha256);
        Assert.Equal(s.Files.Select(x=>x.Sha256),source.Originals.Select(x=>x.Sha256));Assert.Equal(s.Manifest.ObservationUtc,source.AcquisitionUtc);Assert.Equal(s.Manifest.VerticalDatum,source.VerticalReference);
        Assert.Equal(p.Tiles.Sum(t=>t.Values),p.ReferencedValues);Assert.NotEmpty(p.Tiles);
        var json=JsonSerializer.Serialize(p,WireJson.Options);Assert.DoesNotContain("relativeDirectory",json);Assert.DoesNotContain("relativePath",json);Assert.DoesNotContain("\"cells\"",json);Assert.DoesNotContain("\"points\"",json);
        Assert.Equal(before,PreflightReadiness.Hash(MissionEvaluationArchive.Build(f.Missions,f.Science,f.Id)));
    }
    [Fact] public async Task HistoricalPointReferenceKeepsUnknownDateHeightAndOriginalAttributesWithoutImplyingFlightReturns()
    {
        using var f=new TerrainSurveyFixture();var g=Flight(f);
        var source=await LidarImports.Import(f.Science,LidarFixture.Write(Path.Combine(f.Root,"synthetic-reference-las")),new("SYNTHETIC reference test","synthetic-project","Synthetic tests only","Not genuine source acceptance"));
        var job=LidarProcessing.Create(f.Science,f.Missions,f.Id,new(g.Id,[new(source.Id,source.ManifestSha256,true)],new(RecordsPerTile:2,MaximumScratchBytes:4194304),Guid.NewGuid()));f.Science.ControlLidarJob(job.Id,new(Guid.NewGuid(),job.Revision,"start"));var a=await LidarProcessing.Process(f.Science,f.Missions,job.Id);
        var p=SubmissionSupportingProducts.Read(f.Science,f.Missions,f.Id,a.Id);var s=Assert.Single(p.Sources);Assert.Null(s.AcquisitionUtc);Assert.False(s.ReferenceVerified);Assert.Contains("Unknown",s.VerticalReference,StringComparison.OrdinalIgnoreCase);Assert.Contains(s.Dimensions,d=>d.Contains("classification",StringComparison.OrdinalIgnoreCase));Assert.Equal(source.Files.Select(v=>v.Sha256),s.Originals.Select(v=>v.Sha256));Assert.Contains("not generated by the simulator",p.EvidenceClass);
    }
    [Fact] public async Task SourceExclusionDeletionAndAnotherFlightCannotBeSubstitutedDuringReferenceMaterialization()
    {
        using var f=new TerrainSurveyFixture();var g=Flight(f);var (a,s)=await Terrain(f,g);
        Assert.Throws<ArgumentException>(()=>SubmissionSupportingProducts.Read(f.Science,f.Missions,Guid.NewGuid(),a.Id));
        f.Missions.SupportingProductPreferences=()=>new(ExcludedSourceIds:[s.Id]);Assert.Throws<InvalidOperationException>(()=>SubmissionSupportingProducts.Read(f.Science,f.Missions,f.Id,a.Id));
        f.Missions.SupportingProductPreferences=null;File.Delete(Path.Combine(f.Science.DirectoryPath,s.RelativeDirectory,s.Files[0].RelativePath));
        Assert.ThrowsAny<IOException>(()=>SubmissionSupportingProducts.Read(f.Science,f.Missions,f.Id,a.Id));
    }
    [Fact] public void MetadataReferencesNeverSatisfyOriginalMinimumOrReviewerApprovalAndV1StaysExact()
    {
        using var f=new TerrainSurveyFixture();var g=Flight(f);var p=SubmissionSupportingProducts.Read(f.Science,f.Missions,f.Id,g.Id);var d=Document(p);
        SubmissionEvidenceProtocol.Validate(d);var a=SubmissionEvidenceProtocol.Assess(d);Assert.False(a.MinimumMet);Assert.False(a.RuleSupported);
        var card=d.Manifest.Cards.Single();var r=SubmissionReviewEvidence.Requirements(d).Single();Assert.False(SubmissionReviewEvidence.Assess(d,[d],card.CardId,r.Key).CanApprove);
        Assert.All(d.Evidence.Where(i=>i.Data.SupportingProduct is not null),i=>Assert.Equal(0,i.Reference.Records));
        Assert.Throws<ArgumentException>(()=>SubmissionEvidenceProtocol.Validate(Replace(d,p,version:1)));
        Assert.Equal("{}",JsonSerializer.Serialize(new SubmissionEvidenceData(),WireJson.Options));
        var capability=new SubmissionCapabilities(Guid.Empty,Guid.Empty,d.Manifest.Policies[0],OrganizationDesktopPermission.ReadPublished);Assert.DoesNotContain("supportingProductsVersion",JsonSerializer.Serialize(capability,WireJson.Options));
        var advertised=capability with{SupportingProductsVersion=1};var oldClient=JsonSerializer.Deserialize<LegacyCapabilities>(JsonSerializer.Serialize(advertised,WireJson.Options),WireJson.Options)!;
        Assert.Equal(capability.OrganizationId,oldClient.OrganizationId);Assert.Equal(capability.PilotId,oldClient.PilotId);Assert.Equal(capability.Permission,oldClient.Permission);Assert.Equal(PreflightReadiness.Hash(capability.Policy),PreflightReadiness.Hash(oldClient.Policy));
    }
    [Fact] public async Task RehashedMetadataCannotPromoteUnknownHeightForgeSourceKindsOrGrantOriginalScience()
    {
        using var f=new TerrainSurveyFixture();var g=Flight(f);var (a,_)=await Terrain(f,g);var p=SubmissionSupportingProducts.Read(f.Science,f.Missions,f.Id,a.Id);var d=Document(p);
        foreach(var bad in new[]{p with{GrantsOriginalScienceCredit=true},p with{ReferenceOnly=false},p with{FlightId=Guid.NewGuid()},p with{Algorithm="unsupported-method"},p with{ReferencedValues=p.ReferencedValues+1},p with{Sources=p.Sources.Select(s=>s with{VerticalReference="Unknown",ReferenceVerified=true}).ToArray()},p with{Sources=p.Sources.Select(s=>s with{Surface="historical-lidar"}).ToArray()}})
            Assert.ThrowsAny<ArgumentException>(()=>SubmissionEvidenceProtocol.Validate(Replace(d,bad)));
        Assert.Throws<ArgumentException>(()=>SubmissionEvidenceProtocol.Validate(Replace(d,p,records:1)));
    }
}
