using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.AspNetCore.DataProtection;
namespace AeroScience.Organization;
public sealed class OrganizationOperations(OrganizationDatabase db,OrganizationSettings settings,ServerOperationsOptions options,OrganizationManagement history,OrganizationMembership membership,OrganizationAccountSecurity security,UserManager<OrganizationUser> users)
{
    internal const long OperationLock=7026001;
    internal static ServerOperationView View(ServerOperationRecord r)=>new(r.Id,r.Revision,r.Kind,r.State,r.CreatedUtc,r.FinishedUtc,r.BackupId,r.Message);
    async Task Authorize(HttpContext context,bool recent)
    {
        await membership.RequireManager(context.User,true,context.RequestAborted);var user=await users.GetUserAsync(context.User)??throw new OrganizationForbidden();
        if(!OrganizationAccountSecurity.Ready(user)||!(recent?OrganizationAccountSecurity.Recent(context.User):OrganizationAccountSecurity.Verified(context.User))||context.User.FindFirstValue(users.Options.ClaimsIdentity.SecurityStampClaimType)!=user.SecurityStamp)throw new OrganizationForbidden();
    }
    async Task MutationAuthorization(HttpContext context){await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({OrganizationMembership.MembershipLock})",context.RequestAborted);await Authorize(context,true);await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({OperationLock})",context.RequestAborted);}
    public async Task<ServerOperationsView> Status(int offset,HttpContext context)
    {
        if(offset<0)throw new ArgumentException("Choose a valid recovery history page.");var ct=context.RequestAborted;
        // A job and its backup verification are committed together. Keep their paged status reads
        // on the same MVCC snapshot so completion cannot be paired with stale backup metadata.
        await using var snapshot=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
        await Authorize(context,false);var policy=await db.ServerOperationsPolicies.AsNoTracking().SingleOrDefaultAsync(ct);
        var backups=await db.ServerBackups.AsNoTracking().OrderByDescending(r=>r.CreatedUtc).ThenBy(r=>r.Id).Skip(offset).Take(21).ToArrayAsync(ct);var jobs=await db.ServerOperations.AsNoTracking().OrderByDescending(r=>r.CreatedUtc).ThenBy(r=>r.Id).Take(20).ToArrayAsync(ct);
        var quota=context.RequestServices.GetRequiredService<OrganizationSubmissionOptions>().MaximumRetainedPayloadBytes;
        var size=await db.Database.SqlQueryRaw<long>("SELECT pg_database_size(current_database()) AS \"Value\"").SingleAsync(ct);var bytes=await db.SubmissionUsage.AsNoTracking().Select(r=>(long?)r.PayloadBytes).SingleOrDefaultAsync(ct)??0;
        long? available=null;if(settings.StateDirectory is {} state){try{available=new DriveInfo(Path.GetPathRoot(state)!).AvailableFreeSpace;}catch(IOException){}}
        string update;try{var distribution=options.Distribution();update=distribution is null?"No accepted production server update has been published/configured.":ReleaseVersionComparison.Compare(distribution.Distribution.ApplicationVersion,OrganizationDeployment.ApplicationVersion)>0?"A signed pinned update is available. Prepare a verified paired recovery checkpoint before provider rollout.":"The configured signed distribution is not newer than this application.";}catch{update="The proposed distribution could not be verified. No update is authorized.";}
        var view=new ServerOperationsView(options.Enabled,policy?.Scheduled??false,policy?.IntervalHours??24,policy?.RetainDaily??14,policy?.RetainWeekly??8,policy?.Revision??0,policy?.RecoveryKitSavedUtc,
            backups.Take(20).Select(r=>{var m=JsonSerializer.Deserialize<ServerBackupManifest>(r.ManifestJson,WireJson.Options)!;return new ServerBackupView(r.Id,r.CreatedUtc,r.Bytes,r.VerifiedRestoreUtc,m.ApplicationVersion,m.SourceRevision,r.DeletedUtc is not null?"Deleted under the approved retention policy; historical receipt only":r.RetentionStartedUtc is not null?"Deletion in progress under the approved policy; this checkpoint is unavailable for recovery":r.VerifiedRestoreUtc is null?"Protected snapshot; restoration not yet verified":"Paired restore verified against original evidence and audit digests"){IndependentVerifiedUtc=r.IndependentVerifiedUtc,DeletedUtc=r.DeletedUtc,RetentionPending=r.RetentionStartedUtc is not null&&r.DeletedUtc is null};}).ToArray(),jobs.Select(View).ToArray(),backups.Length>20?offset+20:null,new(size,bytes,quota,available,"Database logical payload quota is not a physical capacity guarantee. Available configuration space reflects this process's filesystem, not a verified provider allocation."),options.Destination,update,
            ["Backups include organization records and only already-received authorized evidence; no access to pilots' private archives.","A Railway Bucket in the same project/account is not protection against deletion of that project/account. Keep an independently controlled copy and recovery key.","Restored copies retain the original origin and claim identities. A new public address needs separately verified reconnection/migration; no historical evidence is rewritten.","Production cloud deployment, novice operation and external failure-domain acceptance are not inferred from these checks."]){IndependentConfigured=options.Independent is not null,AutomaticRetention=policy?.AutomaticRetention??false,RestoreCandidate=OrganizationRecoveryActivation.View(settings)};
        await snapshot.CommitAsync(ct);return view with{RestoreSupported=settings.StateDirectory is not null};
    }
    public async Task<object> Policy(ServerOperationsPolicyCommand r,HttpContext context)
    {
        if(!options.Enabled)throw new OrganizationConflict("Protected recovery storage is not connected. The hosting-account owner must complete the supported deployment configuration.");
        if(r.RequestId==Guid.Empty||!r.ConfirmOrganizationalBackup||r.IntervalHours is <1 or >168||r.RetainDaily is <1 or >90||r.RetainWeekly is <1 or >52)throw new ArgumentException("Approve the disclosed organizational backup policy, with a 1–168 hour interval and bounded daily/weekly retention.");
        await security.VerifyPassword(new(r.Password),context);
        return await history.Mutate(context.User,r.RequestId,new{operation="server-backup-policy",r.ExpectedRevision,r.Scheduled,r.IntervalHours,r.RetainDaily,r.RetainWeekly,r.ConfirmOrganizationalBackup,r.AutomaticRetention},async(actor,name)=>{
            var row=await db.ServerOperationsPolicies.SingleOrDefaultAsync(context.RequestAborted);if((row?.Revision??0)!=r.ExpectedRevision)throw new OrganizationConflict("Backup settings changed. Refresh before saving.");
            if(row is null){row=new();db.ServerOperationsPolicies.Add(row);}else row.Revision++;
            if(r.AutomaticRetention&&options.Independent is null)throw new OrganizationConflict("Automatic deletion requires a separately configured and verified independent recovery destination.");
            row.AutomaticRetention=r.AutomaticRetention;row.Scheduled=r.Scheduled;row.IntervalHours=r.IntervalHours;row.RetainDaily=r.RetainDaily;row.RetainWeekly=r.RetainWeekly;row.ApprovedBy=actor;row.ApprovedUtc=DateTimeOffset.UtcNow;history.Audit(actor,name,"server-backup-policy-approved",(await db.Organizations.SingleAsync()).PublicId,row.Revision);
            return new{revision=row.Revision,message="Backup policy saved. The recovery destination and independent restore still require verification; no pilot archive is accessed."};
        },context.RequestAborted,()=>MutationAuthorization(context));
    }
    public async Task<ServerOperationView> Command(ServerOperationCommand r,HttpContext context)
    {
        if(!options.Enabled)throw new OrganizationConflict("Protected backup storage is not connected. No recovery operation was started.");
        if(r.RequestId==Guid.Empty||!Enum.IsDefined(r.Kind)||!r.Confirm)throw new ArgumentException("Review and explicitly approve this recovery operation.");await security.VerifyPassword(new(r.Password),context);
        if(r.Kind==ServerOperationKind.ImportRestore)throw new ArgumentException("Use Restore From Private Recovery File to review and approve independent recovery.");
        return await history.Mutate(context.User,r.RequestId,new{operation="server-operation",r.Kind,r.BackupId,r.Confirm},async(actor,name)=>{
            if(!await db.ServerOperationsPolicies.AnyAsync(context.RequestAborted))throw new OrganizationConflict("Approve the organizational backup policy first.");
            if(await db.ServerOperations.AnyAsync(j=>j.State==ServerOperationState.Pending||j.State==ServerOperationState.Running,context.RequestAborted))throw new OrganizationConflict("Another recovery operation is active. Its history is retained; wait for its result.");
            if(r.Kind is ServerOperationKind.VerifyRestore or ServerOperationKind.StageRestore&&(r.BackupId is null||!await db.ServerBackups.AnyAsync(b=>b.Id==r.BackupId&&b.DeletedUtc==null&&b.RetentionStartedUtc==null,context.RequestAborted)))throw new ArgumentException("Choose an existing protected backup.");
            if(r.Kind is ServerOperationKind.StageRestore or ServerOperationKind.RenewIdentity&&(settings.StateDirectory is null||OrganizationRecoveryActivation.Read(settings,false) is not null))throw new OrganizationConflict("A private persistent configuration volume and an empty replacement slot are required. Review the existing replacement first.");
            SignedServerDistribution? distribution=null;
            if(r.Kind==ServerOperationKind.PrepareUpdate){distribution=options.Distribution()??throw new OrganizationConflict("There is no verified pinned production update to prepare.");if(ReleaseVersionComparison.Compare(distribution.Distribution.ApplicationVersion,OrganizationDeployment.ApplicationVersion)<=0||ReleaseVersionComparison.Compare(OrganizationDeployment.ApplicationVersion,distribution.Distribution.MinimumVersion)<0)throw new OrganizationConflict("This signed update is not compatible with the running version.");var hash=OrganizationDeployment.Digest(JsonSerializer.SerializeToUtf8Bytes(db.Database.GetMigrations().ToArray(),WireJson.Options));if(distribution.Distribution.SchemaSha256!=hash)throw new OrganizationConflict("This update does not accept the exact current database schema. No migration or rollout was attempted.");}
            var job=new ServerOperationRecord{Id=Guid.NewGuid(),ActorId=actor,ActorName=name,RequestId=r.RequestId,Kind=r.Kind,BackupId=r.BackupId,State=ServerOperationState.Pending,CreatedUtc=DateTimeOffset.UtcNow,Message="Approved — waiting for the protected operations worker.",DistributionJson=distribution is null?null:JsonSerializer.Serialize(distribution,WireJson.Options)};db.ServerOperations.Add(job);history.Audit(actor,name,"server-operation-approved",job.Id);return View(job);
        },context.RequestAborted,()=>MutationAuthorization(context));
    }
    public async Task<OrganizationRecoveryKit> RecoveryKit(AccountPassword r,HttpContext context)
    {
        await Authorize(context,true);await security.VerifyPassword(r,context);if(!options.Enabled)throw new OrganizationConflict("Recovery storage is not configured.");
        var org=await db.Organizations.SingleAsync(context.RequestAborted);var credentials=options.StorageCredentials();if(options.RepositoryCredentialsFile is null)foreach(var name in RecoveryRepositoryTarget.CredentialNames)if(Environment.GetEnvironmentVariable(name) is {} value)credentials[name]=value;var password=File.ReadAllText(OrganizationSettings.PrivateFile(options.PasswordFile!,32,256).FullName).Trim();
        history.Audit(OrganizationMembership.Actor(context.User),context.User.Identity!.Name!,"recovery-key-export-approved",org.PublicId);await db.SaveChangesAsync(context.RequestAborted);
        var repositoryHash=OrganizationDeployment.Digest(Encoding.UTF8.GetBytes(options.Repository!));var independentHash=options.Independent is {} target?OrganizationDeployment.Digest(Encoding.UTF8.GetBytes(target.Repository)):null;
        var checkpoint=independentHash is null?null:await db.ServerBackups.AsNoTracking().Where(b=>b.RepositorySha256==repositoryHash&&b.IndependentRepositorySha256==independentHash&&b.IndependentVerifiedUtc!=null&&b.IndependentSnapshotId!=null&&b.DeletedUtc==null&&b.RetentionStartedUtc==null).OrderByDescending(b=>b.CreatedUtc).FirstOrDefaultAsync(context.RequestAborted);
        return new(1,org.PublicId,settings.Origin.GetLeftPart(UriPartial.Authority),options.Repository!,password,credentials.GetValueOrDefault("AWS_ACCESS_KEY_ID"),credentials.GetValueOrDefault("AWS_SECRET_ACCESS_KEY"),credentials.GetValueOrDefault("AWS_DEFAULT_REGION"),"Keep this key independently of the server and provider account. It unlocks sensitive organization backups. Provider storage access credentials and an accepted recovery deployment are also required. Same-account bucket deletion can still destroy the only remote copy."){S3BucketLookup=options.S3BucketLookup,Independent=options.Independent is {} independent?new(independent.Repository,File.ReadAllText(OrganizationSettings.PrivateFile(independent.PasswordFile,32,256).FullName).Trim(),independent.Credentials(),independent.S3BucketLookup):null,RecoveryPoint=checkpoint is null?null:new(checkpoint.Id,checkpoint.OrganizationId,checkpoint.IndependentSnapshotId!,checkpoint.BundleSha256,checkpoint.CreatedUtc),RecoveryAdministrator=context.User.Identity!.Name};
    }
    public async Task<ServerOperationView> ImportRestore(ImportRecoveryRequest r,HttpContext context)
    {
        if(!options.Enabled||settings.StateDirectory is null||r.RequestId==Guid.Empty||!r.Confirm)throw new OrganizationConflict("A protected deployment and explicit recovery approval are required. No account or paid storage was created.");
        OrganizationRecoveryImport.Validate(r.Kit,settings,r.ConfirmAddressChange);await Authorize(context,true);await security.VerifyPassword(new(r.Password),context);
        var kitHash=OrganizationDeployment.Digest(JsonSerializer.SerializeToUtf8Bytes(r.Kit,WireJson.Options));
        return await history.Mutate(context.User,r.RequestId,new{operation="import-independent-recovery",kitHash,r.Confirm,r.ConfirmAddressChange,r.UseLatestRecoveryPoint},async(actor,name)=>{
            if(OrganizationRecoveryActivation.Read(settings,false) is not null||await db.ServerOperations.AnyAsync(j=>j.State==ServerOperationState.Pending||j.State==ServerOperationState.Running,context.RequestAborted))throw new OrganizationConflict("Finish or cancel the existing recovery operation first.");
            var organization=await db.Organizations.SingleAsync(context.RequestAborted);
            if(organization.PublicId!=r.Kit.OrganizationId&&(await db.Users.CountAsync(context.RequestAborted)!=1||await db.Programs.AnyAsync(context.RequestAborted)||await db.Claims.AnyAsync(context.RequestAborted)||await db.Submissions.AnyAsync(context.RequestAborted)))throw new OrganizationConflict("Restore a different organization only into a fresh recovery deployment, before inviting pilots or creating research.");
            var job=new ServerOperationRecord{Id=Guid.NewGuid(),ActorId=actor,ActorName=name,RequestId=r.RequestId,Kind=ServerOperationKind.ImportRestore,State=ServerOperationState.Pending,CreatedUtc=DateTimeOffset.UtcNow,Message="Approved independent recovery — waiting for verification. No live database was replaced."};
            job.ProtectedImportJson=OrganizationRecoveryImport.Protector(context.RequestServices.GetRequiredService<IDataProtectionProvider>(),job.Id,actor).Protect(JsonSerializer.Serialize(new ProtectedRecoveryImport(r.Kit,r.UseLatestRecoveryPoint),WireJson.Options));db.ServerOperations.Add(job);history.Audit(actor,name,"independent-recovery-import-approved",job.Id);return View(job);
        },context.RequestAborted,()=>MutationAuthorization(context));
    }
    public async Task<object> Activate(ActivateRecoveryPair r,HttpContext context)
    {
        if(!r.Confirm||r.CandidateId==Guid.Empty)throw new ArgumentException("Review and explicitly confirm the replacement before activating it.");
        await Authorize(context,true);await security.VerifyPassword(new(r.Password),context);
        var runtime=context.RequestServices.GetRequiredService<OrganizationMaintenance>();if(!runtime.Enter())throw new OrganizationConflict("Another activation is in progress.");
        try{
            var candidate=OrganizationRecoveryActivation.Read(settings,false)??throw new OrganizationConflict("No verified replacement is ready.");
            if(!await db.ServerOperations.AnyAsync(j=>(j.Kind==ServerOperationKind.StageRestore||j.Kind==ServerOperationKind.RenewIdentity||j.Kind==ServerOperationKind.ImportRestore)&&j.BackupId==candidate.BackupId&&j.State==ServerOperationState.Completed,context.RequestAborted))throw new OrganizationConflict("The approved restore has not completed.");
            await OrganizationRecoveryActivation.Activate(settings,r.CandidateId,context.RequestAborted);
            context.Response.OnCompleted(()=>{if(!settings.LocalDevelopment)Environment.ExitCode=75;context.RequestServices.GetRequiredService<IHostApplicationLifetime>().StopApplication();return Task.CompletedTask;});
            return new{state="Restarting",message="Verified replacement selected. Sign in again after restart. Device connections, notifications and live sharing require fresh authorization. The original database was not deleted."};
        }catch{runtime.Exit();throw;}
    }
    public async Task<object> CancelRestore(ActivateRecoveryPair r,HttpContext context)
    {
        if(!r.Confirm||r.CandidateId==Guid.Empty)throw new ArgumentException("Confirm cancellation of the exact restored copy.");await Authorize(context,true);await security.VerifyPassword(new(r.Password),context);
        var runtime=context.RequestServices.GetRequiredService<OrganizationMaintenance>();if(!runtime.Enter())throw new OrganizationConflict("An activation is in progress.");
        try{
            var candidate=OrganizationRecoveryActivation.Read(settings,false)??throw new OrganizationConflict("No replacement is waiting.");
            if(candidate.Id!=r.CandidateId||new NpgsqlConnectionStringBuilder(settings.ConnectionString).Database==candidate.Database)throw new OrganizationConflict("This replacement changed or is already active.");
            OrganizationRecoveryEngine.ClearCandidatePools(settings.ConnectionString,options.RecoveryConnectionString??throw new OrganizationConflict("Protected recovery access is unavailable."),candidate.Database);
            await using var admin=new NpgsqlConnection(options.RecoveryConnectionString??throw new OrganizationConflict("Protected recovery access is unavailable."));await admin.OpenAsync(context.RequestAborted);var applicationRole=new NpgsqlConnectionStringBuilder(settings.ConnectionString).Username!;await using(var role=new NpgsqlCommand("SET ROLE \""+applicationRole.Replace("\"","\"\"")+"\"",admin))await role.ExecuteNonQueryAsync(context.RequestAborted);await using var command=new NpgsqlCommand("DROP DATABASE IF EXISTS \""+candidate.Database+"\" WITH (FORCE)",admin);await command.ExecuteNonQueryAsync(context.RequestAborted);
            File.Delete(OrganizationRecoveryActivation.PathFor(settings,false));File.Delete(Path.Combine(settings.StateDirectory!,candidate.CertificateFile));
            history.Audit(OrganizationMembership.Actor(context.User),context.User.Identity!.Name!,"server-recovery-candidate-cancelled",candidate.BackupId);await db.SaveChangesAsync(context.RequestAborted);return new{message="The unactivated replacement was deleted. The live database and encrypted recovery checkpoints were not changed."};
        }finally{runtime.Exit();}
    }
    internal async Task<ServerOperationRecord?> Take(CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(ct);await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({OperationLock})",ct);
        // A killed worker leaves a visible failed operation; it never claims an unverified partial restore succeeded.
        var stale=await db.ServerOperations.Where(j=>j.State==ServerOperationState.Running&&j.StartedUtc<DateTimeOffset.UtcNow.AddMinutes(-20)).OrderBy(j=>j.CreatedUtc).Take(20).ToArrayAsync(ct);
        foreach(var job in stale){job.State=ServerOperationState.Failed;job.ProtectedImportJson=null;job.FinishedUtc=DateTimeOffset.UtcNow;job.Revision++;job.Message="The previous worker was interrupted. Originals remain unchanged; retry explicitly using the retained checkpoint.";}
        var next=await db.ServerOperations.Where(j=>j.State==ServerOperationState.Pending).OrderBy(j=>j.CreatedUtc).FirstOrDefaultAsync(ct);
        if(next is not null){
            var authorized=next.ActorId==Guid.Empty||await db.Memberships.AnyAsync(m=>m.UserId==next.ActorId&&m.Role=="Administrator"&&m.State==OrganizationMembershipState.Active,ct)&&await db.Users.AnyAsync(u=>u.Id==next.ActorId&&u.TwoFactorEnabled&&u.RecoveryAcknowledgedUtc!=null,ct);
            next.State=authorized?ServerOperationState.Running:ServerOperationState.Cancelled;next.Revision++;next.StartedUtc=DateTimeOffset.UtcNow;next.Message=authorized?"Protected recovery operation is running.":"Authorization was revoked before execution; no transfer was started.";
            if(!authorized)next.ProtectedImportJson=null;
        }
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return next?.State==ServerOperationState.Running?next:null;
    }
    internal async Task Schedule(CancellationToken ct)
    {
        if(!options.Enabled)return;await using var tx=await db.Database.BeginTransactionAsync(ct);await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({OperationLock})",ct);
        var policy=await db.ServerOperationsPolicies.AsNoTracking().SingleOrDefaultAsync(ct);
        if(policy?.Scheduled==true&&!await db.ServerOperations.AnyAsync(j=>j.State==ServerOperationState.Pending||j.State==ServerOperationState.Running,ct)){
            var last=await db.ServerOperations.Where(j=>j.Kind==ServerOperationKind.Backup).Select(j=>(DateTimeOffset?)j.CreatedUtc).MaxAsync(ct);
            if(last is null||last<DateTimeOffset.UtcNow.AddHours(-policy.IntervalHours))db.ServerOperations.Add(new(){Id=Guid.NewGuid(),RequestId=Guid.NewGuid(),ActorId=Guid.Empty,ActorName="Approved backup schedule",Kind=ServerOperationKind.Backup,State=ServerOperationState.Pending,CreatedUtc=DateTimeOffset.UtcNow,Message="Scheduled under the administrator-approved organizational backup policy."});
        }
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
    }
}
public static class OrganizationOperationsApi
{
    public static void Map(WebApplication app)
    {
        var api=app.MapGroup("/api/organization/operations").RequireAuthorization(OrganizationApplication.PublishPolicy);
        api.MapGet("",(int? offset,OrganizationOperations operations,HttpContext c)=>operations.Status(offset??0,c));
        api.MapPost("/activate-restore",(ActivateRecoveryPair r,OrganizationOperations operations,HttpContext c)=>operations.Activate(r,c));
        api.MapPost("/cancel-restore",(ActivateRecoveryPair r,OrganizationOperations operations,HttpContext c)=>operations.CancelRestore(r,c));
        api.MapPost("/import-restore",(ImportRecoveryRequest r,OrganizationOperations operations,HttpContext c)=>operations.ImportRestore(r,c)).RequireRateLimiting("identity");
        api.MapPost("/policy",(ServerOperationsPolicyCommand r,OrganizationOperations operations,HttpContext c)=>operations.Policy(r,c));
        api.MapPost("/commands",(ServerOperationCommand r,OrganizationOperations operations,HttpContext c)=>operations.Command(r,c)).RequireRateLimiting("identity");
        api.MapPost("/recovery-key",(AccountPassword r,OrganizationOperations operations,HttpContext c)=>operations.RecoveryKit(r,c)).RequireRateLimiting("identity");
    }
}
