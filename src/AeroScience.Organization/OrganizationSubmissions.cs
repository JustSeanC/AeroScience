using System.Security.Claims;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace AeroScience.Organization;

public sealed class OrganizationSubmissions(OrganizationDatabase db,OrganizationMembership members,OrganizationDevices devices,
    OrganizationManagement history,OrganizationSettings settings,OrganizationSubmissionOptions limits,OrganizationReviews reviews,CampaignProgressProjection progress)
{
    static SubmissionPolicy View(SubmissionPolicyRecord row)=>SubmissionEvidenceProtocol.DefaultPolicy(row.OpportunityId,row.PublishedUtc) with{Revision=row.Revision,Mode=row.Mode};
    async Task Lock(CancellationToken ct)=>await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({OrganizationMembership.MembershipLock})",ct);
    async Task<SubmissionPolicy> Policy(Guid opportunity,CancellationToken ct)
    {
        var row=await db.SubmissionPolicies.AsNoTracking().Where(p=>p.OpportunityId==opportunity).OrderByDescending(p=>p.Revision).FirstOrDefaultAsync(ct)??throw new OrganizationConflict("This opportunity has no published submission policy. Ask a manager to publish it before approval.");return View(row);
    }
    public async Task<SubmissionCapabilities> Capabilities(Guid claimId,HttpContext c,CancellationToken ct)
    {
        var grant=await devices.Authorize(c,ct);var claim=await db.Claims.AsNoTracking().SingleOrDefaultAsync(r=>r.Id==claimId&&r.UserId==grant.UserId,ct)??throw new OrganizationNotFound();
        return new((await members.Public(ct)).Id,grant.UserId!.Value,await Policy(claim.OpportunityId,ct),grant.Permission);
    }
    public async Task<SubmissionPolicy> ManagerPolicy(Guid opportunity,ClaimsPrincipal actor,CancellationToken ct)
    {await members.RequireManager(actor,false,ct);return await Policy(opportunity,ct);}
    public Task<SubmissionPolicy> PublishPolicy(Guid opportunity,SubmissionPolicyCommand command,ClaimsPrincipal actor,CancellationToken ct)
    {
        if(command.Version!=1||command.RequestId==Guid.Empty||!Enum.IsDefined(command.Mode))throw new ArgumentException("Choose a supported evidence policy and review its current revision.");
        return history.Mutate(actor,command.RequestId,new{operation="submission-policy",opportunity,command},async(user,name)=>{
            var current=await Policy(opportunity,ct);if(current.Revision!=command.ExpectedRevision)throw new OrganizationConflict("The submission policy changed; review it again.");
            var row=new SubmissionPolicyRecord{OpportunityId=opportunity,Revision=current.Revision+1,Mode=command.Mode,PublishedUtc=OrganizationTime.UtcNow,AuthorId=user};db.SubmissionPolicies.Add(row);history.Audit(user,name,"submission-policy-published",opportunity,row.Revision);return View(row);
        },ct,async()=>{await Lock(ct);await members.RequireManager(actor,false,ct);});
    }
    static SubmissionReceipt Receipt(ReceivedSubmissionRecord row)=>JsonSerializer.Deserialize<SubmissionReceipt>(row.ReceiptJson,WireJson.Options)??throw new InvalidOperationException("Receipt unavailable.");
    async Task<ReceivedSubmissionRecord?> Received(Guid pilot,Guid id,CancellationToken ct)=>await db.Submissions.AsNoTracking().SingleOrDefaultAsync(r=>r.PilotId==pilot&&r.Id==id,ct);
    public async Task<SubmissionRemoteState> Status(Guid id,string manifestSha,HttpContext c,CancellationToken ct)
    {
        var grant=await devices.Authorize(c,ct,requireSubmission:true);var row=await Received(grant.UserId!.Value,id,ct);
        if(row is not null){if(row.ManifestSha256!=manifestSha)throw new OrganizationConflict("This submission identity already belongs to a different approved manifest.");return new("Received",Receipt(row));}
        var cancelled=await db.CancelledSubmissions.AsNoTracking().SingleOrDefaultAsync(r=>r.PilotId==grant.UserId&&r.Id==id,ct);if(cancelled is not null&&cancelled.ManifestSha256!=manifestSha)throw new OrganizationConflict("The original submission was cancelled under a different manifest.");return new(cancelled is null?"Not Received":"Cancelled");
    }
    public async Task<SubmissionRemoteState> Cancel(SubmissionCancellation command,HttpContext c,CancellationToken ct)
    {
        if(command.SubmissionId==Guid.Empty||command.ManifestSha256 is not {Length:64}||command.ManifestSha256.Any(ch=>!char.IsAsciiHexDigit(ch)))throw new ArgumentException("Choose the exact approved submission to cancel.");
        await using var tx=await db.Database.BeginTransactionAsync(ct);await Lock(ct);var grant=await devices.Authorize(c,ct,requireSubmission:true);var pilot=grant.UserId!.Value;
        var row=await Received(pilot,command.SubmissionId,ct);if(row is not null){if(row.ManifestSha256!=command.ManifestSha256)throw new OrganizationConflict("The received manifest differs.");return new("Received",Receipt(row));}
        var existing=await db.CancelledSubmissions.FindAsync([pilot,command.SubmissionId],ct);if(existing is not null){if(existing.ManifestSha256!=command.ManifestSha256)throw new OrganizationConflict("This cancellation identity differs.");return new("Cancelled");}
        // Identity is (authenticated pilot, submission ID): another pilot cannot reserve or cancel this pilot's namespace.
        db.CancelledSubmissions.Add(new(){PilotId=pilot,Id=command.SubmissionId,ManifestSha256=command.ManifestSha256,CancelledUtc=OrganizationTime.UtcNow});history.Audit(pilot,"desktop","submission-cancelled",command.SubmissionId);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new("Cancelled");
    }
    async Task ValidateClaims(SubmissionDocument document,Guid pilot,CancellationToken ct)
    {
        var m=document.Manifest;var organization=await members.Public(ct);
        if(m.OrganizationId!=organization.Id||m.Origin!=settings.Origin.GetLeftPart(UriPartial.Authority)||m.PilotId!=pilot)throw new OrganizationForbidden();
        foreach(var b in m.Cards){
            ct.ThrowIfCancellationRequested();var claim=await db.Claims.AsNoTracking().SingleOrDefaultAsync(r=>r.Id==b.ClaimId&&r.UserId==pilot,ct)??throw new OrganizationForbidden();
            if(claim.OpportunityId!=b.OpportunityId||claim.IssuedRevision!=b.IssuedRevision||claim.IssuedSha256!=b.IssuedSha256||claim.AcceptedUtc is null)throw new OrganizationForbidden();
            var original=await history.ReadRevision(claim.OpportunityId,claim.IssuedRevision,ct);var published=JsonSerializer.Deserialize<OrganizationOpportunitySnapshot>(original.SnapshotJson,WireJson.Options)!;
            var definition=document.Evidence.Single(i=>i.Reference.CardId==b.CardId&&i.Data.Definition is not null).Data.Definition!;
            if(original.Sha256!=b.IssuedSha256||published.Status!=OrganizationPublication.Published||published.OrganizationId!=organization.Id||published.CampaignId!=m.CampaignId||published.Id!=b.OpportunityId
                ||b.CardId!=MissionStore.OrganizationLocalCardId(m.Origin,organization.Id,pilot,claim.Id,claim.IssuedSha256)||b.CardRevision!=1||definition.CreatedUtc!=claim.AcceptedUtc||!MissionStore.MatchesSubmissionDefinition(definition,published))throw new OrganizationForbidden();
            // The server ledger determines the claim interval. Client names or a locally asserted Pass never authorize it.
            var cutoff=claim.EndedUtc;
            var allowedRoles=published.Policy.EligibleRoles.Select(role=>"membership-active-"+role.ToLowerInvariant()).ToArray();
            var suspension=await db.Audit.AsNoTracking().Where(a=>a.ResourceId==pilot&&a.TimestampUtc>=claim.AcceptedUtc&&(a.Action.StartsWith("membership-suspended-")||a.Action.StartsWith("membership-removed-")||a.Action.StartsWith("membership-active-")&&!allowedRoles.Contains(a.Action))).OrderBy(a=>a.TimestampUtc).Select(a=>(DateTimeOffset?)a.TimestampUtc).FirstOrDefaultAsync(ct);
            var withdrawal=await db.Audit.AsNoTracking().Where(a=>a.ResourceId==b.OpportunityId&&a.TimestampUtc>=claim.AcceptedUtc&&a.Action=="opportunity-withdrawn").OrderBy(a=>a.TimestampUtc).Select(a=>(DateTimeOffset?)a.TimestampUtc).FirstOrDefaultAsync(ct);
            if(suspension is {} suspended&&(cutoff is null||suspended<cutoff))cutoff=suspended;if(withdrawal is {} withdrawn&&(cutoff is null||withdrawn<cutoff))cutoff=withdrawn;
            if(b.StartUtc<claim.AcceptedUtc||cutoff is {} ended&&b.EndUtc>=ended||b.EndUtc>OrganizationTime.UtcNow)throw new OrganizationForbidden();
            var current=await Policy(b.OpportunityId,ct);var selected=m.Policies.Single(p=>p.OpportunityId==b.OpportunityId);
            if(PreflightReadiness.Hash(current)!=PreflightReadiness.Hash(selected))throw new OrganizationConflict("The published submission policy changed. Review and approve a new package; issued test-card requirements were not changed.");
        }
    }
    public async Task<SubmissionReceipt> Receive(HttpContext c,CancellationToken ct)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(15));ct=deadline.Token;
        var initial=await devices.Authorize(c,ct,requireSubmission:true);
        if(!c.Request.HasJsonContentType()||c.Request.Headers.ContentEncoding.Count>0)throw new ArgumentException("Only bounded uncompressed typed JSON evidence is supported; ZIPs, arbitrary files and external references are not accepted.");
        var options=c.RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        using var buffer=new MemoryStream();var chunk=new byte[8192];
        while(true){var read=await c.Request.Body.ReadAsync(chunk,ct);if(read==0)break;if(buffer.Length+read>SubmissionEvidenceProtocol.MaximumWireBytes)throw new BadHttpRequestException("The protected evidence package is too large.",413);buffer.Write(chunk,0,read);}
        var upload=JsonSerializer.Deserialize<SubmissionUpload>(buffer.GetBuffer().AsSpan(0,checked((int)buffer.Length)),options)??throw new ArgumentException("Choose a reviewed submission package.");var d=upload.Document;var consent=upload.Consent;
        SubmissionEvidenceProtocol.Validate(d,ct);var payloadSha=PreflightReadiness.Hash(d);var bytes=SubmissionEvidenceProtocol.Bytes(d);var manifestSha=PreflightReadiness.Hash(d.Manifest);
        if(consent is null||consent.Version!=1||!consent.ExplicitApproval||consent.Scope!="submitEvidence"||consent.GrantId!=initial.Id||consent.ManifestSha256!=manifestSha||consent.PayloadSha256!=payloadSha||consent.PayloadBytes!=bytes||consent.ApprovedUtc==default||consent.ApprovedUtc>OrganizationTime.UtcNow.AddMinutes(5))throw new OrganizationForbidden();
        // Parse/evaluate the bounded package outside the membership mutation lock; reauthorize freshly inside it.
        foreach(var binding in d.Manifest.Cards){
            var scoped=d with{Manifest=d.Manifest with{Cards=[binding],Policies=d.Manifest.Policies.Where(p=>p.OpportunityId==binding.OpportunityId).ToArray(),Items=d.Manifest.Items.Where(i=>i.CardId==binding.CardId).ToArray()},Evidence=d.Evidence.Where(i=>i.Reference.CardId==binding.CardId).ToArray()};
            var assessment=SubmissionEvidenceProtocol.Assess(scoped,ct);if(scoped.Manifest.Policies[0].Mode==SubmissionPolicyMode.RequireMinimumEvidence&&(!assessment.MinimumMet||!assessment.RuleSupported))throw new OrganizationConflict("The selected evidence does not meet this published minimum-evidence policy. An incomplete draft is not formal submission authorization.");
        }
        await using var tx=await db.Database.BeginTransactionAsync(ct);await Lock(ct);var grant=await devices.Authorize(c,ct,requireSubmission:true);if(grant.Id!=consent.GrantId||grant.UserId!=initial.UserId)throw new OrganizationForbidden();
        var existing=await Received(grant.UserId!.Value,d.Manifest.Id,ct);
        if(existing is not null){if(existing.ManifestSha256!=manifestSha||existing.PayloadSha256!=payloadSha)throw new OrganizationConflict("This submission identity already received different evidence. No duplicate or replacement was created.");return Receipt(existing);}
        if(await db.CancelledSubmissions.AnyAsync(r=>r.PilotId==grant.UserId&&r.Id==d.Manifest.Id,ct))throw new OrganizationConflict("This submission was cancelled before receipt. New evidence needs a new reviewed submission identity.");
        await ValidateClaims(d,grant.UserId!.Value,ct);
        var usage=await db.SubmissionUsage.SingleAsync(ct);if(usage.PayloadBytes>limits.MaximumRetainedPayloadBytes-bytes)throw new OrganizationConflict("The organization's configured evidence-storage quota is full. Your approved local package was not received; ask the administrator to resolve storage.");
        var receipt=new SubmissionReceipt(1,Guid.NewGuid(),d.Manifest.Id,d.Manifest.OrganizationId,grant.UserId.Value,d.Manifest.CampaignId,manifestSha,payloadSha,bytes,OrganizationTime.UtcNow,"Received / Awaiting Review",SubmissionEvidenceProtocol.RetentionDisclosure);
        db.Submissions.Add(new(){PilotId=grant.UserId.Value,Id=d.Manifest.Id,ReceiptId=receipt.Id,CampaignId=d.Manifest.CampaignId,Name=d.Manifest.Name,SearchName=d.Manifest.Name.ToUpperInvariant(),ManifestSha256=manifestSha,PayloadSha256=payloadSha,PayloadBytes=bytes,PayloadJson=JsonSerializer.Serialize(d,WireJson.Options),ReceiptJson=JsonSerializer.Serialize(receipt,WireJson.Options),ReceivedUtc=receipt.ReceivedUtc});usage.PayloadBytes+=bytes;
        await reviews.Received(d,receipt,ct);history.Audit(grant.UserId.Value,"desktop","submission-received-awaiting-review",d.Manifest.Id,1);ct.ThrowIfCancellationRequested();await db.SaveChangesAsync(ct);await progress.Sync(d.Manifest.ReviewLink?.RootReceiptId??receipt.Id,ct);await tx.CommitAsync(ct);return receipt;
    }
    public async Task<OrganizationPage<SubmissionServerSummary>> Inventory(int offset,string? prefix,Guid? campaign,ClaimsPrincipal actor,CancellationToken ct)
    {
        await members.RequireManager(actor,false,ct);if(offset<0||prefix?.Length>100)throw new ArgumentException("Choose a valid page and a name prefix of up to 100 characters.");var rows=db.Submissions.AsNoTracking();if(campaign is {} campaignId)rows=rows.Where(r=>r.CampaignId==campaignId);if(!string.IsNullOrWhiteSpace(prefix)){var value=prefix.ToUpperInvariant();rows=rows.Where(r=>r.SearchName.StartsWith(value));}
        var page=await rows.OrderByDescending(r=>r.ReceivedUtc).ThenBy(r=>r.Id).Skip(offset).Take(21).Select(r=>new SubmissionServerSummary(r.Id,r.Name,r.PilotId,r.CampaignId,r.ReceivedUtc,"Received / Awaiting Review",r.ManifestSha256,r.PayloadBytes,r.ReceiptId)).ToArrayAsync(ct);return new(1,page.Take(20).ToArray(),page.Length>20?checked(offset+20):null);
    }
    public async Task<SubmissionDocument> Inspect(Guid pilot,Guid id,ClaimsPrincipal actor,CancellationToken ct)
    {await members.RequireManager(actor,false,ct);var row=await Received(pilot,id,ct)??throw new OrganizationNotFound();var doc=JsonSerializer.Deserialize<SubmissionDocument>(row.PayloadJson,WireJson.Options)??throw new InvalidOperationException("Received evidence unavailable.");if(PreflightReadiness.Hash(doc)!=row.PayloadSha256)throw new InvalidOperationException("Stored evidence failed its integrity check.");return doc;}
}

public static class OrganizationSubmissionApi
{
    public const string ReceivePath="/api/member/submissions/receive",CancelPath="/api/member/submissions/cancel";
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/member/claims/{id:guid}/submission-policy",(Guid id,OrganizationSubmissions s,HttpContext c)=>s.Capabilities(id,c,c.RequestAborted));
        app.MapPost(ReceivePath,(OrganizationSubmissions s,HttpContext c)=>s.Receive(c,c.RequestAborted)).RequireRateLimiting("evidence-receive");
        app.MapGet("/api/member/submissions/{id:guid}",(Guid id,string manifestSha,OrganizationSubmissions s,HttpContext c)=>s.Status(id,manifestSha,c,c.RequestAborted));
        app.MapPost(CancelPath,(SubmissionCancellation r,OrganizationSubmissions s,HttpContext c)=>s.Cancel(r,c,c.RequestAborted)).RequireRateLimiting("public");
        var api=app.MapGroup("/api/organization/submissions").RequireAuthorization(OrganizationApplication.ManagePolicy);
        api.MapGet("",(int? offset,string? prefix,Guid? campaign,OrganizationSubmissions s,HttpContext c)=>s.Inventory(offset??0,prefix,campaign,c.User,c.RequestAborted));
        api.MapGet("/{pilot:guid}/{id:guid}",(Guid pilot,Guid id,OrganizationSubmissions s,HttpContext c)=>s.Inspect(pilot,id,c.User,c.RequestAborted));
        api.MapGet("/policies/{opportunity:guid}",(Guid opportunity,OrganizationSubmissions s,HttpContext c)=>s.ManagerPolicy(opportunity,c.User,c.RequestAborted));
        api.MapPost("/policies/{opportunity:guid}",(Guid opportunity,SubmissionPolicyCommand r,OrganizationSubmissions s,HttpContext c)=>s.PublishPolicy(opportunity,r,c.User,c.RequestAborted));
    }
}
