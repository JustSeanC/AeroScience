using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
namespace AeroScience.Organization;

public sealed partial class OrganizationRecoveryEngine
{
    internal OrganizationRecoveryEngine IndependentEngine()
    {
        var target=options.Independent??throw new InvalidOperationException("An independently controlled recovery destination is not configured.");
        if(target.Repository==options.Repository||File.ReadAllText(OrganizationSettings.PrivateFile(target.PasswordFile,32,256).FullName).Trim()==File.ReadAllText(OrganizationSettings.PrivateFile(options.PasswordFile!,32,256).FullName).Trim())throw new InvalidOperationException("Independent copies need separate storage and encryption keys.");
        return new(db,settings,options with{Repository=target.Repository,PasswordFile=target.PasswordFile,RepositoryCredentialsFile=target.CredentialsFile,RepositoryCaFile=target.RepositoryCaFile,S3BucketLookup=target.S3BucketLookup,Independent=null,InheritStorageCredentials=false});
    }
    public async Task CopyIndependent(ServerBackupRecord backup,CancellationToken ct)
    {
        if(backup.DeletedUtc is not null||backup.RetentionStartedUtc is not null)throw new InvalidOperationException("This recovery point has entered approved deletion; it cannot be recertified.");
        var independentHash=Hash(Encoding.UTF8.GetBytes(options.Independent!.Repository));
        if(backup.RepositorySha256 is null){var legacy=await Discover(backup.SnapshotId,ct);if(legacy.BundleSha256!=backup.BundleSha256||legacy.ManifestJson!=backup.ManifestJson)throw new InvalidOperationException("The legacy checkpoint cannot be authenticated; no destination is reassigned.");backup.RepositorySha256=Hash(Encoding.UTF8.GetBytes(options.Repository!));}
        if(backup.RepositorySha256!=Hash(Encoding.UTF8.GetBytes(options.Repository!))||(backup.IndependentRepositorySha256 is {} previous&&previous!=independentHash))throw new InvalidOperationException("Recovery destination changed. A new explicit migration and verification is required; history is not reassigned.");
        var other=IndependentEngine();await other.InitializeRepository(ct);
        var tag="original-"+backup.SnapshotId;var host="aeroscience-"+backup.OrganizationId;
        using var inventory=JsonDocument.Parse(await other.Restic(ct,"snapshots","--host",host,"--tag",tag,"--json"));
        if(inventory.RootElement.GetArrayLength()>1)throw new InvalidOperationException("Ambiguous independent checkpoint identity; no copy is silently selected.");
        string snapshot;
        if(inventory.RootElement.GetArrayLength()==1)snapshot=inventory.RootElement[0].GetProperty("id").GetString()!;
        else{
            // Two native restic processes use separately scoped credentials. The bounded plaintext staging
            // file exists only in private ephemeral work and is always removed, including failed transfers.
            // Native restic copy cannot independently scope S3 credentials for both ends in one process.
            var directory=Path.Combine(options.WorkDirectory,"backup-"+Guid.NewGuid().ToString("N"));MakePrivate(directory);
            try{
                var path=Path.Combine(directory,"organization.aeroscience");await Download(backup,path,ct);
                // Match snapshot time to ORIGINAL checkpoint UTC, not copy/retry time.
                // This permits bounded newest-point lookup without treating an old copy
                // retried today as newer scientific history.
                var output=await other.BackupFile(path,host,[tag,"aeroscience-independent-v2","backup-"+backup.Id.ToString("D"),"bundle-"+backup.BundleSha256,"checkpoint-"+backup.CreatedUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)],ct,backup.CreatedUtc);
                snapshot="";foreach(var line in output.Split('\n',StringSplitOptions.RemoveEmptyEntries)){using var doc=JsonDocument.Parse(line);if(doc.RootElement.TryGetProperty("snapshot_id",out var id))snapshot=id.GetString()!;}
            }finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
        }
        if(snapshot.Length!=64||snapshot.Any(c=>!char.IsAsciiHexDigit(c)))throw new InvalidOperationException("The independent repository did not return an exact snapshot identity.");
        var copy=new ServerBackupRecord{Id=backup.Id,OrganizationId=backup.OrganizationId,SnapshotId=snapshot,ManifestJson=backup.ManifestJson,BundleSha256=backup.BundleSha256,Bytes=backup.Bytes,CreatedUtc=backup.CreatedUtc};
        var verification=await other.VerifyRestore(copy,ct);
        backup.IndependentRepositorySha256=independentHash;backup.IndependentSnapshotId=snapshot;backup.IndependentVerifiedUtc=verification.VerifiedUtc;
    }
    internal async Task<RecoveryPointReference> LatestIndependentPoint(Guid organization,CancellationToken ct)
    {
        var host="aeroscience-"+organization;
        using var document=JsonDocument.Parse(await Restic(ct,"snapshots","--host",host,"--tag","aeroscience-independent-v2","--latest","2","--group-by","host","--json"));
        var groups=document.RootElement;if(groups.ValueKind!=JsonValueKind.Array||groups.GetArrayLength()!=1)throw new InvalidOperationException("No unique recent guided recovery group is available. Choose the file's exact checkpoint instead.");
        var snapshots=groups[0].GetProperty("snapshots");if(snapshots.GetArrayLength() is <1 or >2)throw new InvalidOperationException("The recent recovery inventory is unsupported.");
        var points=new List<RecoveryPointReference>();
        foreach(var snapshot in snapshots.EnumerateArray()){
            if(snapshot.GetProperty("hostname").GetString()!=host)throw new InvalidOperationException("Recovery inventory organization mismatch.");
            var tags=snapshot.GetProperty("tags").EnumerateArray().Select(t=>t.GetString()??"").ToArray();if(tags.Length>16)throw new InvalidOperationException("Recovery tags exceed the bounded format.");
            string Tag(string prefix){var matches=tags.Where(t=>t.StartsWith(prefix,StringComparison.Ordinal)).ToArray();if(matches.Length!=1)throw new InvalidOperationException("Recovery identity tags are ambiguous.");return matches[0][prefix.Length..];}
            if(!Guid.TryParse(Tag("backup-"),out var backup)||backup==Guid.Empty||!long.TryParse(Tag("checkpoint-"),System.Globalization.NumberStyles.None,CultureInfo.InvariantCulture,out var milliseconds))throw new InvalidOperationException("Recovery checkpoint identity is invalid.");
            var hash=Tag("bundle-");var id=snapshot.GetProperty("id").GetString()!;var time=DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);var snapshotTime=snapshot.GetProperty("time").GetDateTimeOffset();
            if(hash.Length!=64||id.Length!=64||hash.Any(c=>!char.IsAsciiHexDigitLower(c))||id.Any(c=>!char.IsAsciiHexDigitLower(c))||time.ToUnixTimeSeconds()!=snapshotTime.ToUnixTimeSeconds())throw new InvalidOperationException("Recovery snapshot time or integrity identity does not match its original checkpoint.");
            points.Add(new(backup,organization,id,hash,time));
        }
        if(points.Count==2&&points[0].CreatedUtc.ToUnixTimeSeconds()==points[1].CreatedUtc.ToUnixTimeSeconds())throw new InvalidOperationException("Recent checkpoints share an ambiguous snapshot time. Choose an exact saved checkpoint; no older point was silently selected.");
        return points.OrderByDescending(p=>p.CreatedUtc).First();
    }
    public async Task<int> EnforceRetention(ServerOperationsPolicyRecord policy,CancellationToken ct)
    {
        if(!policy.AutomaticRetention)return 0;
        if(policy.RetainDaily is <1 or >90||policy.RetainWeekly is <1 or >52)throw new InvalidOperationException("Retention policy is outside its supported bounds.");
        var other=IndependentEngine();var org=await db.Organizations.AsNoTracking().SingleAsync(ct);var now=options.Clock.GetUtcNow();
        // Stream indexed metadata in bounded pages; never materialize scientific payloads or all history.
        async IAsyncEnumerable<ServerBackupRecord> Metadata(){
            DateTimeOffset? cursor=null;Guid id=Guid.Empty;
            while(true){var query=db.ServerBackups.AsNoTracking().Where(b=>b.OrganizationId==org.PublicId&&b.DeletedUtc==null);
                if(cursor is {} time)query=query.Where(b=>b.CreatedUtc<time||(b.CreatedUtc==time&&b.Id.CompareTo(id)>0));
                var page=await query.OrderByDescending(b=>b.CreatedUtc).ThenBy(b=>b.Id).Take(100).Select(b=>new ServerBackupRecord{Id=b.Id,CreatedUtc=b.CreatedUtc,VerifiedRestoreUtc=b.VerifiedRestoreUtc,IndependentVerifiedUtc=b.IndependentVerifiedUtc,RetentionStartedUtc=b.RetentionStartedUtc}).ToArrayAsync(ct);
                foreach(var item in page)yield return item;if(page.Length<100)break;cursor=page[^1].CreatedUtc;id=page[^1].Id;
            }
        }
        // Keep only the bounded selected daily/weekly IDs. Unverified rows are excluded in the candidate query.
        var retained=new HashSet<Guid>();var days=new HashSet<DateOnly>();var weeks=new HashSet<(int,int)>();Guid? safetyId=null;
        await foreach(var b in Metadata()){
            if(b.RetentionStartedUtc is not null||b.VerifiedRestoreUtc is null||b.IndependentVerifiedUtc is null)continue;
            var utc=b.CreatedUtc.UtcDateTime;
            if(safetyId is null){retained.Add(b.Id);safetyId=b.Id;}
            if(days.Count<policy.RetainDaily&&days.Add(DateOnly.FromDateTime(utc)))retained.Add(b.Id);
            if(weeks.Count<policy.RetainWeekly&&weeks.Add((ISOWeek.GetYear(utc),ISOWeek.GetWeekOfYear(utc))))retained.Add(b.Id);
        }
        if(safetyId is null)throw new InvalidOperationException("Retention is held until an independently restored checkpoint exists.");
        var cutoff=now.AddHours(-24);
        var candidates=await db.ServerBackups.Where(b=>b.OrganizationId==org.PublicId&&b.DeletedUtc==null&&b.CreatedUtc<cutoff&&b.VerifiedRestoreUtc!=null&&b.IndependentVerifiedUtc!=null&&!retained.Contains(b.Id)).OrderBy(b=>b.CreatedUtc).ThenBy(b=>b.Id).Take(20).ToArrayAsync(ct);
        if(candidates.Length==0)return 0;
        var safety=await db.ServerBackups.AsNoTracking().SingleAsync(b=>b.Id==safetyId,ct);
        if(safety.RepositorySha256!=Hash(Encoding.UTF8.GetBytes(options.Repository!))||safety.IndependentRepositorySha256!=Hash(Encoding.UTF8.GetBytes(options.Independent!.Repository))||safety.IndependentSnapshotId is null)throw new InvalidOperationException("Retention held: the protected survivor no longer belongs to the configured repositories.");
        // A historical verification timestamp does not prove that the remaining copies still exist.
        // Authenticate the exact protected survivor in both repositories before deleting any older point.
        var originalSurvivor=await Discover(safety.SnapshotId,ct);var independentSurvivor=await other.Discover(safety.IndependentSnapshotId,ct);
        if(originalSurvivor.BundleSha256!=safety.BundleSha256||independentSurvivor.BundleSha256!=safety.BundleSha256||originalSurvivor.ManifestJson!=safety.ManifestJson||independentSurvivor.ManifestJson!=safety.ManifestJson)throw new InvalidOperationException("Retention held: the protected survivor is missing, altered or cannot be authenticated.");
        foreach(var backup in candidates){
            if(backup.RepositorySha256!=Hash(Encoding.UTF8.GetBytes(options.Repository!))||backup.IndependentRepositorySha256!=Hash(Encoding.UTF8.GetBytes(options.Independent!.Repository)))throw new InvalidOperationException("Retention destination no longer matches this checkpoint. Deletion is held.");
            if(backup.IndependentSnapshotId is not {Length:64}||backup.SnapshotId.Length!=64)throw new InvalidOperationException("Retention requires exact source and independent identities.");
            if(backup.RetentionStartedUtc is null){
                // Reauthenticate both exact packages before persisting a restart-safe deletion intent.
                var original=await Discover(backup.SnapshotId,ct);var copy=await other.Discover(backup.IndependentSnapshotId,ct);
                if(original.BundleSha256!=backup.BundleSha256||copy.BundleSha256!=backup.BundleSha256||original.ManifestJson!=backup.ManifestJson||copy.ManifestJson!=backup.ManifestJson)throw new InvalidOperationException("Retention held: a recovery source no longer matches its verified checkpoint.");
                backup.RetentionStartedUtc=now;db.Audit.Add(new(){TimestampUtc=now,ActorId=policy.ApprovedBy,ActorName="Approved retention policy",Action="backup-retention-started",ResourceId=backup.Id,Revision=policy.Revision});await db.SaveChangesAsync(ct);
            }
            // Exact IDs only. Missing IDs after an interrupted approved deletion are safe to retry.
            _=await Restic(ct,"forget",backup.SnapshotId);_=await other.Restic(ct,"forget",backup.IndependentSnapshotId);
            backup.DeletedUtc=DateTimeOffset.UtcNow;db.Audit.Add(new(){TimestampUtc=backup.DeletedUtc.Value,ActorId=policy.ApprovedBy,ActorName="Approved retention policy",Action="backup-retention-completed",ResourceId=backup.Id,Revision=policy.Revision});await db.SaveChangesAsync(ct);
        }
        if(candidates.Length>0){_=await Restic(ct,"prune","--max-repack-size","64M");_=await other.Restic(ct,"prune","--max-repack-size","64M");}
        return candidates.Length;
    }
}
