using AeroScience.Shared;
using Microsoft.EntityFrameworkCore;
namespace AeroScience.Organization;

// Auditable consent, never an archive of live coordinates or aircraft payloads.
public sealed class LiveConsentRecord
{
    public Guid Id {get;set;} public Guid RequestId {get;set;} public string RequestSha256 {get;set;}="";
    public Guid OrganizationId {get;set;} public Guid PilotId {get;set;} public Guid GrantId {get;set;} public Guid FlightId {get;set;}
    public Guid InstanceId {get;set;} public int Revision {get;set;}=1; public int PolicyRevision {get;set;}
    public long LastSequence {get;set;} public string LastUpdateSha256 {get;set;}="";
    public string DisplayLabel {get;set;}=""; public string ClaimsJson {get;set;}="[]";
    public DateTimeOffset FlightStartedUtc {get;set;} public DateTimeOffset StartedUtc {get;set;} public DateTimeOffset ExpiresUtc {get;set;}
    public DateTimeOffset LastSeenUtc {get;set;} public DateTimeOffset? EndedUtc {get;set;} public string EndReason {get;set;}="";
}
public sealed class LivePolicyRecord
{
    public int Id {get;set;}=1; public int Revision {get;set;} public bool Enabled {get;set;}=true;
    public string RestrictedCampaignsJson {get;set;}="[]";
}
public sealed class OrganizationEventRecord
{
    public long Sequence {get;set;} public Guid Id {get;set;} public string IdempotencyKey {get;set;}="";
    public Guid OrganizationId {get;set;} public OrganizationEventType Type {get;set;}
    public string Authority {get;set;}=""; public Guid SourceId {get;set;} public int? SourceRevision {get;set;}
    public Guid ActorId {get;set;} public DateTimeOffset TimestampUtc {get;set;}
    public Guid? SubjectId {get;set;} public Guid? CampaignId {get;set;} public Guid? CardId {get;set;} public string? RequirementKey {get;set;}
    public OrganizationEventVisibility Visibility {get;set;} public string Meaning {get;set;}="";
}
public static class OrganizationLiveDatabase
{
    public static void Configure(ModelBuilder m)
    {
        m.Entity<LiveConsentRecord>().HasKey(x=>x.Id);
        m.Entity<LiveConsentRecord>().HasIndex(x=>new{x.PilotId,x.RequestId}).IsUnique();
        m.Entity<LiveConsentRecord>().HasIndex(x=>new{x.InstanceId,x.EndedUtc,x.LastSeenUtc});
        m.Entity<LiveConsentRecord>().HasIndex(x=>new{x.GrantId,x.FlightId,x.EndedUtc});
        m.Entity<LiveConsentRecord>().HasIndex(x=>new{x.EndedUtc,x.StartedUtc});
        m.Entity<LiveConsentRecord>().HasIndex(x=>new{x.PilotId,x.FlightId,x.InstanceId,x.EndedUtc});
        m.Entity<LiveConsentRecord>().HasOne<DeviceGrantRecord>().WithMany().HasForeignKey(x=>x.GrantId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<LiveConsentRecord>().Property(x=>x.DisplayLabel).HasMaxLength(80);
        m.Entity<LiveConsentRecord>().Property(x=>x.RequestSha256).HasMaxLength(64);
        m.Entity<LiveConsentRecord>().Property(x=>x.LastUpdateSha256).HasMaxLength(64);
        m.Entity<LiveConsentRecord>().Property(x=>x.ClaimsJson).HasMaxLength(400);
        m.Entity<LiveConsentRecord>().Property(x=>x.EndReason).HasMaxLength(40);
        m.Entity<LivePolicyRecord>().HasKey(x=>x.Id);
        m.Entity<LivePolicyRecord>().Property(x=>x.Revision).IsConcurrencyToken();
        m.Entity<OrganizationEventRecord>().HasKey(x=>x.Sequence);
        m.Entity<OrganizationEventRecord>().HasIndex(x=>x.Id).IsUnique();
        m.Entity<OrganizationEventRecord>().HasIndex(x=>x.IdempotencyKey).IsUnique();
        m.Entity<OrganizationEventRecord>().HasIndex(x=>new{x.Visibility,x.Sequence});
        m.Entity<OrganizationEventRecord>().HasIndex(x=>new{x.ActorId,x.Sequence});
        m.Entity<OrganizationEventRecord>().HasIndex(x=>new{x.SubjectId,x.Sequence});
        m.Entity<OrganizationEventRecord>().HasIndex(x=>new{x.CampaignId,x.Sequence});
        m.Entity<OrganizationEventRecord>().Property(x=>x.IdempotencyKey).HasMaxLength(160);
        m.Entity<OrganizationEventRecord>().Property(x=>x.Authority).HasMaxLength(80);
        m.Entity<OrganizationEventRecord>().Property(x=>x.Meaning).HasMaxLength(300);
        m.Entity<OrganizationEventRecord>().Property(x=>x.RequirementKey).HasMaxLength(120);
    }
}
