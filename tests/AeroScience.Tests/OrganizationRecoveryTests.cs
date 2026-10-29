using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Organization;
using AeroScience.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace AeroScience.Tests;
public sealed class RecoveryFactAttribute:FactAttribute
{
    public RecoveryFactAttribute(){if(string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("AEROSCIENCE_RECOVERY_RESTIC"))||string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("AEROSCIENCE_ORG_TEST_DATABASE")))Skip="Actual PostgreSQL/restic/pg_dump/pg_restore tools are not configured on this platform. No simulated backup acceptance.";}
}
public sealed partial class OrganizationConnectionIntegrationTests
{
    [RecoveryFact]public async Task EncryptedOffProcessBackupRestoresAuthorizedEvidenceAuditAndProtectedMfaAfterSourceLoss()
    {
        await using var f=await SubmissionFixture.Create(protectedIdentity:true);var card=await f.Card(ObservationResearch.Template);var attempt=await f.Record(card.Prepared,2,notes:true);var approved=await f.ReviewApprove(f.Draft(attempt));using var delivery=f.Delivery();Assert.Equal(SubmissionDeliveryState.ServerReceived,(await delivery.Send(approved.Id,default)).State);
        var enrollment=await f.Server.Post("/api/account-security/enroll",new AccountEnrollment("ResearchOnly!2026Flight"));var key=enrollment.GetProperty("setupKey").GetString()!;var code=OrganizationServerTests.AuthenticatorCode(key,DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30+1);var enabled=await f.Server.Post("/api/account-security/enable",new AccountCode(code));var recovery=enabled.GetProperty("recoveryCodes")[0].GetString()!;await f.Server.Post("/api/account-security/confirm-recovery",new AccountRecoveryConfirmation(recovery));
        var repository=Path.Combine(Path.GetTempPath(),"aeroscience-independent-recovery-"+Guid.NewGuid().ToString("N"));var work=Path.Combine(f.Root,"private-work");var password=Path.Combine(f.Root,"repository-key");File.WriteAllText(password,Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));OrganizationConfigurationTests.ProtectCredential(password);
        var options=new ServerOperationsOptions(true,repository,password,"Disposable independent local repository",work,System.Environment.GetEnvironmentVariable("AEROSCIENCE_RECOVERY_RESTIC")!,System.Environment.GetEnvironmentVariable("AEROSCIENCE_RECOVERY_PG_DUMP")!,System.Environment.GetEnvironmentVariable("AEROSCIENCE_RECOVERY_PG_RESTORE")!,System.Environment.GetEnvironmentVariable("AEROSCIENCE_ORG_TEST_DATABASE"),null,64*1024*1024,null,null);
        try{
            using var scope=f.Server.App.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();var settings=f.Server.App.Services.GetRequiredService<OrganizationSettings>();var engine=new OrganizationRecoveryEngine(db,settings,options);var backup=await engine.Backup(Guid.NewGuid(),default);Assert.True(backup.Bytes>0);Assert.Empty(Directory.GetFileSystemEntries(work));
            var manifest=JsonSerializer.Deserialize<ServerBackupManifest>(backup.ManifestJson,WireJson.Options)!;Assert.Equal(1,manifest.ReceivedPackages);Assert.True(manifest.AuditEvents>0);
            // Simulate a failed upgrade/loss of records in the disposable primary. Recovery must follow the snapshot, not current tables.
            await db.Database.ExecuteSqlRawAsync("TRUNCATE organization.\"Submissions\" CASCADE");Assert.Equal(0,await db.Submissions.CountAsync());var discovered=await engine.Discover(backup.SnapshotId,default);Assert.Equal(backup.BundleSha256,discovered.BundleSha256);Assert.Equal(backup.ManifestJson,discovered.ManifestJson);var verified=await engine.VerifyRestore(discovered,default);Assert.Equal(manifest.EvidenceDigest,verified.EvidenceDigest);Assert.Equal(manifest.AuditDigest,verified.AuditDigest);Assert.Equal(1,verified.ReceivedPackages);Assert.Equal(0,await db.Submissions.CountAsync());Assert.Empty(Directory.GetFileSystemEntries(work));
            // Authenticated encryption refuses an independently wrong repository key, and never creates replacement identity.
            var wrong=Path.Combine(f.Root,"wrong-repository-key");File.WriteAllText(wrong,new string('x',64));OrganizationConfigurationTests.ProtectCredential(wrong);await Assert.ThrowsAsync<InvalidOperationException>(()=>new OrganizationRecoveryEngine(db,settings,options with{PasswordFile=wrong}).VerifyRestore(backup,default));Assert.Equal(0,await db.Submissions.CountAsync());Assert.Empty(Directory.GetFileSystemEntries(work));
            // Reject modified manifest/hash before touching any database.
            backup.BundleSha256=new string('0',64);await Assert.ThrowsAsync<InvalidOperationException>(()=>engine.VerifyRestore(backup,default));Assert.Empty(Directory.GetFileSystemEntries(work));
            foreach(var file in Directory.EnumerateFiles(repository,"*",SearchOption.AllDirectories)){if(new FileInfo(file).Length>4*1024*1024)continue;var bytes=await File.ReadAllBytesAsync(file);Assert.DoesNotContain("Selected original pilot note",Encoding.UTF8.GetString(bytes));Assert.DoesNotContain(key,Encoding.UTF8.GetString(bytes));}
        }finally{if(Directory.Exists(repository))Directory.Delete(repository,true);}
    }
}
public sealed class ServerOperationsConfigurationTests
{
    [Theory][InlineData("s3:http://storage.example/bucket")][InlineData("rest:https://user:password@storage.example/repository")][InlineData("/tmp/not-independent")][InlineData("s3:https://storage.example/")]
    public void ProductionRefusesInsecureOrCredentialBearingRecoveryRepositories(string value)=>Assert.Throws<InvalidOperationException>(()=>ServerOperationsOptions.ValidateRepository(value,false));
    [Theory][InlineData("s3:https://storage.example/bucket/organization")][InlineData("rest:https://storage.example/private/organization")]
    public void ProtectedRepositoryContractsAreAcceptedWithoutContactingAProvider(string value)=>ServerOperationsOptions.ValidateRepository(value,false);
}

public sealed class ServerDistributionTests
{
    [Fact]public void PinnedUpdateRequiresValidSignatureExactSchemaSourceAndPairedRecoveryPolicy()
    {
        var directory=Path.Combine(Path.GetTempPath(),"aeroscience-distribution-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try{
            using var signer=ECDsa.Create(ECCurve.NamedCurves.nistP256);var file=Path.Combine(directory,"distribution.json");var key=Path.Combine(directory,"publisher.pem");File.WriteAllText(key,signer.ExportSubjectPublicKeyInfoPem());OrganizationConfigurationTests.ProtectCredential(key);
            var options=new ServerOperationsOptions(false,null,null,"test",directory,"","","",null,null,1048576,file,key);
            var distribution=new ServerDistribution(1,"0.7.1",new string('a',40),"ghcr.io/justseanc/aeroscience-organization@sha256:"+new string('b',64),"0.7.0",new string('c',64),true);
            void Save(ServerDistribution value,string? signature=null){File.WriteAllText(file,JsonSerializer.Serialize(new SignedServerDistribution(value,signature??Convert.ToBase64String(signer.SignData(JsonSerializer.SerializeToUtf8Bytes(value,WireJson.Options),HashAlgorithmName.SHA256))),WireJson.Options));OrganizationConfigurationTests.ProtectCredential(file);}
            Save(distribution);Assert.Equal(distribution,options.Distribution()!.Distribution);
            Save(distribution with{Image="ghcr.io/justseanc/aeroscience-organization:latest"});Assert.Throws<InvalidOperationException>(()=>options.Distribution());Save(distribution with{PairedRestoreRequired=false});Assert.Throws<InvalidOperationException>(()=>options.Distribution());Save(distribution,Convert.ToBase64String(new byte[64]));Assert.Throws<InvalidOperationException>(()=>options.Distribution());
        }finally{Directory.Delete(directory,true);}
    }
}

public sealed partial class OrganizationServerTests
{
    [RecoveryFact]public async Task ExplicitBackupConsentPersistsScheduledPolicyIdempotentJobsAndVerifiedRestoreAcrossRestart()
    {
        var keyFile=Path.Combine(directory,"ops-identity.pfx");using(var rsa=RSA.Create(2048)){var r=new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=P26 Operations Fixture",rsa,HashAlgorithmName.SHA256,System.Security.Cryptography.RSASignaturePadding.Pkcs1);using var c=r.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddDays(1));File.WriteAllBytes(keyFile,c.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx,"P26-Operations-Certificate-Key"));OrganizationConfigurationTests.ProtectCredential(keyFile);}
        var passwordFile=Path.Combine(directory,"repository-key");File.WriteAllText(passwordFile,Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));OrganizationConfigurationTests.ProtectCredential(passwordFile);
        var distributionFile=Path.Combine(directory,"server-distribution.json");var distributionKey=Path.Combine(directory,"publisher.pem");using var publisher=ECDsa.Create(ECCurve.NamedCurves.nistP256);File.WriteAllText(distributionKey,publisher.ExportSubjectPublicKeyInfoPem());OrganizationConfigurationTests.ProtectCredential(distributionKey);
        deploymentOverrides=new(){["Organization:Operations:DistributionFile"]=distributionFile,["Organization:Operations:DistributionPublicKeyFile"]=distributionKey,["Organization:KeyCertificateFile"]=keyFile,["Organization:KeyCertificatePassword"]="P26-Operations-Certificate-Key",["Organization:Operations:Enabled"]="true",["Organization:Operations:Repository"]=Path.Combine(directory,"repository"),["Organization:Operations:PasswordFile"]=passwordFile,["Organization:Operations:WorkDirectory"]=Path.Combine(directory,"work"),["Organization:Operations:ResticExecutable"]=System.Environment.GetEnvironmentVariable("AEROSCIENCE_RECOVERY_RESTIC"),["Organization:Operations:PgDumpExecutable"]=System.Environment.GetEnvironmentVariable("AEROSCIENCE_RECOVERY_PG_DUMP"),["Organization:Operations:PgRestoreExecutable"]=System.Environment.GetEnvironmentVariable("AEROSCIENCE_RECOVERY_PG_RESTORE"),["Organization:Operations:RecoveryConnectionString"]=System.Environment.GetEnvironmentVariable("AEROSCIENCE_ORG_TEST_DATABASE")};
        var e=await SecureAdministrator();await Value<JsonElement>(await browser.Send("/api/account-security/confirm-recovery",new AccountRecoveryConfirmation(e.Codes[0])));
        var consent=new ServerOperationsPolicyCommand(Guid.NewGuid(),0,false,24,14,8,true,Password);await Value<JsonElement>(await browser.Send("/api/organization/operations/policy",consent));
        var command=new ServerOperationCommand(Guid.NewGuid(),ServerOperationKind.Backup,null,Password,true);var first=await Value<ServerOperationView>(await browser.Send("/api/organization/operations/commands",command));var repeat=await Value<ServerOperationView>(await browser.Send("/api/organization/operations/commands",command));Assert.Equal(first,repeat);
        async Task<ServerOperationsView> Finished(Guid id){var until=DateTimeOffset.UtcNow.AddSeconds(40);while(DateTimeOffset.UtcNow<until){var state=await Value<ServerOperationsView>(await browser.Client.GetAsync("/api/organization/operations"));var job=state.Operations.Single(j=>j.Id==id);if(job.State is ServerOperationState.Completed or ServerOperationState.Failed){Assert.Equal(ServerOperationState.Completed,job.State);return state;}await Task.Delay(200);}throw new TimeoutException("Protected operation did not complete within the fixture deadline.");}
        var before=await Finished(first.Id);var backup=Assert.Single(before.Backups);Assert.Null(backup.VerifiedRestoreUtc);await app.StopAsync();await app.DisposeAsync();await Start();browser.Connect(app);await browser.Session();var restored=await Value<ServerOperationView>(await browser.Send("/api/organization/operations/commands",new ServerOperationCommand(Guid.NewGuid(),ServerOperationKind.VerifyRestore,backup.Id,Password,true)));var after=await Finished(restored.Id);Assert.NotNull(Assert.Single(after.Backups).VerifiedRestoreUtc);Assert.Equal(1,after.PolicyRevision);Assert.Equal(2,after.Operations.Length);Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(directory,"work")));
        using(var scope=app.Services.CreateScope()){
            var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();var schema=OrganizationDeployment.Digest(JsonSerializer.SerializeToUtf8Bytes(db.Database.GetMigrations().ToArray(),WireJson.Options));var release=new ServerDistribution(1,"0.7.1",new string('a',40),"ghcr.io/justseanc/aeroscience-organization@sha256:"+new string('b',64),"0.7.0-development",schema,true);var signature=Convert.ToBase64String(publisher.SignData(JsonSerializer.SerializeToUtf8Bytes(release,WireJson.Options),HashAlgorithmName.SHA256));File.WriteAllText(distributionFile,JsonSerializer.Serialize(new SignedServerDistribution(release,signature),WireJson.Options));OrganizationConfigurationTests.ProtectCredential(distributionFile);
        }
        var preflight=await Value<ServerOperationView>(await browser.Send("/api/organization/operations/commands",new ServerOperationCommand(Guid.NewGuid(),ServerOperationKind.PrepareUpdate,null,Password,true)));var prepared=await Finished(preflight.Id);Assert.Equal(2,prepared.Backups.Length);Assert.All(prepared.Backups,b=>Assert.NotNull(b.VerifiedRestoreUtc));Assert.Contains("no migration or image was applied",prepared.Operations.Single(j=>j.Id==preflight.Id).Message);
        var stale=consent with{RequestId=Guid.NewGuid(),ExpectedRevision=0};Assert.Equal(System.Net.HttpStatusCode.Conflict,(await browser.Send("/api/organization/operations/policy",stale)).StatusCode);
    }
}
