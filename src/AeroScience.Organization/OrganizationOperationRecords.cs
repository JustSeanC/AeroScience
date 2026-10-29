using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
namespace AeroScience.Organization;
public enum ServerOperationKind { Backup, VerifyRestore, PrepareUpdate }
public enum ServerOperationState { Pending, Running, Completed, Failed, Cancelled }
public sealed class ServerOperationRecord
{
    public Guid Id{get;set;}public int Revision{get;set;}=1;public ServerOperationKind Kind{get;set;}public ServerOperationState State{get;set;}
    public Guid ActorId{get;set;}public Guid RequestId{get;set;}public string RequestSha256{get;set;}="";public string ActorName{get;set;}="";
    public Guid? BackupId{get;set;}public DateTimeOffset CreatedUtc{get;set;}public DateTimeOffset? StartedUtc{get;set;}public DateTimeOffset? FinishedUtc{get;set;}
    public string Message{get;set;}="";public string? DistributionJson{get;set;}
}
public sealed class ServerBackupRecord
{
    public Guid Id{get;set;}public Guid OrganizationId{get;set;}public string SnapshotId{get;set;}="";public string BundleSha256{get;set;}="";public long Bytes{get;set;}
    public string ManifestJson{get;set;}="";public DateTimeOffset CreatedUtc{get;set;}public DateTimeOffset? VerifiedRestoreUtc{get;set;}public string? VerificationJson{get;set;}
}
public sealed class ServerOperationsPolicyRecord
{
    public int Id{get;set;}=1;public int Revision{get;set;}=1;public bool Scheduled{get;set;}public int IntervalHours{get;set;}=24;
    public int RetainDaily{get;set;}=14;public int RetainWeekly{get;set;}=8;public Guid ApprovedBy{get;set;}public DateTimeOffset ApprovedUtc{get;set;}public DateTimeOffset? RecoveryKitSavedUtc{get;set;}
}
public sealed record ServerOperationsPolicyCommand(Guid RequestId,int ExpectedRevision,bool Scheduled,int IntervalHours,int RetainDaily,int RetainWeekly,bool ConfirmOrganizationalBackup,string Password);
public sealed record ServerOperationCommand(Guid RequestId,ServerOperationKind Kind,Guid? BackupId,string Password,bool Confirm);
public sealed record ServerOperationView(Guid Id,int Revision,ServerOperationKind Kind,ServerOperationState State,DateTimeOffset CreatedUtc,DateTimeOffset? FinishedUtc,Guid? BackupId,string Message);
public sealed record ServerBackupView(Guid Id,DateTimeOffset CreatedUtc,long Bytes,DateTimeOffset? VerifiedRestoreUtc,string Version,string SourceRevision,string IntegrityStatus);
public sealed record ServerStorageUsage(long DatabaseBytes,long ReceivedEvidenceBytes,long EvidenceQuotaBytes,long? ConfigurationAvailableBytes,string Limitation);
public sealed record ServerOperationsView(bool Configured,bool Scheduled,int IntervalHours,int RetainDaily,int RetainWeekly,int PolicyRevision,DateTimeOffset? RecoveryKitSavedUtc,ServerBackupView[] Backups,ServerOperationView[] Operations,int? NextOffset,ServerStorageUsage Storage,string Destination,string UpdateState,string[] Warnings);
public sealed record ServerBackupManifest(int Version,Guid BackupId,Guid OrganizationId,string PublicOrigin,string ApplicationVersion,string SourceRevision,string[] Migrations,string CertificateSha256,string DatabaseSha256,long DatabaseBytes,string EvidenceDigest,long ReceivedPackages,string AuditDigest,long AuditEvents,DateTimeOffset CreatedUtc);
public sealed record ServerRestoreVerification(int Version,Guid BackupId,Guid OrganizationId,string DatabaseSha256,string CertificateSha256,string EvidenceDigest,long ReceivedPackages,string AuditDigest,long AuditEvents,string[] Migrations,DateTimeOffset VerifiedUtc,string Scope);
public sealed record ServerDistribution(int Version,string ApplicationVersion,string SourceRevision,string Image,string MinimumVersion,string SchemaSha256,bool PairedRestoreRequired);
public sealed record SignedServerDistribution(ServerDistribution Distribution,string Signature);
public sealed record OrganizationRecoveryKit(int Version,Guid OrganizationId,string Origin,string Repository,string RepositoryPassword,string? AccessKeyId,string? SecretAccessKey,string? Region,string Instructions);
public static class ServerOperationsDatabase
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<ServerOperationRecord>().HasKey(r=>r.Id);model.Entity<ServerOperationRecord>().Property(r=>r.Revision).IsConcurrencyToken();model.Entity<ServerOperationRecord>().HasIndex(r=>new{r.ActorId,r.RequestId}).IsUnique();model.Entity<ServerOperationRecord>().HasIndex(r=>new{r.State,r.CreatedUtc});
        model.Entity<ServerBackupRecord>().HasKey(r=>r.Id);model.Entity<ServerBackupRecord>().HasIndex(r=>r.SnapshotId).IsUnique();model.Entity<ServerBackupRecord>().HasIndex(r=>r.CreatedUtc);
        model.Entity<ServerOperationsPolicyRecord>().HasKey(r=>r.Id);model.Entity<ServerOperationsPolicyRecord>().Property(r=>r.Revision).IsConcurrencyToken();
    }
}
