using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Organization;
using AeroScience.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace AeroScience.Tests;

// Real isolated PostgreSQL + Kestrel + desktop services. Recorded readings are explicitly synthetic fixtures.
public sealed partial class OrganizationConnectionIntegrationTests
{
    sealed class SubmissionFixture:IAsyncDisposable
    {
        public string Root="",Database="";public Server Server=null!;public Vault Vault=new();public OrganizationConnections Desktop=null!;public MissionStore Store=null!;public ScienceStore Science=null!;public Guid Read,Upload;
        public static async Task<SubmissionFixture> Create(bool protectedIdentity=false)
        {
            var f=new SubmissionFixture{Root=Path.Combine(Path.GetTempPath(),"org-submission-"+Guid.NewGuid().ToString("N")),Database="aeroscience_p17_"+Guid.NewGuid().ToString("N")};Directory.CreateDirectory(f.Root);
            try{if(protectedIdentity){using var key=System.Security.Cryptography.RSA.Create(2048);var request=new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=P26 Synthetic Recovery Fixture",key,System.Security.Cryptography.HashAlgorithmName.SHA256,System.Security.Cryptography.RSASignaturePadding.Pkcs1);using var certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddDays(1));var path=Path.Combine(f.Root,"recovery-identity.pfx");File.WriteAllBytes(path,certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx,"P26-Only!Identity-Certificate-Key"));OrganizationConfigurationTests.ProtectCredential(path);}f.Server=await Start(f.Database,f.Root,"Evidence Research Lab");f.Desktop=new(new EngineOptions{ConfigurationDirectory=f.Root,OrganizationLoopbackDevelopment=true},new Environment(),f.Vault);f.Read=await Connect(f.Desktop,f.Server);f.Upload=await Connect(f.Desktop,f.Server,OrganizationDesktopPermission.SubmitEvidence);var paths=new StorageManager(new PreferenceStore(f.Root)).Initialize();f.Store=new(Path.Combine(paths.MissionDirectory,"missions.db"));f.Store.Initialize();f.Science=new(paths.ScienceLibraryDirectory);f.Science.Initialize();f.Store.ScienceLibrary=f.Science;return f;}
            catch{await f.DisposeAsync();throw;}
        }
        public async Task<(OrganizationOpportunitySnapshot Published,OrganizationClaimView Claim,PreparedOrganizationMission Prepared)> Card(string template="custom-flight-test",bool incomplete=false)
        {
            var card=await RealOpportunity(Server,template);if(incomplete)await Server.Post($"/api/organization/submissions/policies/{card.Id}",new SubmissionPolicyCommand(1,Guid.NewGuid(),1,SubmissionPolicyMode.AllowIncompleteSubmission));var claim=await BrowserClaim(Server,card);var preview=await Desktop.PreviewMission(Read,claim.Id,new(),Store,default);var saved=await Desktop.PrepareMission(Read,claim.Id,MissionSave(preview),Store,default);return(card,claim,saved);
        }
        public static TelemetryFrame Frame(DateTimeOffset at,double speed=120)=>new(at,TelemetrySource.Simulated,"SYNTHETIC evidence fixture, no MSFS",40,-75,3000,null,speed,speed,speed,0,0,0,0,0);
        public async Task<PersonalResearchAttempt> Record(PreparedOrganizationMission p,int seconds=2,bool notes=false,bool fail=false)
        {
            // Wait for the real server's authoritative claim interval; never backdate a claim to authorize evidence.
            var start=DateTimeOffset.UtcNow;await Task.Delay(TimeSpan.FromSeconds(seconds));var flight=Store.Create("Synthetic retained submission flight",Frame(start),start,plan:p.Plan);
            for(var i=0;i<=seconds;i++)Store.Append(flight,Frame(start.AddSeconds(i),fail?150:120));if(notes)for(var i=0;i<3;i++)Store.AddMarker(flight,new("observation","Selected original pilot note "+i),start.AddSeconds(i*seconds/3d),Frame(start.AddSeconds(i*seconds/3d)),"PRIVATE-CONTEXT-NOT-SHARED");
            Store.Finish(flight,MissionStatus.Completed,start.AddSeconds(seconds),"complete");return Store.EvaluateResearchAttempt(new(Guid.NewGuid(),flight,start,start.AddSeconds(seconds),CardId:p.Card.Id),science:Science);
        }
        public SubmissionDraft Draft(PersonalResearchAttempt result,string[]? keys=null)=>Store.SaveSubmissionDraft(new(1,Guid.NewGuid(),0,result.FlightId,[result.Id],keys,"Explicit selected package",Upload),Science);
        public async Task<SubmissionDraft> ReviewApprove(SubmissionDraft row)
        {
            await Desktop.RefreshSubmissionPolicies(Upload,row,Store,default);row=Store.SaveSubmissionDraft(MissionStore.SubmissionRequest(row),Science);var p=Store.PreviewSavedSubmission(row.Id,Science);return await Desktop.ApproveSubmission(row.Id,new(1,row.Revision,p.ManifestSha256,p.PayloadSha256,p.PayloadBytes,Upload,true),Store,Science,default);
        }
        public async Task Restart(){var origin=Server.Origin;await Server.DisposeAsync();Server=await Start(Database,Root,"Evidence Research Lab",existing:true,retainedOrigin:origin);}
        public SubmissionDelivery Delivery()=>new(Store,Science,Desktop,new MissionCoordinator(Store,1000));
        public async Task<HttpResponseMessage> Packet(SubmissionDocument doc,SubmissionConsent? consent=null,Guid? connection=null,string? path=null)
        {
            using var client=new HttpClient{BaseAddress=new(Server.Origin)};client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",Vault.Read(connection??Upload));var actual=consent??new(1,connection??Upload,PreflightReadiness.Hash(doc.Manifest),PreflightReadiness.Hash(doc),SubmissionEvidenceProtocol.Bytes(doc),DateTimeOffset.UtcNow,true);return await client.PostAsJsonAsync(path??OrganizationSubmissionApi.ReceivePath,new SubmissionUpload(doc,actual),WireJson.Options);
        }
        public async Task<int> Received(){using var scope=Server.App.Services.CreateScope();return await scope.ServiceProvider.GetRequiredService<OrganizationDatabase>().Submissions.CountAsync();}
        public async ValueTask DisposeAsync(){Science?.Dispose();Desktop?.Dispose();if(Server is not null)await Server.DisposeAsync();if(Directory.Exists(Root))await DropMissionFixture(Database,Root);}
    }
    [PostgresFact]public async Task ActualQuantitativeMeasuredFailureReceivesOneDurableReceiptWithoutBecomingScientificApproval()
    {
        await using var f=await SubmissionFixture.Create();var c=await f.Card();var result=await f.Record(c.Prepared,31,fail:true);Assert.Equal(ResearchAttemptOutcome.Fail,result.Outcome);var row=f.Draft(result);Assert.Equal(0,await f.Received());var approved=await f.ReviewApprove(row);var original=f.Store.ApprovedSubmissionDocument(row.Id,f.Science);using var delivery=f.Delivery();var received=await delivery.Send(row.Id,default);Assert.Equal(SubmissionDeliveryState.ServerReceived,received.State);Assert.Equal("Received / Awaiting Review",received.Receipt!.Status);Assert.Equal(1,await f.Received());
        // A lost acknowledgment can retry the exact identity; the organization returns the original receipt.
        using var duplicate=await f.Packet(original,approved.Consent);Assert.Equal(HttpStatusCode.OK,duplicate.StatusCode);var receipt=(await duplicate.Content.ReadFromJsonAsync<SubmissionReceipt>(WireJson.Options))!;Assert.Equal(received.Receipt,receipt);Assert.Equal(1,await f.Received());
        await f.Restart();var remote=await f.Desktop.SubmissionStatus(approved,default);Assert.Equal(receipt,remote.Receipt);using var scope=f.Server.App.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();var retained=await db.Submissions.SingleAsync();Assert.Equal(PreflightReadiness.Hash(original),retained.PayloadSha256);Assert.Equal(retained.PayloadBytes,(await db.SubmissionUsage.SingleAsync()).PayloadBytes);Assert.Equal(ResearchAttemptOutcome.Fail,f.Store.ResearchAttempt(result.Id).Outcome);
    }
    [PostgresFact]public async Task OriginalPilotDocumentationIsSelectedAndReceivedWhilePrivateContextAndUnapprovedDraftStayLocal()
    {
        await using var f=await SubmissionFixture.Create();var c=await f.Card(ObservationResearch.Template);var result=await f.Record(c.Prepared,2,notes:true);Assert.Equal(ResearchAttemptOutcome.CollectionComplete,result.Outcome);var row=f.Draft(result);using var delivery=f.Delivery();Assert.Equal(SubmissionDeliveryState.Draft,(await delivery.Send(row.Id,default)).State);Assert.Equal(0,await f.Received());var approved=await f.ReviewApprove(row);var document=f.Store.ApprovedSubmissionDocument(row.Id,f.Science);Assert.Equal(3,document.Evidence.Sum(i=>i.Data.Documentation?.Length??0));Assert.DoesNotContain("PRIVATE-CONTEXT-NOT-SHARED",JsonSerializer.Serialize(document,WireJson.Options));
        var received=await delivery.Send(row.Id,default);Assert.Equal(SubmissionDeliveryState.ServerReceived,received.State);using var scope=f.Server.App.Services.CreateScope();var retained=await scope.ServiceProvider.GetRequiredService<OrganizationDatabase>().Submissions.SingleAsync();Assert.Contains("Selected original pilot note",retained.PayloadJson);Assert.DoesNotContain("PRIVATE-CONTEXT-NOT-SHARED",retained.PayloadJson);Assert.DoesNotContain(f.Vault.Read(f.Upload)!,retained.PayloadJson);Assert.DoesNotContain("Selected original pilot note",JsonSerializer.Serialize(f.Store.SubmissionDraft(row.Id),WireJson.Options));
    }
    [PostgresFact]public async Task ActualPublishedPoliciesAndSeparateBrowserScopeRejectReadOnlyAndChangedConsent()
    {
        await using var f=await SubmissionFixture.Create();var c=await f.Card();var result=await f.Record(c.Prepared);var row=f.Draft(result);await f.Desktop.RefreshSubmissionPolicies(f.Read,row,f.Store,default);row=f.Store.SaveSubmissionDraft(MissionStore.SubmissionRequest(row),f.Science);var p=f.Store.PreviewSavedSubmission(row.Id,f.Science);Assert.False(p.CanApprove);
        await Assert.ThrowsAsync<OrganizationSubmissionAuthorizationRequired>(()=>f.Desktop.ApproveSubmission(row.Id,new(1,row.Revision,p.ManifestSha256,p.PayloadSha256,p.PayloadBytes,f.Read,true),f.Store,f.Science,default));
        await f.Server.Post($"/api/organization/submissions/policies/{c.Published.Id}",new SubmissionPolicyCommand(1,Guid.NewGuid(),1,SubmissionPolicyMode.AllowIncompleteSubmission));var approved=await f.ReviewApprove(row);var doc=f.Store.ApprovedSubmissionDocument(row.Id,f.Science);Assert.False(SubmissionEvidenceProtocol.Assess(doc).MinimumMet);
        using(var noScope=await f.Packet(doc,approved.Consent,connection:f.Read))Assert.Equal(HttpStatusCode.Forbidden,noScope.StatusCode);
        using(var changed=await f.Packet(doc,approved.Consent! with{PayloadBytes=1}))Assert.Equal(HttpStatusCode.Forbidden,changed.StatusCode);
        using(var noConsent=await f.Packet(doc,approved.Consent! with{ExplicitApproval=false}))Assert.Equal(HttpStatusCode.Forbidden,noConsent.StatusCode);
        Assert.Equal(0,await f.Received());using var delivery=f.Delivery();Assert.Equal(SubmissionDeliveryState.ServerReceived,(await delivery.Send(row.Id,default)).State);
    }
    [PostgresFact]public async Task OfflineApprovedQueueRetainsNoBytesAndDeletedOriginalsHoldInsteadOfBeingRegenerated()
    {
        await using var f=await SubmissionFixture.Create();var c=await f.Card(ObservationResearch.Template);var result=await f.Record(c.Prepared,2,notes:true);var row=f.Draft(result);await f.Desktop.RefreshSubmissionPolicies(f.Upload,row,f.Store,default);row=f.Store.SaveSubmissionDraft(MissionStore.SubmissionRequest(row),f.Science);await f.Server.App.StopAsync();var p=f.Store.PreviewSavedSubmission(row.Id,f.Science);row=await f.Desktop.ApproveSubmission(row.Id,new(1,row.Revision,p.ManifestSha256,p.PayloadSha256,p.PayloadBytes,f.Upload,true),f.Store,f.Science,default);using var delivery=f.Delivery();var retry=await delivery.Send(row.Id,default);Assert.Equal(SubmissionDeliveryState.RetryAvailable,retry.State);Assert.DoesNotContain("Selected original pilot note",JsonSerializer.Serialize(retry,WireJson.Options));
        var deletion=f.Science.PreviewCollectedDeletion(new([result.FlightId]),f.Store);f.Science.DeleteCollectedScience(new(deletion.Id,deletion.SelectionHash),f.Store,new MissionCoordinator(f.Store,1000).EnterMaintenance);await f.Restart();var held=await delivery.Send(row.Id,default);Assert.Equal(SubmissionDeliveryState.HeldEvidenceMissing,held.State);Assert.Equal(0,await f.Received());Assert.Equal(3,f.Store.StreamSamples(result.FlightId).Count());Assert.Null(held.Receipt);
    }
    [PostgresFact]public async Task CancellationTombstoneWinsBeforeReceiptAndRevokedScopeNeverUploads()
    {
        await using var f=await SubmissionFixture.Create();var c=await f.Card(incomplete:true);var result=await f.Record(c.Prepared);var row=await f.ReviewApprove(f.Draft(result));var doc=f.Store.ApprovedSubmissionDocument(row.Id,f.Science);using var delivery=f.Delivery();var cancelled=await delivery.Cancel(row.Id,row.Revision,default);Assert.Equal(SubmissionDeliveryState.Cancelled,cancelled.State);using(var late=await f.Packet(doc,row.Consent))Assert.Equal(HttpStatusCode.Conflict,late.StatusCode);Assert.Equal(0,await f.Received());
        var revised=f.Store.ReopenSubmission(row.Id,cancelled.Revision);Assert.NotEqual(row.Id,revised.Id);var fresh=await f.ReviewApprove(revised);await f.Desktop.Disconnect(f.Upload,default);var held=await delivery.Send(fresh.Id,default);Assert.Equal(SubmissionDeliveryState.HeldAuthorization,held.State);Assert.Equal(0,await f.Received());
    }
    [PostgresFact]public async Task ReleasedClaimAllowsLegitimateEarlierEvidenceButRejectsNewlyRecordedWork()
    {
        await using var f=await SubmissionFixture.Create();var c=await f.Card(incomplete:true);var prior=await f.Record(c.Prepared);var first=await f.ReviewApprove(f.Draft(prior));await f.Server.Post("/api/membership/research/claim-command",new OrganizationClaimCommand(1,Guid.NewGuid(),c.Published.OrganizationId,c.Published.Id,c.Claim.IssuedRevision,OrganizationClaimAction.Release,c.Claim.Id,c.Claim.Revision));
        var later=await f.Record(c.Prepared);var second=await f.ReviewApprove(f.Draft(later));using var delivery=f.Delivery();Assert.Equal(SubmissionDeliveryState.ServerReceived,(await delivery.Send(first.Id,default)).State);Assert.Equal(SubmissionDeliveryState.HeldAuthorization,(await delivery.Send(second.Id,default)).State);Assert.Equal(1,await f.Received());Assert.NotNull(f.Store.ResearchAttempt(later.Id));
    }
    [PostgresFact]public async Task ProtectedReceiverRejectsMalformedOversizeAndBrowserOriginsWithoutTouchingGlobalRequestLimits()
    {
        await using var f=await SubmissionFixture.Create();using var client=new HttpClient{BaseAddress=new(f.Server.Origin)};client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",f.Vault.Read(f.Upload));
        foreach(var json in new[]{"{}","{\"document\":null,\"consent\":null}","{\"document\":{\"manifest\":null,\"evidence\":null},\"consent\":null}"}){using var response=await client.PostAsync(OrganizationSubmissionApi.ReceivePath,new StringContent(json,System.Text.Encoding.UTF8,"application/json"));Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);}
        client.DefaultRequestHeaders.ExpectContinue=true;using(var response=await client.PostAsync(OrganizationSubmissionApi.ReceivePath,new StringContent(new string('x',SubmissionEvidenceProtocol.MaximumWireBytes+1),System.Text.Encoding.UTF8,"application/json")))Assert.Equal(HttpStatusCode.RequestEntityTooLarge,response.StatusCode);
        client.DefaultRequestHeaders.ExpectContinue=false;client.DefaultRequestHeaders.Add("Origin",f.Server.Origin);using(var response=await client.PostAsync(OrganizationSubmissionApi.ReceivePath,new StringContent("{}",System.Text.Encoding.UTF8,"application/json")))Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);Assert.Equal(0,await f.Received());
    }
    [PostgresFact]public async Task LostAcknowledgmentRecoversOriginalReceiptAfterRestartEvenWhenLocalEvidenceIsDeleted()
    {
        await using var f=await SubmissionFixture.Create();var c=await f.Card(incomplete:true);var result=await f.Record(c.Prepared);var approved=await f.ReviewApprove(f.Draft(result));using var delivery=f.Delivery();f.Server.DropNextSubmissionAcknowledgment=true;var retry=await delivery.Send(approved.Id,default);Assert.Equal(SubmissionDeliveryState.RetryAvailable,retry.State);Assert.Equal(1,await f.Received());Assert.Equal(1,f.Server.SubmissionWrites);
        var review=f.Science.PreviewCollectedDeletion(new([result.FlightId]),f.Store);f.Science.DeleteCollectedScience(new(review.Id,review.SelectionHash),f.Store,new MissionCoordinator(f.Store,1000).EnterMaintenance);await f.Restart();var receipt=await delivery.Send(approved.Id,default);Assert.Equal(SubmissionDeliveryState.ServerReceived,receipt.State);Assert.Equal(0,f.Server.SubmissionWrites);Assert.Equal(1,await f.Received());Assert.NotNull(receipt.Receipt);
    }
    [PostgresFact]public async Task InterruptedTransferRetriesOnlyUnchangedApprovalAndChangedSourceIsHeld()
    {
        await using var f=await SubmissionFixture.Create();var c=await f.Card(incomplete:true);var result=await f.Record(c.Prepared);var approved=await f.ReviewApprove(f.Draft(result));using var delivery=f.Delivery();f.Server.InterruptNextSubmission=true;var retry=await delivery.Send(approved.Id,default);Assert.Equal(SubmissionDeliveryState.RetryAvailable,retry.State);Assert.Equal(0,await f.Received());
        using(var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+f.Store.DatabasePath)){db.Open();using var q=db.CreateCommand();q.CommandText="UPDATE telemetry SET payload_json=json_set(payload_json,'$.indicatedAirspeedKnots',999) WHERE mission_id=$id";q.Parameters.AddWithValue("$id",result.FlightId.ToString());q.ExecuteNonQuery();}
        Assert.Equal(SubmissionDeliveryState.HeldEvidenceMissing,(await delivery.Send(approved.Id,default)).State);Assert.Equal(1,f.Server.SubmissionWrites);Assert.Equal(0,await f.Received());
    }
    [PostgresFact]public async Task GroupedFlightKeepsDistinctClaimDefinitionsAndIncompleteResultsInOneExplicitPackage()
    {
        await using var f=await SubmissionFixture.Create();var first=await f.Card(incomplete:true);var issued=first.Published;
        var draft=await f.Server.Post($"/api/organization/opportunities/campaigns/{issued.CampaignId}",new OrganizationOpportunityRequest(1,Guid.NewGuid(),0,issued.CampaignRevision,"Second independent card","","Selected original evidence","Each claim keeps its identity",issued.Policy));var second=(await f.Server.Post($"/api/organization/opportunities/{draft.GetProperty("id").GetGuid()}/publish",new OrganizationPublishRequest(1,Guid.NewGuid(),1))).Deserialize<OrganizationOpportunitySnapshot>(WireJson.Options)!;await f.Server.Post($"/api/organization/submissions/policies/{second.Id}",new SubmissionPolicyCommand(1,Guid.NewGuid(),1,SubmissionPolicyMode.AllowIncompleteSubmission));var claim=await BrowserClaim(f.Server,second);var preview=await f.Desktop.PreviewMission(f.Read,claim.Id,new(first.Prepared.Plan.Id),f.Store,default);var together=await f.Desktop.PrepareMission(f.Read,claim.Id,MissionSave(preview),f.Store,default);var one=await f.Record(together);var two=f.Store.EvaluateResearchAttempt(new(Guid.NewGuid(),one.FlightId,one.StartUtc,one.EndUtc,CardId:first.Prepared.Card.Id));
        var row=f.Store.SaveSubmissionDraft(new(1,Guid.NewGuid(),0,one.FlightId,[one.Id,two.Id],null,"Grouped selected flight",f.Upload),f.Science);row=await f.ReviewApprove(row);var doc=f.Store.ApprovedSubmissionDocument(row.Id,f.Science);Assert.Equal(2,doc.Manifest.Cards.Length);Assert.Equal(2,doc.Evidence.Count(i=>i.Data.Result is not null));Assert.Equal(2,doc.Manifest.Cards.Select(b=>b.ClaimId).Distinct().Count());Assert.All(doc.Evidence.Where(i=>i.Data.Result is not null),i=>Assert.Equal(ResearchAttemptOutcome.InsufficientEvidence,i.Data.Result!.Outcome));using var delivery=f.Delivery();Assert.Equal(SubmissionDeliveryState.ServerReceived,(await delivery.Send(row.Id,default)).State);Assert.Equal(1,await f.Received());
    }
    [PostgresFact]public async Task IndependentDestinationsCannotUseEachOthersClaimsApprovalsOrCredentials()
    {
        await using var first=await SubmissionFixture.Create();await using var second=await SubmissionFixture.Create();var c=await first.Card(incomplete:true);var result=await first.Record(c.Prepared);var row=await first.ReviewApprove(first.Draft(result));var document=first.Store.ApprovedSubmissionDocument(row.Id,first.Science);
        // Even a valid second-server credential cannot make a first-server claim local to it.
        using(var foreign=await second.Packet(document))Assert.Equal(HttpStatusCode.Forbidden,foreign.StatusCode);Assert.Equal(0,await second.Received());Assert.Equal(0,await first.Received());
        var before=second.Server.ClaimReads;using var mixed=new OrganizationConnections(new EngineOptions{ConfigurationDirectory=first.Root,OrganizationLoopbackDevelopment=true},new Environment(),first.Vault);var other=await Connect(mixed,second.Server,OrganizationDesktopPermission.SubmitEvidence);await Assert.ThrowsAsync<ArgumentException>(()=>mixed.RefreshSubmissionPolicies(other,row,first.Store,default));Assert.Equal(before,second.Server.ClaimReads);
        var forged=document with{Manifest=document.Manifest with{PilotId=Guid.NewGuid()}};using(var response=await first.Packet(forged))Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);Assert.Equal(0,await first.Received());
    }

    [PostgresFact]public async Task FreshServerRevocationAndStorageQuotaHoldWithoutPartialCommittedEvidence()
    {
        await using var f=await SubmissionFixture.Create();var c=await f.Card(incomplete:true);var result=await f.Record(c.Prepared);var row=await f.ReviewApprove(f.Draft(result));var doc=f.Store.ApprovedSubmissionDocument(row.Id,f.Science);
        using(var scope=f.Server.App.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();var quota=scope.ServiceProvider.GetRequiredService<OrganizationSubmissionOptions>();await db.SubmissionUsage.ExecuteUpdateAsync(s=>s.SetProperty(u=>u.PayloadBytes,quota.MaximumRetainedPayloadBytes-SubmissionEvidenceProtocol.Bytes(doc)+1));}
        using(var full=await f.Packet(doc,row.Consent))Assert.Equal(HttpStatusCode.Conflict,full.StatusCode);Assert.Equal(0,await f.Received());using(var scope=f.Server.App.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();await db.SubmissionUsage.ExecuteUpdateAsync(s=>s.SetProperty(u=>u.PayloadBytes,0));await db.DeviceGrants.Where(g=>g.Id==f.Upload).ExecuteUpdateAsync(s=>s.SetProperty(g=>g.Revoked,true));}
        using var delivery=f.Delivery();Assert.Equal(SubmissionDeliveryState.HeldAuthorization,(await delivery.Send(row.Id,default)).State);Assert.Equal(0,await f.Received());
    }

    [PostgresFact]public async Task RecordingPriorityLeavesApprovedDeliveryQueuedAndTelemetryContinues()
    {
        await using var f=await SubmissionFixture.Create();var c=await f.Card(incomplete:true);var result=await f.Record(c.Prepared);var row=await f.ReviewApprove(f.Draft(result));var coordinator=new MissionCoordinator(f.Store,1000);var now=DateTimeOffset.UtcNow;var frame=SubmissionFixture.Frame(now);coordinator.Apply(new(new(ConnectionState.Connected,frame.Source,"Synthetic fixture",now),frame));var active=coordinator.Start(new("Independent continuous flight",PlanId:c.Prepared.Plan.Id));using var delivery=new SubmissionDelivery(f.Store,f.Science,f.Desktop,coordinator);Assert.Equal(SubmissionDeliveryState.ApprovedQueued,(await delivery.Send(row.Id,default)).State);Assert.Equal(0,f.Server.SubmissionWrites);coordinator.Apply(new(new(ConnectionState.Connected,frame.Source,"Synthetic fixture",now.AddSeconds(1)),frame with{TimestampUtc=now.AddSeconds(1)}));Assert.Equal(active.Id,coordinator.Snapshot().ActiveMission!.Id);Assert.True(coordinator.Snapshot().ActiveMission!.SampleCount>0);coordinator.End(active.Id);Assert.Equal(SubmissionDeliveryState.ServerReceived,(await delivery.Send(row.Id,default)).State);
    }
    [PostgresFact]public async Task ReceiverReauthorizesRevokedSubmissionGrantAfterWaitingForMembershipLock()
    {
        await using var f=await SubmissionFixture.Create();var c=await f.Card(incomplete:true);var result=await f.Record(c.Prepared);var row=await f.ReviewApprove(f.Draft(result));var doc=f.Store.ApprovedSubmissionDocument(row.Id,f.Science);var connection=new Npgsql.NpgsqlConnectionStringBuilder(System.Environment.GetEnvironmentVariable("AEROSCIENCE_ORG_TEST_DATABASE")){Database=f.Database};await using var holder=new Npgsql.NpgsqlConnection(connection.ConnectionString);await holder.OpenAsync();await using var tx=await holder.BeginTransactionAsync();await using(var take=new Npgsql.NpgsqlCommand("SELECT pg_advisory_xact_lock(7014001)",holder,tx))await take.ExecuteNonQueryAsync();var send=f.Packet(doc,row.Consent);var deadline=DateTime.UtcNow.AddSeconds(10);var waiting=false;await using var observer=new Npgsql.NpgsqlConnection(connection.ConnectionString);await observer.OpenAsync();while(DateTime.UtcNow<deadline){await using var check=new Npgsql.NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event='advisory' AND query LIKE '%pg_advisory_xact_lock%')",observer);waiting=(bool)(await check.ExecuteScalarAsync())!;if(waiting)break;await Task.Delay(20);}Assert.True(waiting,"The real receive request must reach the authoritative transaction lock.");using(var scope=f.Server.App.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();await db.DeviceGrants.Where(g=>g.Id==f.Upload).ExecuteUpdateAsync(s=>s.SetProperty(g=>g.Revoked,true));}await tx.CommitAsync();using var denied=await send;Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);Assert.Equal(0,await f.Received());
    }

    [PostgresFact]public async Task ChangedVerifiedServerIdentityCannotMasqueradeAsOfflineApproval()
    {
        await using var f=await SubmissionFixture.Create();var c=await f.Card(incomplete:true);var result=await f.Record(c.Prepared);var row=f.Draft(result);await f.Desktop.RefreshSubmissionPolicies(f.Upload,row,f.Store,default);row=f.Store.SaveSubmissionDraft(MissionStore.SubmissionRequest(row),f.Science);var preview=f.Store.PreviewSavedSubmission(row.Id,f.Science);var before=f.Server.ClaimReads;
        using(var scope=f.Server.App.Services.CreateScope())await scope.ServiceProvider.GetRequiredService<OrganizationDatabase>().Organizations.ExecuteUpdateAsync(s=>s.SetProperty(o=>o.PublicId,Guid.NewGuid()));await Assert.ThrowsAsync<OrganizationSubmissionAuthorizationRequired>(()=>f.Desktop.ApproveSubmission(row.Id,new(1,row.Revision,preview.ManifestSha256,preview.PayloadSha256,preview.PayloadBytes,f.Upload,true),f.Store,f.Science,default));Assert.Equal(before,f.Server.ClaimReads);Assert.Equal(SubmissionDeliveryState.Draft,f.Store.SubmissionDraft(row.Id).State);Assert.Null(f.Store.SubmissionDraft(row.Id).Consent);Assert.Equal(0,f.Server.SubmissionWrites);
    }

}
