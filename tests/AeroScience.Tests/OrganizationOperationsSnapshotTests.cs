using System.Data.Common;
using System.Text.Json;
using AeroScience.Organization;
using AeroScience.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
namespace AeroScience.Tests;

public sealed partial class OrganizationServerTests
{
    DbCommandInterceptor? operationStatusInterceptor;
    sealed class OperationStatusReadBarrier:DbCommandInterceptor
    {
        public Func<Task>? BetweenReads;
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,CommandExecutedEventData data,DbDataReader result,CancellationToken ct=default)
        {
            if(command.CommandText.Contains("FROM organization.\"ServerBackups\"",StringComparison.Ordinal)&&Interlocked.Exchange(ref BetweenReads,null) is {} update)await update();
            return result;
        }
    }
    [PostgresFact]public async Task RecoveryStatusReadsOneConsistentSnapshotAcrossAnAtomicVerificationCompletion()
    {
        // Metadata race fixture only, not a fabricated successful encrypted backup/restore acceptance.
        var barrier=new OperationStatusReadBarrier();operationStatusInterceptor=barrier;var e=await SecureAdministrator();await Value<JsonElement>(await browser.Send("/api/account-security/confirm-recovery",new AccountRecoveryConfirmation(e.Codes[0])));
        var backup=Guid.NewGuid();var job=Guid.NewGuid();var now=DateTimeOffset.UtcNow;
        using(var scope=app.Services.CreateScope()){
            var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();var organization=await db.Organizations.SingleAsync();var manifest=new ServerBackupManifest(1,backup,organization.PublicId,Origin,"metadata fixture",new string('a',40),[],new string('b',64),new string('c',64),0,new string('d',64),0,new string('e',64),0,now);
            db.ServerBackups.Add(new(){Id=backup,OrganizationId=organization.PublicId,SnapshotId="metadata-only-race-fixture",BundleSha256=new string('f',64),ManifestJson=JsonSerializer.Serialize(manifest,WireJson.Options),CreatedUtc=now});
            db.ServerOperations.Add(new(){Id=job,ActorId=organization.AdministratorId,RequestId=Guid.NewGuid(),Kind=ServerOperationKind.VerifyRestore,State=ServerOperationState.Running,BackupId=backup,CreatedUtc=now,StartedUtc=now});await db.SaveChangesAsync();
        }
        barrier.BetweenReads=async()=>{
            await using var writer=new NpgsqlConnection(connection);await writer.OpenAsync();await using var tx=await writer.BeginTransactionAsync();
            await using var command=new NpgsqlCommand("UPDATE organization.\"ServerBackups\" SET \"VerifiedRestoreUtc\"=@at WHERE \"Id\"=@backup; UPDATE organization.\"ServerOperations\" SET \"State\"=@state,\"Revision\"=\"Revision\"+1,\"FinishedUtc\"=@at WHERE \"Id\"=@job",writer,tx);
            command.Parameters.AddWithValue("at",DateTimeOffset.UtcNow);command.Parameters.AddWithValue("backup",backup);command.Parameters.AddWithValue("job",job);command.Parameters.AddWithValue("state",(int)ServerOperationState.Completed);await command.ExecuteNonQueryAsync();await tx.CommitAsync();
        };
        var original=await Value<ServerOperationsView>(await browser.Client.GetAsync("/api/organization/operations"));Assert.Null(Assert.Single(original.Backups).VerifiedRestoreUtc);Assert.Equal(ServerOperationState.Running,Assert.Single(original.Operations).State);
        var current=await Value<ServerOperationsView>(await browser.Client.GetAsync("/api/organization/operations"));Assert.NotNull(Assert.Single(current.Backups).VerifiedRestoreUtc);Assert.Equal(ServerOperationState.Completed,Assert.Single(current.Operations).State);
    }
}
