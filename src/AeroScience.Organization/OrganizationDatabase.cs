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
    public DbSet<DataProtectionKey> DataProtectionKeys {get;set;}=null!;
    public DbSet<EnrollmentPolicyRecord> EnrollmentPolicies=>Set<EnrollmentPolicyRecord>();
    public DbSet<MembershipRecord> Memberships=>Set<MembershipRecord>();
    public DbSet<InvitationRecord> Invitations=>Set<InvitationRecord>();
    public DbSet<DeviceGrantRecord> DeviceGrants=>Set<DeviceGrantRecord>();
    public DbSet<OpportunityRecord> Opportunities=>Set<OpportunityRecord>();
    public DbSet<ClaimRecord> Claims=>Set<ClaimRecord>();
    public DbSet<ClaimEventRecord> ClaimEvents=>Set<ClaimEventRecord>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        model.HasDefaultSchema("organization");
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
        // Schema receives no flight/science/original-source tables or attachment storage.
    }
    public override int SaveChanges(bool acceptAllChangesOnSuccess){ProtectHistory();return base.SaveChanges(acceptAllChangesOnSuccess);}
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess,CancellationToken cancellationToken=default){ProtectHistory();return base.SaveChangesAsync(acceptAllChangesOnSuccess,cancellationToken);}
    void ProtectHistory()
    {
        foreach(var entry in ChangeTracker.Entries<ClaimRecord>())
            if(entry.State==EntityState.Deleted||entry.State==EntityState.Modified&&new[]{nameof(ClaimRecord.UserId),nameof(ClaimRecord.OpportunityId),nameof(ClaimRecord.IssuedRevision),nameof(ClaimRecord.IssuedSha256),nameof(ClaimRecord.AssignedUtc),nameof(ClaimRecord.Slot)}.Any(p=>entry.Property(p).IsModified))
                throw new InvalidOperationException("Issued claim identity, definition and assignment boundaries are immutable.");
        if(ChangeTracker.Entries().Any(e=>(e.Entity is DefinitionRevision or OrganizationCommandReceipt or OrganizationAudit or OrganizationRecord or ClaimEventRecord)&&e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Organization identity, revision, audit and command history are append-only.");
    }
}
