using Microsoft.EntityFrameworkCore;
namespace AeroScience.Organization;

[Flags] public enum DiscordCategories { None=0, Flights=1, ApprovedResearch=2, AcceptedExceptions=4 }
public enum NotificationState { Pending, Sending, Delivered, Failed, Disabled, Ambiguous }
public sealed class DiscordDestinationRecord
{
    public int Id {get;set;}=1; public Guid PublicId {get;set;} public Guid OrganizationId {get;set;}
    public int Revision {get;set;} public string Label {get;set;}=""; public string MaskedIdentifier {get;set;}="";
    public string ProtectedCredential {get;set;}=""; public bool Enabled {get;set;} public bool Deleted {get;set;}
    public DiscordCategories Categories {get;set;} public Guid AuthorizerId {get;set;} public string AuthorizerStamp {get;set;}="";
    public DateTimeOffset NextSendUtc {get;set;} public long EventCursor {get;set;} public DateTimeOffset UpdatedUtc {get;set;}
}
public sealed class DiscordConsentRecord
{
    public Guid SessionId {get;set;} public Guid PilotId {get;set;} public Guid DestinationId {get;set;} public int DestinationRevision {get;set;}
    public int Revision {get;set;} public bool Allowed {get;set;} public DateTimeOffset UpdatedUtc {get;set;}
}
public sealed class DiscordDisclosureRecord
{
    public Guid EventId {get;set;} public Guid DestinationId {get;set;} public int DestinationRevision {get;set;}
    public string MessageSha256 {get;set;}=""; public Guid AuthorizerId {get;set;} public string AuthorizerStamp {get;set;}=""; public DateTimeOffset ApprovedUtc {get;set;}
}
public sealed class DiscordDeliveryRecord
{
    public long Sequence {get;set;} public Guid Id {get;set;} public Guid EventId {get;set;} public Guid DestinationId {get;set;}
    public int DestinationRevision {get;set;} public Guid? ConsentSessionId {get;set;} public int? ConsentRevision {get;set;}
    public bool Test {get;set;} public NotificationState State {get;set;} public int Attempts {get;set;}
    public DateTimeOffset CreatedUtc {get;set;} public DateTimeOffset DueUtc {get;set;} public DateTimeOffset? FinishedUtc {get;set;}
    public Guid? SenderInstance {get;set;} public string MessageId {get;set;}=""; public string Reason {get;set;}="";
}
public static class OrganizationNotificationDatabase
{
    public static void Configure(ModelBuilder m)
    {
        m.Entity<DiscordDestinationRecord>().HasKey(x=>x.Id);m.Entity<DiscordDestinationRecord>().HasIndex(x=>x.PublicId).IsUnique();
        m.Entity<DiscordDestinationRecord>().Property(x=>x.Revision).IsConcurrencyToken();
        m.Entity<DiscordDestinationRecord>().Property(x=>x.Label).HasMaxLength(80);m.Entity<DiscordDestinationRecord>().Property(x=>x.ProtectedCredential).HasMaxLength(4096);
        m.Entity<DiscordDestinationRecord>().Property(x=>x.MaskedIdentifier).HasMaxLength(24);m.Entity<DiscordDestinationRecord>().Property(x=>x.AuthorizerStamp).HasMaxLength(128);
        m.Entity<DiscordConsentRecord>().HasKey(x=>x.SessionId);m.Entity<DiscordConsentRecord>().Property(x=>x.Revision).IsConcurrencyToken();
        m.Entity<DiscordConsentRecord>().HasIndex(x=>new{x.DestinationId,x.DestinationRevision,x.Allowed});
        m.Entity<DiscordDisclosureRecord>().HasKey(x=>new{x.EventId,x.DestinationId,x.DestinationRevision});m.Entity<DiscordDisclosureRecord>().Property(x=>x.AuthorizerStamp).HasMaxLength(128);m.Entity<DiscordDisclosureRecord>().Property(x=>x.MessageSha256).HasMaxLength(64);
        m.Entity<DiscordDeliveryRecord>().HasKey(x=>x.Sequence);m.Entity<DiscordDeliveryRecord>().HasIndex(x=>x.Id).IsUnique();
        m.Entity<DiscordDeliveryRecord>().HasIndex(x=>new{x.EventId,x.DestinationId,x.DestinationRevision}).IsUnique();
        m.Entity<DiscordDeliveryRecord>().HasIndex(x=>new{x.State,x.DueUtc,x.Sequence});
        m.Entity<DiscordDeliveryRecord>().HasIndex(x=>new{x.DestinationId,x.Sequence});
        m.Entity<DiscordDeliveryRecord>().Property(x=>x.Reason).HasMaxLength(200);m.Entity<DiscordDeliveryRecord>().Property(x=>x.MessageId).HasMaxLength(20);
    }
}
