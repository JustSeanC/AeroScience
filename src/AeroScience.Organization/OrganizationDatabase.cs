using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AeroScience.Organization;

public sealed class OrganizationUser:IdentityUser<Guid> { }
public sealed class OrganizationRecord
{
    public int Id {get;set;}=1;
    public Guid PublicId {get;set;}
    public string Name {get;set;}="";
    public string Description {get;set;}="";
    public DateTimeOffset CreatedUtc {get;set;}
    public Guid AdministratorId {get;set;}
}
public sealed class ProgramRecord
{
    public Guid Id {get;set;}
    public int Revision {get;set;}
    public string Name {get;set;}="";
    public string Description {get;set;}="";
    public DateTimeOffset CreatedUtc {get;set;}
    public DateTimeOffset UpdatedUtc {get;set;}
    public Guid AuthorId {get;set;}
    public string AuthorName {get;set;}="";
}
public sealed class CampaignRecord
{
    public int ProgressIndexVersion {get;set;}public string PublishedName {get;set;}="";public string PublishedSearchName {get;set;}="";public string PublishedObjective {get;set;}="";
    public Guid Id {get;set;}
    public Guid ProgramId {get;set;}
    public int Revision {get;set;}
    public int? PublishedRevision {get;set;}
    public string SnapshotJson {get;set;}="";
    public DateTimeOffset CreatedUtc {get;set;}
}
public sealed class DefinitionRevision
{
    public Guid ResourceId {get;set;}
    public int Revision {get;set;}
    public string Kind {get;set;}="";
    public string SnapshotJson {get;set;}="";
    public string Sha256 {get;set;}="";
    public DateTimeOffset TimestampUtc {get;set;}
    public Guid AuthorId {get;set;}
}
public sealed class OrganizationAudit
{
    public long Id {get;set;}
    public DateTimeOffset TimestampUtc {get;set;}
    public Guid ActorId {get;set;}
    public string ActorName {get;set;}="";
    public string Action {get;set;}="";
    public Guid ResourceId {get;set;}
    public int? Revision {get;set;}
}
public sealed class OrganizationCommandReceipt
{
    public Guid ActorId {get;set;}
    public Guid RequestId {get;set;}
    public string RequestSha256 {get;set;}="";
    public string ResponseJson {get;set;}="";
}
public sealed class OrganizationDatabase(DbContextOptions<OrganizationDatabase> options):IdentityDbContext<OrganizationUser,IdentityRole<Guid>,Guid>(options),IDataProtectionKeyContext
{
    public DbSet<OrganizationRecord> Organizations=>Set<OrganizationRecord>();
    public DbSet<ProgramRecord> Programs=>Set<ProgramRecord>();
    public DbSet<CampaignRecord> Campaigns=>Set<CampaignRecord>();
    public DbSet<DefinitionRevision> Revisions=>Set<DefinitionRevision>();
    public DbSet<OrganizationAudit> Audit=>Set<OrganizationAudit>();
    public DbSet<OrganizationCommandReceipt> Receipts=>Set<OrganizationCommandReceipt>();
    public DbSet<ResearchTemplateRecord> ResearchTemplates=>Set<ResearchTemplateRecord>();
    public DbSet<ResearchPublishingPolicyRecord> PublishingPolicies=>Set<ResearchPublishingPolicyRecord>();
    public DbSet<PublicationReviewRecord> PublicationReviews=>Set<PublicationReviewRecord>();
    public DbSet<DataProtectionKey> DataProtectionKeys {get;set;}=null!;
    public DbSet<EnrollmentPolicyRecord> EnrollmentPolicies=>Set<EnrollmentPolicyRecord>();
    public DbSet<MembershipRecord> Memberships=>Set<MembershipRecord>();
    public DbSet<InvitationRecord> Invitations=>Set<InvitationRecord>();
    public DbSet<DeviceGrantRecord> DeviceGrants=>Set<DeviceGrantRecord>();
    public DbSet<OpportunityRecord> Opportunities=>Set<OpportunityRecord>();
    public DbSet<ClaimRecord> Claims=>Set<ClaimRecord>();
    public DbSet<ClaimEventRecord> ClaimEvents=>Set<ClaimEventRecord>();
    public DbSet<SubmissionPolicyRecord> SubmissionPolicies=>Set<SubmissionPolicyRecord>();
    public DbSet<ReceivedSubmissionRecord> Submissions=>Set<ReceivedSubmissionRecord>();
    public DbSet<CancelledSubmissionRecord> CancelledSubmissions=>Set<CancelledSubmissionRecord>();
    public DbSet<SubmissionUsageRecord> SubmissionUsage=>Set<SubmissionUsageRecord>();
    public DbSet<ResearchReviewCaseRecord> Reviews=>Set<ResearchReviewCaseRecord>();
    public DbSet<ResearchReviewRequirementRecord> ReviewRequirements=>Set<ResearchReviewRequirementRecord>();
    public DbSet<ResearchReviewEventRecord> ReviewEvents=>Set<ResearchReviewEventRecord>();
    public DbSet<ResearchReviewRequestRecord> ReviewRequests=>Set<ResearchReviewRequestRecord>();
    public DbSet<ResearchReviewPackageRecord> ReviewPackages=>Set<ResearchReviewPackageRecord>();
    public DbSet<CampaignReviewGrantRecord> ReviewGrants=>Set<CampaignReviewGrantRecord>();
    public DbSet<CampaignProjectionRecord> CampaignProjections=>Set<CampaignProjectionRecord>();
    public DbSet<CampaignContributionRecord> CampaignContributions=>Set<CampaignContributionRecord>();
    public DbSet<CampaignMeasurementRecord> CampaignMeasurements=>Set<CampaignMeasurementRecord>();
    public DbSet<CampaignCoverageIndex> CoverageIndexes=>Set<CampaignCoverageIndex>();
    public DbSet<CampaignCoverageObservation> CoverageObservations=>Set<CampaignCoverageObservation>();
    public DbSet<CampaignCoverageGoalRecord> CoverageGoals=>Set<CampaignCoverageGoalRecord>();
    public DbSet<WorkflowReviewIndex> WorkflowReviewIndexes=>Set<WorkflowReviewIndex>();
    public DbSet<WorkflowRequirementApproval> WorkflowRequirementApprovals=>Set<WorkflowRequirementApproval>();
    public DbSet<ProgramWorkflowPolicyRecord> WorkflowPolicies=>Set<ProgramWorkflowPolicyRecord>();
    public DbSet<TestPhaseRecord> TestPhases=>Set<TestPhaseRecord>();
    public DbSet<PhaseCardRecord> PhaseCards=>Set<PhaseCardRecord>();
    public DbSet<WorkflowExceptionRecord> WorkflowExceptions=>Set<WorkflowExceptionRecord>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        ResearchTemplateDatabase.Configure(model);
        model.HasDefaultSchema("organization");
        model.Entity<WorkflowReviewIndex>().HasKey(r=>r.RootId);
        model.Entity<WorkflowReviewIndex>().HasOne<ResearchReviewCaseRecord>().WithMany().HasForeignKey(r=>r.RootId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<WorkflowRequirementApproval>().HasKey(r=>new{r.RootId,r.CardId,r.RequirementKey});
        model.Entity<WorkflowRequirementApproval>().HasOne<CampaignContributionRecord>().WithMany().HasForeignKey(r=>new{r.RootId,r.CardId}).OnDelete(DeleteBehavior.Cascade);
        model.Entity<ProgramWorkflowPolicyRecord>().HasKey(r=>r.Id);
        model.Entity<ProgramWorkflowPolicyRecord>().HasIndex(r=>r.ProgramId).IsUnique();
        model.Entity<ProgramWorkflowPolicyRecord>().Property(r=>r.Revision).IsConcurrencyToken();
        model.Entity<ProgramWorkflowPolicyRecord>().HasOne<ProgramRecord>().WithMany().HasForeignKey(r=>r.ProgramId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<TestPhaseRecord>().HasKey(r=>r.Id);
        model.Entity<TestPhaseRecord>().Property(r=>r.Revision).IsConcurrencyToken();
        model.Entity<TestPhaseRecord>().HasOne<ProgramRecord>().WithMany().HasForeignKey(r=>r.ProgramId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<TestPhaseRecord>().HasIndex(r=>new{r.ProgramId,r.Sequence,r.Id});
        model.Entity<TestPhaseRecord>().HasIndex(r=>new{r.ProgramId,r.Sequence}).IsUnique().HasFilter("\"Enabled\"");
        model.Entity<PhaseCardRecord>().HasKey(r=>new{r.PhaseId,r.OpportunityId});
        model.Entity<PhaseCardRecord>().HasOne<TestPhaseRecord>().WithMany().HasForeignKey(r=>r.PhaseId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<PhaseCardRecord>().HasOne<OpportunityRecord>().WithMany().HasForeignKey(r=>r.OpportunityId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<PhaseCardRecord>().HasIndex(r=>r.OpportunityId);
        model.Entity<WorkflowExceptionRecord>().HasKey(r=>r.Id);
        model.Entity<WorkflowExceptionRecord>().Property(r=>r.Revision).IsConcurrencyToken();
        model.Entity<WorkflowExceptionRecord>().HasOne<ProgramRecord>().WithMany().HasForeignKey(r=>r.ProgramId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<WorkflowExceptionRecord>().HasOne<OpportunityRecord>().WithMany().HasForeignKey(r=>r.OpportunityId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<WorkflowExceptionRecord>().HasIndex(r=>r.ScopeSha256).IsUnique().HasFilter("\"Active\"");
        model.Entity<WorkflowExceptionRecord>().HasIndex(r=>new{r.ProgramId,r.PhaseId,r.OpportunityId,r.PublicationRevision,r.RequirementKey,r.Active});
        model.Entity<WorkflowExceptionRecord>().HasIndex(r=>new{r.ProgramId,r.TimestampUtc,r.Id});

        model.Entity<CampaignCoverageIndex>().HasKey(r=>r.RootId);
        model.Entity<CampaignCoverageIndex>().HasOne<ResearchReviewCaseRecord>().WithMany().HasForeignKey(r=>r.RootId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<CampaignCoverageObservation>().HasKey(r=>new{r.RootId,r.CardId,r.RequirementKey,r.IdentitySha256});
        model.Entity<CampaignCoverageObservation>().HasOne<CampaignContributionRecord>().WithMany().HasForeignKey(r=>new{r.RootId,r.CardId}).OnDelete(DeleteBehavior.Cascade);
        model.Entity<CampaignCoverageObservation>().HasIndex(r=>new{r.CampaignId,r.CompatibilityId,r.IdentitySha256});
        model.Entity<CampaignCoverageObservation>().HasIndex(r=>new{r.CampaignId,r.OpportunityId,r.PublicationRevision,r.Variable,r.Category,r.TimestampUtc});
        model.Entity<CampaignCoverageObservation>().HasIndex(r=>new{r.CompatibilityId,r.AltitudeFeetMsl});
        model.Entity<CampaignCoverageObservation>().HasIndex(r=>new{r.CompatibilityId,r.Latitude,r.Longitude});
        model.Entity<CampaignCoverageObservation>().HasIndex(r=>new{r.CampaignId,r.Date,r.FlightId});
        model.Entity<CampaignCoverageGoalRecord>().HasKey(r=>r.Id);
        model.Entity<CampaignCoverageGoalRecord>().Property(r=>r.Revision).IsConcurrencyToken();
        model.Entity<CampaignCoverageGoalRecord>().HasOne<CampaignRecord>().WithMany().HasForeignKey(r=>r.CampaignId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<CampaignCoverageGoalRecord>().HasIndex(r=>new{r.CampaignId,r.Name,r.Id});
        model.Entity<CampaignCoverageGoalRecord>().HasIndex(r=>r.SearchName).HasOperators("text_pattern_ops");
        model.Entity<CampaignProjectionRecord>().HasKey(r=>r.RootId);
        model.Entity<CampaignProjectionRecord>().HasOne<ResearchReviewCaseRecord>().WithMany().HasForeignKey(r=>r.RootId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<CampaignContributionRecord>().HasKey(r=>new{r.RootId,r.CardId});
        model.Entity<CampaignContributionRecord>().HasOne<ResearchReviewCaseRecord>().WithMany().HasForeignKey(r=>r.RootId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<CampaignContributionRecord>().HasIndex(r=>new{r.OpportunityId,r.PublicationRevision,r.PublicationSha256,r.State,r.PilotId});
        model.Entity<CampaignContributionRecord>().HasIndex(r=>new{r.CampaignId,r.State,r.AcceptedUtc,r.RootId,r.CardId});
        model.Entity<CampaignContributionRecord>().HasIndex(r=>new{r.PilotId,r.CampaignId,r.OpportunityId});
        model.Entity<CampaignMeasurementRecord>().HasKey(r=>new{r.RootId,r.CardId,r.GroupId});
        model.Entity<CampaignMeasurementRecord>().HasOne<CampaignContributionRecord>().WithMany().HasForeignKey(r=>new{r.RootId,r.CardId}).OnDelete(DeleteBehavior.Cascade);
        model.Entity<OpportunityRecord>().HasIndex(r=>new{r.CampaignId,r.PublishedName,r.Id});
        model.Entity<OpportunityRecord>().HasIndex(r=>r.PublishedSearchName).HasOperators("text_pattern_ops");
        model.Entity<CampaignRecord>().HasIndex(r=>new{r.ProgramId,r.PublishedName,r.Id});
        model.Entity<CampaignRecord>().HasIndex(r=>r.PublishedSearchName).HasOperators("text_pattern_ops");
        model.Entity<ResearchReviewCaseRecord>().HasKey(r=>r.Id);
        model.Entity<ResearchReviewCaseRecord>().Property(r=>r.Revision).IsConcurrencyToken();
        model.Entity<ResearchReviewCaseRecord>().HasOne<ReceivedSubmissionRecord>().WithMany().HasForeignKey(r=>new{r.PilotId,r.SubmissionId}).HasPrincipalKey(r=>new{r.PilotId,SubmissionId=r.Id}).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ResearchReviewCaseRecord>().HasIndex(r=>new{r.State,r.UpdatedUtc,r.Id});
        model.Entity<ResearchReviewCaseRecord>().HasIndex(r=>new{r.CampaignId,r.UpdatedUtc,r.Id});
        model.Entity<ResearchReviewCaseRecord>().HasIndex(r=>new{r.UpdatedUtc,r.Id});
        model.Entity<ResearchReviewCaseRecord>().HasIndex(r=>r.SearchName).HasOperators("text_pattern_ops");
        model.Entity<ResearchReviewCaseRecord>().HasIndex(r=>r.SearchPilot).HasOperators("text_pattern_ops");
        model.Entity<ResearchReviewCaseRecord>().HasIndex(r=>r.SearchCampaign).HasOperators("text_pattern_ops");
        model.Entity<ResearchReviewCaseRecord>().HasIndex(r=>r.SearchProgram).HasOperators("text_pattern_ops");
        model.Entity<ResearchReviewCaseRecord>().HasIndex(r=>new{r.PilotId,r.UpdatedUtc,r.Id});
        model.Entity<ResearchReviewRequirementRecord>().HasKey(r=>new{r.RootId,r.CardId,r.Key});
        model.Entity<ResearchReviewRequirementRecord>().HasOne<ResearchReviewCaseRecord>().WithMany().HasForeignKey(r=>r.RootId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ResearchReviewEventRecord>().HasKey(r=>new{r.RootId,r.Sequence});
        model.Entity<ResearchReviewEventRecord>().HasIndex(r=>r.Id).IsUnique();
        model.Entity<ResearchReviewEventRecord>().HasOne<ResearchReviewCaseRecord>().WithMany().HasForeignKey(r=>r.RootId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ResearchReviewRequestRecord>().HasKey(r=>r.Id);
        model.Entity<ResearchReviewRequestRecord>().HasIndex(r=>new{r.RootId,r.State,r.CreatedUtc,r.Id});
        model.Entity<ResearchReviewRequestRecord>().HasOne<ResearchReviewCaseRecord>().WithMany().HasForeignKey(r=>r.RootId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ResearchReviewPackageRecord>().HasKey(r=>r.ReceiptId);
        model.Entity<ResearchReviewPackageRecord>().HasOne<ResearchReviewCaseRecord>().WithMany().HasForeignKey(r=>r.RootId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ResearchReviewPackageRecord>().HasOne<ReceivedSubmissionRecord>().WithMany().HasForeignKey(r=>new{r.PilotId,r.SubmissionId}).HasPrincipalKey(r=>new{r.PilotId,SubmissionId=r.Id}).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ResearchReviewPackageRecord>().HasIndex(r=>new{r.RootId,r.ReceivedUtc,r.ReceiptId});
        model.Entity<CampaignReviewGrantRecord>().HasKey(r=>new{r.CampaignId,r.ReviewerId});
        model.Entity<CampaignReviewGrantRecord>().Property(r=>r.Revision).IsConcurrencyToken();
        model.Entity<CampaignReviewGrantRecord>().HasOne<CampaignRecord>().WithMany().HasForeignKey(r=>r.CampaignId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<CampaignReviewGrantRecord>().HasOne<OrganizationUser>().WithMany().HasForeignKey(r=>r.ReviewerId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<CampaignReviewGrantRecord>().HasIndex(r=>new{r.ReviewerId,r.Allowed,r.CampaignId});
        model.Entity<SubmissionPolicyRecord>().HasKey(x=>new{x.OpportunityId,x.Revision});
        model.Entity<SubmissionPolicyRecord>().HasOne<OpportunityRecord>().WithMany().HasForeignKey(x=>x.OpportunityId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ReceivedSubmissionRecord>().HasKey(x=>new{x.PilotId,x.Id});
        model.Entity<ReceivedSubmissionRecord>().HasIndex(x=>x.ReceiptId).IsUnique();
        model.Entity<ReceivedSubmissionRecord>().HasIndex(x=>new{x.CampaignId,x.ReceivedUtc,x.Id});
        model.Entity<ReceivedSubmissionRecord>().HasIndex(x=>new{x.SearchName,x.ReceivedUtc,x.Id});
        model.Entity<ReceivedSubmissionRecord>().HasIndex(x=>new{x.ReceivedUtc,x.Id});
        model.Entity<ReceivedSubmissionRecord>().HasOne<OrganizationUser>().WithMany().HasForeignKey(x=>x.PilotId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ReceivedSubmissionRecord>().HasOne<CampaignRecord>().WithMany().HasForeignKey(x=>x.CampaignId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<CancelledSubmissionRecord>().HasKey(x=>new{x.PilotId,x.Id});
        model.Entity<CancelledSubmissionRecord>().HasOne<OrganizationUser>().WithMany().HasForeignKey(x=>x.PilotId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<SubmissionUsageRecord>().ToTable(t=>t.HasCheckConstraint("single_submission_usage","\"Id\" = 1 AND \"PayloadBytes\" >= 0"));
        model.Entity<OpportunityRecord>().HasOne<CampaignRecord>().WithMany().HasForeignKey(x=>x.CampaignId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<OpportunityRecord>().HasIndex(x=>new{x.CampaignId,x.CreatedUtc,x.Id});
        model.Entity<OpportunityRecord>().HasIndex(x=>new{x.CreatedUtc,x.Id});
        model.Entity<OpportunityRecord>().Property(x=>x.Revision).IsConcurrencyToken();
        model.Entity<OpportunityRecord>().Property(x=>x.AvailabilityRevision).IsConcurrencyToken();
        model.Entity<ClaimRecord>().HasOne<OpportunityRecord>().WithMany().HasForeignKey(x=>x.OpportunityId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ClaimRecord>().HasOne<OrganizationUser>().WithMany().HasForeignKey(x=>x.UserId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ClaimRecord>().HasOne<DefinitionRevision>().WithMany().HasForeignKey(x=>new{x.OpportunityId,x.IssuedRevision}).HasPrincipalKey(x=>new{x.ResourceId,x.Revision}).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ClaimRecord>().Property(x=>x.Revision).IsConcurrencyToken();
        model.Entity<ClaimRecord>().HasIndex(x=>new{x.OpportunityId,x.UserId}).IsUnique().HasFilter("\"State\" IN (0,1)");
        model.Entity<ClaimRecord>().HasIndex(x=>new{x.OpportunityId,x.Slot}).IsUnique().HasFilter("\"State\" IN (0,1)");
        model.Entity<ClaimRecord>().HasIndex(x=>new{x.UserId,x.AssignedUtc,x.Id});
        model.Entity<ClaimRecord>().HasIndex(x=>new{x.OpportunityId,x.AssignedUtc,x.Id});
        model.Entity<ClaimRecord>().ToTable(t=>{t.HasCheckConstraint("claim_state","\"State\" IN (0,1,2)");t.HasCheckConstraint("claim_slot","\"Slot\" > 0");t.HasCheckConstraint("claim_lifecycle","(\"State\" = 2) = (\"EndedUtc\" IS NOT NULL)");});
        model.Entity<ClaimEventRecord>().HasOne<ClaimRecord>().WithMany().HasForeignKey(x=>x.ClaimId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ClaimEventRecord>().HasIndex(x=>new{x.ClaimId,x.Id});
        model.Entity<EnrollmentPolicyRecord>().ToTable(t=>t.HasCheckConstraint("single_enrollment_policy","\"Id\" = 1"));
        model.Entity<EnrollmentPolicyRecord>().Property(x=>x.Revision).IsConcurrencyToken();
        model.Entity<MembershipRecord>().HasKey(x=>x.UserId);
        model.Entity<MembershipRecord>().HasOne<OrganizationUser>().WithMany().HasForeignKey(x=>x.UserId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<MembershipRecord>().HasIndex(x=>new{x.UpdatedUtc,x.UserId});
        model.Entity<MembershipRecord>().Property(x=>x.Revision).IsConcurrencyToken();
        model.Entity<InvitationRecord>().HasIndex(x=>new{x.CreatedUtc,x.Id});
        model.Entity<DeviceGrantRecord>().HasIndex(x=>x.UserId);
        model.Entity<DeviceGrantRecord>().HasIndex(x=>x.CredentialHash).IsUnique();
        model.Entity<OrganizationRecord>().ToTable(t=>t.HasCheckConstraint("single_organization","\"Id\" = 1"));
        model.Entity<OrganizationRecord>().HasOne<OrganizationUser>().WithMany().HasForeignKey(x=>x.AdministratorId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ProgramRecord>().Property(x=>x.Revision).IsConcurrencyToken();
        model.Entity<ProgramRecord>().HasIndex(x=>new{x.CreatedUtc,x.Id});
        model.Entity<CampaignRecord>().Property(x=>x.Revision).IsConcurrencyToken();
        model.Entity<CampaignRecord>().HasOne<ProgramRecord>().WithMany().HasForeignKey(x=>x.ProgramId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<CampaignRecord>().HasIndex(x=>new{x.ProgramId,x.CreatedUtc,x.Id});
        model.Entity<DefinitionRevision>().HasKey(x=>new{x.ResourceId,x.Revision});
        model.Entity<OrganizationCommandReceipt>().HasKey(x=>new{x.ActorId,x.RequestId});
        model.Entity<OrganizationAudit>().HasIndex(x=>x.Id);
        model.Entity<DefinitionRevision>().Property(x=>x.SnapshotJson).HasColumnType("text");
        // Only explicitly consented bounded submission payloads; no general science/flight archive.
    }
    public override int SaveChanges(bool acceptAllChangesOnSuccess){ProtectHistory();return base.SaveChanges(acceptAllChangesOnSuccess);}
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess,CancellationToken cancellationToken=default){ProtectHistory();return base.SaveChangesAsync(acceptAllChangesOnSuccess,cancellationToken);}
    void ProtectHistory()
    {
        foreach(var entry in ChangeTracker.Entries<ResearchTemplateRecord>())
            if(entry.State==EntityState.Deleted||entry.State==EntityState.Modified&&new[]{nameof(ResearchTemplateRecord.OwnerId),nameof(ResearchTemplateRecord.Proposal),nameof(ResearchTemplateRecord.CreatedUtc)}.Any(p=>entry.Property(p).IsModified))throw new InvalidOperationException("Template ownership and original proposal identity are immutable.");
        foreach(var entry in ChangeTracker.Entries<PublicationReviewRecord>())
            if(entry.State==EntityState.Deleted||entry.State==EntityState.Modified&&(entry.Entity.Kind.StartsWith("publication:",StringComparison.Ordinal)||new[]{nameof(PublicationReviewRecord.ResourceId),nameof(PublicationReviewRecord.Kind),nameof(PublicationReviewRecord.DraftRevision),nameof(PublicationReviewRecord.CreatedUtc)}.Any(p=>entry.Property(p).IsModified)))throw new InvalidOperationException("Publication authorization and review targets are immutable.");
        foreach(var entry in ChangeTracker.Entries<ClaimRecord>())
            if(entry.State==EntityState.Deleted||entry.State==EntityState.Modified&&new[]{nameof(ClaimRecord.UserId),nameof(ClaimRecord.OpportunityId),nameof(ClaimRecord.IssuedRevision),nameof(ClaimRecord.IssuedSha256),nameof(ClaimRecord.AssignedUtc),nameof(ClaimRecord.Slot)}.Any(p=>entry.Property(p).IsModified))
                throw new InvalidOperationException("Issued claim identity, definition and assignment boundaries are immutable.");
        foreach(var entry in ChangeTracker.Entries<ResearchReviewCaseRecord>())
            if(entry.State==EntityState.Deleted||entry.State==EntityState.Modified&&new[]{nameof(ResearchReviewCaseRecord.Id),nameof(ResearchReviewCaseRecord.PilotId),nameof(ResearchReviewCaseRecord.SubmissionId),nameof(ResearchReviewCaseRecord.ProgramId),nameof(ResearchReviewCaseRecord.CampaignId),nameof(ResearchReviewCaseRecord.RootPayloadSha256),nameof(ResearchReviewCaseRecord.ReceivedUtc)}.Any(p=>entry.Property(p).IsModified))throw new InvalidOperationException("The received review identity and original source are immutable.");
        foreach(var entry in ChangeTracker.Entries<ResearchReviewRequirementRecord>())
            if(entry.State==EntityState.Deleted||entry.State==EntityState.Modified&&entry.Property(nameof(ResearchReviewRequirementRecord.DefinitionJson)).IsModified)throw new InvalidOperationException("Issued review requirements are immutable.");
        if(ChangeTracker.Entries().Any(e=>(e.Entity is DefinitionRevision or OrganizationCommandReceipt or OrganizationAudit or OrganizationRecord or ClaimEventRecord or SubmissionPolicyRecord or ReceivedSubmissionRecord or CancelledSubmissionRecord or ResearchReviewEventRecord or ResearchReviewPackageRecord)&&e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Organization identity, revision, audit and command history are append-only.");
    }
}
