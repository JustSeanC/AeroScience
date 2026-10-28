using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;
namespace AeroScience.Tests;

public sealed partial class SubmissionStoreTests
{
    [Fact] public void ApprovedQuantitativeCoverageRetainsOriginalIdentitiesButNeverTurnsFlightPositionsIntoSensorLocations()
    {
        var q=Quantitative();Policy(q.Source);var doc=ReviewDocument(q.Result);var rule=SubmissionReviewEvidence.Requirements(doc).Single();var finding=new ReviewFinding(ResearchReviewState.Approved,Guid.NewGuid(),1,Guid.NewGuid(),"Reviewer",at,"",[],1);var samples=new List<ApprovedCoverageSample>();
        var interpretation=SubmissionReviewEvidence.DescribeApproved(doc,[doc],rule,finding,coverage:samples.Add);Assert.NotEmpty(samples);Assert.Equal(interpretation.Measurements.Sum(m=>m.Count),samples.Count);Assert.All(samples,s=>{Assert.Null(s.Latitude);Assert.Null(s.Longitude);Assert.Equal(CoverageEvidenceCategory.SyntheticFixture,s.Category);Assert.Equal(doc.Manifest.Id,s.Original.SubmissionId);Assert.NotNull(s.Original.Sequence);Assert.Equal(q.Result.FlightId,s.FlightId);});
        var repeated=new List<ApprovedCoverageSample>();SubmissionReviewEvidence.DescribeApproved(doc,[doc,doc],rule,finding,coverage:repeated.Add);Assert.Equal(samples,repeated);Assert.Equal(samples.Count,samples.Select(s=>s.IdentitySha256).Distinct().Count());Assert.Contains(samples,s=>s.Variable=="quantitative:ias"&&s.AltitudeFeetMsl is null);Assert.Contains(samples,s=>s.Variable=="quantitative:altitude-msl"&&s.AltitudeFeetMsl==3000);
        Assert.Throws<InvalidOperationException>(()=>SubmissionReviewEvidence.DescribeApproved(doc,[doc],rule,finding with{State=ResearchReviewState.AwaitingReview},coverage:_=>throw new Exception("Unapproved evidence must never be indexed")));
    }
}
