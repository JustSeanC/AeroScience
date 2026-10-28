using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;
namespace AeroScience.Tests;

public sealed class CampaignCoverageTests
{
    static CoverageGoalDefinition Definition()=>new(1,"Temperature sampling","How does temperature change with altitude?",Guid.NewGuid(),2,new('a',64),"science:ambient-temperature:temperature","ambient-temperature","degC",CoverageEvidenceCategory.SimulatorReported,Altitude:new(0,10000,2000),MinimumPilots:2);
    [Fact] public void MslBandsAndSamplingCellsHaveDeterministicBoundariesWithoutInterpolation()
    {
        var a=Definition().Altitude!;Assert.Null(CampaignCoverageRules.Band(a,null));Assert.Null(CampaignCoverageRules.Band(a,double.NaN));Assert.Null(CampaignCoverageRules.Band(a,-1));Assert.Null(CampaignCoverageRules.Band(a,10001));Assert.Equal(0,CampaignCoverageRules.Band(a,1999));Assert.Equal(1,CampaignCoverageRules.Band(a,2000));Assert.Equal(4,CampaignCoverageRules.Band(a,10000));
        var grid=new CoverageSamplingGrid(39,-76,41,-74,2,2);Assert.Equal((0,0),CampaignCoverageRules.Cell(grid,39,-76));Assert.Equal((1,1),CampaignCoverageRules.Cell(grid,40,-75));Assert.Equal((1,1),CampaignCoverageRules.Cell(grid,41,-74));Assert.Null(CampaignCoverageRules.Cell(grid,42,-75));Assert.Null(CampaignCoverageRules.Cell(grid,null,-75));
    }
    [Fact] public void GoalsRequireExactMeaningUnitsReferencesAndBoundedTargets()
    {
        var d=Definition();CampaignCoverageRules.Validate(d);foreach(var invalid in new[]{d with{Unit="kelvin"},d with{PublicationSha256="same-looking-name"},d with{Category=CoverageEvidenceCategory.Unknown},d with{MinimumPilots=0},d with{Altitude=new(0,10000,10)},d with{Grid=new(0,170,1,-170,1,1)},d with{Grid=new(-90,0,1,1,1,1)},d with{Grid=new(0,0,1,1,16,16)},d with{FromUtc=DateTimeOffset.UtcNow,ThroughUtc=DateTimeOffset.UtcNow.AddDays(-1)}})Assert.Throws<ArgumentException>(()=>CampaignCoverageRules.Validate(invalid));
        Assert.False(CampaignCoverageRules.Satisfied(new(100,10,10,1),d));Assert.True(CampaignCoverageRules.Satisfied(new(2,2,1,2),d));Assert.DoesNotContain("percentage",string.Join(" ",CampaignCoverageRules.Methods).Replace("No area percentage",""));
    }
}
