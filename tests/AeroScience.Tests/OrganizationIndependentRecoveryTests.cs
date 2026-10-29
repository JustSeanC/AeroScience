using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Organization;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace AeroScience.Tests;

public sealed partial class OrganizationConnectionIntegrationTests
{
    sealed class RecoveryClock(DateTimeOffset now):TimeProvider{public override DateTimeOffset GetUtcNow()=>now;}
    [RecoveryFact]public async Task SeparatelyKeyedCopiesSurvivePrimaryRepositoryAndIdentityLossAndEnforceApprovedRetention()
    {
        await using var f=await SubmissionFixture.Create(protectedIdentity:true);
        var card=await f.Card(ObservationResearch.Template);var attempt=await f.Record(card.Prepared,2,notes:true);var draft=await f.ReviewApprove(f.Draft(attempt));using var delivery=f.Delivery();Assert.Equal(SubmissionDeliveryState.ServerReceived,(await delivery.Send(draft.Id,default)).State);
        var enrollment=await f.Server.Post("/api/account-security/enroll",new AccountEnrollment("ResearchOnly!2026Flight"));var authenticator=enrollment.GetProperty("setupKey").GetString()!;
        var enabled=await f.Server.Post("/api/account-security/enable",new AccountCode(OrganizationServerTests.AuthenticatorCode(authenticator,DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30+1)));await f.Server.Post("/api/account-security/confirm-recovery",new AccountRecoveryConfirmation(enabled.GetProperty("recoveryCodes")[0].GetString()!));
        var primary=Path.Combine(f.Root,"primary-repository");var independent=Path.Combine(Path.GetTempPath(),"aeroscience-account-loss-"+Guid.NewGuid().ToString("N"));
        string Key(string name){var path=Path.Combine(f.Root,name);File.WriteAllText(path,Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));OrganizationConfigurationTests.ProtectCredential(path);return path;}
        var primaryKey=Key("primary-key");var independentKey=Key("independent-key");var work=Path.Combine(f.Root,"copy-work");
        var options=new ServerOperationsOptions(true,primary,primaryKey,"Disposable project and independent account",work,System.Environment.GetEnvironmentVariable("AEROSCIENCE_RECOVERY_RESTIC")!,System.Environment.GetEnvironmentVariable("AEROSCIENCE_RECOVERY_PG_DUMP")!,System.Environment.GetEnvironmentVariable("AEROSCIENCE_RECOVERY_PG_RESTORE")!,System.Environment.GetEnvironmentVariable("AEROSCIENCE_ORG_TEST_DATABASE"),null,64*1024*1024,null,null){Independent=new(independent,independentKey,null,null,"auto"),Clock=new RecoveryClock(DateTimeOffset.UtcNow.AddDays(30))};
        try{
            using var scope=f.Server.App.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();var settings=f.Server.App.Services.GetRequiredService<OrganizationSettings>();var engine=new OrganizationRecoveryEngine(db,settings,options);
            async Task<ServerBackupRecord> Backup(){var b=await engine.Backup(Guid.NewGuid(),default);b.VerifiedRestoreUtc=(await engine.VerifyRestore(b,default)).VerifiedUtc;await engine.CopyIndependent(b,default);db.ServerBackups.Add(b);await db.SaveChangesAsync();return b;}
            var old=await Backup();var unverified=await Backup();var latest=await Backup();var firstCopy=latest.IndependentSnapshotId;await engine.CopyIndependent(latest,default);Assert.Equal(firstCopy,latest.IndependentSnapshotId);Assert.NotNull(latest.IndependentVerifiedUtc);Assert.NotEqual(latest.SnapshotId,latest.IndependentSnapshotId);
            unverified.RepositorySha256=null;await engine.CopyIndependent(unverified,default);Assert.NotNull(unverified.RepositorySha256); // authenticated pre-completion metadata remains recoverable
            // The only daily/weekly point and any unverified checkpoint remain protected. Explicit policy is required.
            unverified.VerifiedRestoreUtc=null;await db.SaveChangesAsync();var policy=new ServerOperationsPolicyRecord{RetainDaily=1,RetainWeekly=1,AutomaticRetention=false};Assert.Equal(0,await engine.EnforceRetention(policy,default));Assert.Null(old.DeletedUtc);policy.AutomaticRetention=true;
            var fault=Path.Combine(f.Root,"interrupt-retention");static string Quote(string value)=>"'"+value.Replace("'","'\\''",StringComparison.Ordinal)+"'";
            File.WriteAllText(fault,"#!/bin/sh\ncase \" $* \" in *' forget '*"+Quote(old.IndependentSnapshotId!)+"*) exit 77;; esac\nexec "+Quote(options.ResticExecutable)+" \"$@\"\n");if(!OperatingSystem.IsWindows())File.SetUnixFileMode(fault,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            // Kill/fail after source deletion but before independent deletion. Intent must survive and retry exactly.
            await Assert.ThrowsAsync<InvalidOperationException>(()=>new OrganizationRecoveryEngine(db,settings,options with{ResticExecutable=fault}).EnforceRetention(policy,default));Assert.NotNull(old.RetentionStartedUtc);Assert.Null(old.DeletedUtc);await Assert.ThrowsAsync<InvalidOperationException>(()=>engine.Discover(old.SnapshotId,default));
            Assert.Equal(1,await engine.EnforceRetention(policy,default));Assert.NotNull(old.DeletedUtc);Assert.Null(latest.DeletedUtc);Assert.Null(unverified.DeletedUtc);Assert.Equal(0,await engine.EnforceRetention(policy,default));
            await Assert.ThrowsAsync<InvalidOperationException>(()=>engine.Discover(old.SnapshotId,default));Assert.Equal(2,await db.Audit.CountAsync(a=>a.ResourceId==old.Id&&a.Action.StartsWith("backup-retention-")));
            // Simulated complete project loss: remove original repository, key, protected PFX and received source records.
            Directory.Delete(primary,true);File.Delete(primaryKey);File.Delete(settings.KeyCertificateFile!);await db.Database.ExecuteSqlRawAsync("TRUNCATE organization.\"Submissions\" CASCADE");
            var recovery=new OrganizationRecoveryEngine(db,settings,options with{Repository=independent,PasswordFile=independentKey,Independent=null,InheritStorageCredentials=false});
            var recovered=await recovery.Discover(latest.IndependentSnapshotId!,default);Assert.Equal(latest.BundleSha256,recovered.BundleSha256);var verified=await recovery.VerifyRestore(recovered,default);Assert.Equal(1,verified.ReceivedPackages);Assert.Equal(latest.OrganizationId,verified.OrganizationId);Assert.Empty(Directory.GetFileSystemEntries(work));
            var wrong=Key("wrong-key");await Assert.ThrowsAsync<InvalidOperationException>(()=>new OrganizationRecoveryEngine(db,settings,options with{Repository=independent,PasswordFile=wrong,Independent=null}).Discover(latest.IndependentSnapshotId!,default));
            foreach(var file in Directory.EnumerateFiles(independent,"*",SearchOption.AllDirectories))if(new FileInfo(file).Length<4*1024*1024)Assert.DoesNotContain("Selected original pilot note",Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)));
        }finally{if(Directory.Exists(independent))Directory.Delete(independent,true);}
    }
}
