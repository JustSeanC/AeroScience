using Microsoft.EntityFrameworkCore;

namespace AeroScience.Organization;

// Existing PostgreSQL organization database and existing immutable revision/audit/receipt tables.
public sealed class ResearchTemplateRecord
{
    public Guid Id {get;set;}
    public bool Proposal {get;set;}
    public Guid OwnerId {get;set;}
    public int Revision {get;set;}
    public int? PublishedRevision {get;set;}
    public string Name {get;set;}="";
    public string Status {get;set;}="";
    public string SnapshotJson {get;set;}="";
    public DateTimeOffset CreatedUtc {get;set;}
}
public sealed class ResearchPublishingPolicyRecord
{
    public int Id {get;set;}=1;
    public int Revision {get;set;}
    public int Mode {get;set;}
}
public sealed class PublicationReviewRecord
{
    public Guid Id {get;set;}
    public Guid ResourceId {get;set;}
    public string Kind {get;set;}="";
    public int DraftRevision {get;set;}
    public int Revision {get;set;}
    public string Status {get;set;}="";
    public string SnapshotJson {get;set;}="";
    public DateTimeOffset CreatedUtc {get;set;}
}
internal static class ResearchTemplateDatabase
{
    internal static void Configure(ModelBuilder m)
    {
        m.Entity<ResearchTemplateRecord>(e=>{e.HasKey(x=>x.Id);e.Property(x=>x.Revision).IsConcurrencyToken();e.Property(x=>x.Name).HasMaxLength(100);e.Property(x=>x.Status).HasMaxLength(32);e.HasIndex(x=>new{x.Proposal,x.CreatedUtc,x.Id});e.HasIndex(x=>new{x.OwnerId,x.Proposal,x.CreatedUtc,x.Id});e.HasIndex(x=>new{x.Proposal,x.PublishedRevision});});
        m.Entity<ResearchPublishingPolicyRecord>(e=>{e.HasKey(x=>x.Id);e.Property(x=>x.Revision).IsConcurrencyToken();});
        m.Entity<PublicationReviewRecord>(e=>{e.HasKey(x=>x.Id);e.Property(x=>x.Revision).IsConcurrencyToken();e.Property(x=>x.Kind).HasMaxLength(32);e.Property(x=>x.Status).HasMaxLength(32);e.HasIndex(x=>new{x.Status,x.CreatedUtc,x.Id});e.HasIndex(x=>new{x.ResourceId,x.DraftRevision,x.Kind});});
    }
}
