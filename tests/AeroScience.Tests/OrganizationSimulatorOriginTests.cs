using System.Net;
using System.Net.Http.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;

namespace AeroScience.Tests;

public sealed partial class OrganizationConnectionIntegrationTests
{
    [PostgresFact] public async Task SourceAwarePackagesAdvertiseCapabilityReceiveExactBytesAndRefuseDowngrading()
    {
        await using var fixture=await SubmissionFixture.Create();var card=await fixture.Card(incomplete:true);
        var origin=new SimulatorOrigin(1,"msfs2024",null,"aeroscience.simconnect","synthetic-fixture",null,Guid.NewGuid(),"fixture-map")
            {TimestampBasis=SimulatorProvenance.HostReceiptBasis,Limitations=["Declared simulator source in a synthetic protocol fixture; no genuine MSFS acceptance"]};
        var result=await fixture.Record(card.Prepared,origin:origin);var draft=fixture.Draft(result);
        Assert.Equal(3,draft.Manifest.Version);Assert.Null(draft.Consent);Assert.Equal(0,await fixture.Received());
        var capabilities=await fixture.Desktop.RefreshSubmissionPolicies(fixture.Upload,draft,fixture.Store,default);
        Assert.All(capabilities,c=>Assert.Equal(1,c.SimulatorOriginsVersion));
        var approved=await fixture.ReviewApprove(draft);var document=fixture.Store.ApprovedSubmissionDocument(approved.Id,fixture.Science);
        Assert.Equal(origin,document.Manifest.SimulatorOrigin);Assert.All(document.Evidence.SelectMany(i=>i.Data.Telemetry?.Samples??[]),s=>Assert.Equal(origin,s.Frame.SimulatorOrigin));
        var downgraded=document with{Manifest=document.Manifest with{Version=1,SimulatorOrigin=null}};
        Assert.Equal(HttpStatusCode.BadRequest,(await fixture.Packet(downgraded)).StatusCode);Assert.Equal(0,await fixture.Received());
        var response=await fixture.Packet(document);response.EnsureSuccessStatusCode();var receipt=(await response.Content.ReadFromJsonAsync<SubmissionReceipt>(WireJson.Options))!;
        Assert.Equal(PreflightReadiness.Hash(document),receipt.PayloadSha256);Assert.Equal(1,await fixture.Received());
        var repeat=await fixture.Packet(document);repeat.EnsureSuccessStatusCode();Assert.Equal(receipt,(await repeat.Content.ReadFromJsonAsync<SubmissionReceipt>(WireJson.Options))!);
        Assert.Equal(1,await fixture.Received());Assert.Equal(PreflightReadiness.Hash(result),PreflightReadiness.Hash(fixture.Store.ResearchAttempt(result.Id)));
    }
}
