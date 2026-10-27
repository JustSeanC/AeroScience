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
    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        model.HasDefaultSchema("organization");
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
        if(ChangeTracker.Entries().Any(e=>(e.Entity is DefinitionRevision or OrganizationCommandReceipt or OrganizationAudit or OrganizationRecord)&&e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Organization identity, revision, audit and command history are append-only.");
    }
}
