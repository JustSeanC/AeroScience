using System.Text.Json;
using System.Security.Cryptography;
using AeroScience.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
namespace AeroScience.Organization;

// Applies only migrations compiled into this application, never SQL/expressions from a template or request.
// Provider image promotion and draining old replicas remain independently authorized operator work.
public static class OrganizationSchemaUpgrade
{
    // A Railway volume deploy stops the old instance (overlap=0). Before opening either
    // listener, only an exact pre-approved signed distribution may migrate an existing DB.
    // This does not call Railway, select a mutable image, or roll back a migrated schema.
    public static async Task ApplyConfigured(IServiceProvider services,CancellationToken ct)
    {
        var configuration=services.GetRequiredService<IConfiguration>();var value=configuration["Organization:Operations:ApplyOperationId"];
        if(value is null)return;if(!Guid.TryParse(value,out var id)||id==Guid.Empty)throw new InvalidOperationException("An exact approved update operation identity is required.");
        using(var scope=services.CreateScope()){
            var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();var applied=(await db.Database.GetAppliedMigrationsAsync(ct)).ToArray();
            if(applied.Length==0)throw new InvalidOperationException("A prepared update cannot initialize a different empty database.");
            var options=scope.ServiceProvider.GetRequiredService<ServerOperationsOptions>();var signed=options.Distribution()??throw new InvalidOperationException("The exact signed update manifest is unavailable.");var distribution=signed.Distribution;
            if(distribution.SourceRevision!=OrganizationDeployment.ApplicationVersion.Split('+').ElementAtOrDefault(1)||distribution.ApplicationVersion!=OrganizationDeployment.ApplicationVersion.Split('+')[0])throw new InvalidOperationException("This is not the exact approved update binary.");
            if(await db.Audit.AnyAsync(a=>a.Action=="server-schema-update-applied"&&a.ResourceId==id,ct)&&SchemaHash(applied)==SchemaHash(db.Database.GetMigrations()))return;
        }
        await ApplyPrepared(services,id,ct);
    }
    static async Task<string[]> OperationalQueries(NpgsqlConnection connection,NpgsqlTransaction transaction,CancellationToken ct)
    {
        var queries=new List<string>();
        // Compare only columns present in the source schema. New additive tables/columns
        // are allowed; existing consent, delivery and authority records cannot be rewritten.
        foreach(var (table,order) in new[]{("Events","\"Sequence\""),("LiveConsents","\"Id\""),("LivePolicies","\"Id\""),("DiscordDestinations","\"Id\""),("DiscordConsents","\"SessionId\""),("DiscordDisclosures","\"EventId\",\"DestinationId\",\"DestinationRevision\""),("DiscordDeliveries","\"Sequence\"")}){
            var columns=new List<string>();
            await using(var metadata=new NpgsqlCommand("SELECT quote_ident(column_name) FROM information_schema.columns WHERE table_schema='organization' AND table_name=@table ORDER BY ordinal_position",connection,transaction)){
                metadata.Parameters.AddWithValue("table",table);await using var reader=await metadata.ExecuteReaderAsync(ct);
                while(await reader.ReadAsync(ct)){if(columns.Count>=128)throw new InvalidOperationException("Operational schema exceeds the bounded update contract.");columns.Add(reader.GetString(0));}
            }
            // Names are a compiled allowlist and PostgreSQL-quoted metadata, never input from
            // a request, template, update manifest or external SQL script.
            if(columns.Count>0)queries.Add("SELECT "+string.Join(',',columns)+" FROM organization.\""+table+"\" ORDER BY "+order);
        }
        return queries.ToArray();
    }
    static async Task<string> OperationalDigest(string[] queries,NpgsqlConnection connection,NpgsqlTransaction transaction,CancellationToken ct)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach(var query in queries){
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(query));
            await using var command=new NpgsqlCommand(query,connection,transaction);await using var reader=await command.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct)){var row=new object[reader.FieldCount];reader.GetValues(row);hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(row,WireJson.Options));}
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    static async Task<string> IdentityDigest(NpgsqlConnection connection,NpgsqlTransaction transaction,CancellationToken ct)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        string[] queries=[
            "SELECT \"PublicId\" FROM organization.\"Organizations\" ORDER BY \"PublicId\"",
            "SELECT \"Id\",\"UserName\",\"PasswordHash\",\"SecurityStamp\",\"TwoFactorEnabled\" FROM organization.\"AspNetUsers\" ORDER BY \"Id\"",
            "SELECT \"UserId\",\"RoleId\" FROM organization.\"AspNetUserRoles\" ORDER BY \"UserId\",\"RoleId\"",
            "SELECT \"UserId\",\"LoginProvider\",\"Name\",\"Value\" FROM organization.\"AspNetUserTokens\" ORDER BY \"UserId\",\"LoginProvider\",\"Name\"",
            "SELECT \"UserId\",\"Role\",\"State\" FROM organization.\"Memberships\" ORDER BY \"UserId\"",
            "SELECT \"Id\",\"FriendlyName\",\"Xml\" FROM organization.\"DataProtectionKeys\" ORDER BY \"Id\""
        ];
        foreach(var query in queries){await using var command=new NpgsqlCommand(query,connection,transaction);await using var reader=await command.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct)){var row=new object[reader.FieldCount];reader.GetValues(row);hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(row,WireJson.Options));}}
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    public static async Task ApplyPrepared(IServiceProvider services,Guid operationId,CancellationToken ct)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromMinutes(15));ct=deadline.Token;
        using var scope=services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();var options=scope.ServiceProvider.GetRequiredService<ServerOperationsOptions>();var settings=scope.ServiceProvider.GetRequiredService<OrganizationSettings>();
        var signed=options.Distribution()??throw new InvalidOperationException("No authenticated pinned update is configured.");var distribution=signed.Distribution;
        var version=OrganizationDeployment.ApplicationVersion;var source=version.Split('+').ElementAtOrDefault(1);
        if(distribution.ApplicationVersion!=version.Split('+')[0]||distribution.SourceRevision!=source)throw new InvalidOperationException("This application is not the exact signed update. No schema changes were applied.");
        // A new binary must read the approval BEFORE its additive columns exist.
        var job=await db.ServerOperations.AsNoTracking().Where(j=>j.Id==operationId).Select(j=>new ServerOperationRecord{Id=j.Id,Revision=j.Revision,ActorId=j.ActorId,ActorName=j.ActorName,Kind=j.Kind,State=j.State,BackupId=j.BackupId,DistributionJson=j.DistributionJson,RequestId=j.RequestId}).SingleOrDefaultAsync(ct);
        if(job is null||job.Kind!=ServerOperationKind.PrepareUpdate||job.State!=ServerOperationState.Completed||job.DistributionJson!=JsonSerializer.Serialize(signed,WireJson.Options)||job.BackupId is null)throw new InvalidOperationException("A completed administrator-approved checkpoint for this exact distribution is required.");
        if(await db.Audit.AnyAsync(a=>a.Action=="server-schema-update-applied"&&a.ResourceId==operationId,ct)&&SchemaHash(await db.Database.GetAppliedMigrationsAsync(ct))==SchemaHash(db.Database.GetMigrations()))return;
        await RequireApprover(db,job,ct);
        // These columns predate the accepted P28 source. Missing independent protection
        // must block a production update rather than imply the primary bucket is sufficient.
        var backup=await db.ServerBackups.AsNoTracking().Where(b=>b.Id==job.BackupId).Select(b=>new ServerBackupRecord{Id=b.Id,OrganizationId=b.OrganizationId,SnapshotId=b.SnapshotId,BundleSha256=b.BundleSha256,Bytes=b.Bytes,ManifestJson=b.ManifestJson,CreatedUtc=b.CreatedUtc,VerifiedRestoreUtc=b.VerifiedRestoreUtc,RepositorySha256=b.RepositorySha256,IndependentSnapshotId=b.IndependentSnapshotId,IndependentRepositorySha256=b.IndependentRepositorySha256,IndependentVerifiedUtc=b.IndependentVerifiedUtc}).SingleAsync(ct);
        var manifest=JsonSerializer.Deserialize<ServerBackupManifest>(backup.ManifestJson,WireJson.Options)!;
        using var certificate=settings.LoadKeyCertificate()??throw new InvalidOperationException("Original protected identity is required.");
        if(backup.VerifiedRestoreUtc is null||manifest.CertificateSha256!=OrganizationDeployment.Digest(certificate.RawData)||SchemaHash(manifest.Migrations)!=distribution.SchemaSha256)throw new InvalidOperationException("The signed source schema and verified paired identity do not match.");
        if(options.Independent is null||backup.IndependentVerifiedUtc is null||backup.IndependentSnapshotId is null||backup.RepositorySha256!=OrganizationDeployment.Digest(System.Text.Encoding.UTF8.GetBytes(options.Repository!))||backup.IndependentRepositorySha256!=OrganizationDeployment.Digest(System.Text.Encoding.UTF8.GetBytes(options.Independent.Repository)))throw new InvalidOperationException("A separately keyed, verified independent recovery checkpoint is required before applying an update.");
        if(settings.HostingProvider=="railway"&&!distribution.Image.StartsWith("ghcr.io/justseanc/aeroscience-organization-railway@sha256:",StringComparison.Ordinal))throw new InvalidOperationException("The signed update must select the exact supported Railway runtime role.");
        var recovery=scope.ServiceProvider.GetRequiredService<OrganizationRecoveryEngine>();await recovery.VerifyRestore(backup,ct,true);var independent=recovery.IndependentEngine();var copy=await independent.Discover(backup.IndependentSnapshotId,ct);
        if(copy.Id!=backup.Id||copy.BundleSha256!=backup.BundleSha256||copy.ManifestJson!=backup.ManifestJson)throw new InvalidOperationException("The independent update checkpoint no longer matches the exact original pair.");await independent.VerifyRestore(copy,ct,true);
        await Apply(db,distribution.SchemaSha256,job,ct);
    }
    static async Task RequireApprover(OrganizationDatabase db,ServerOperationRecord job,CancellationToken ct){if(!await db.Memberships.AnyAsync(m=>m.UserId==job.ActorId&&m.Role=="Administrator"&&m.State==OrganizationMembershipState.Active,ct)||!await db.Users.AnyAsync(u=>u.Id==job.ActorId&&u.TwoFactorEnabled&&u.RecoveryAcknowledgedUtc!=null,ct))throw new InvalidOperationException("The approving administrator no longer has the required authority.");}
    public static string SchemaHash(IEnumerable<string> migrations)=>OrganizationDeployment.Digest(JsonSerializer.SerializeToUtf8Bytes(migrations.ToArray(),WireJson.Options));
    public static Task ApplyKnownMigrations(OrganizationDatabase db,string expectedSourceSchemaHash,CancellationToken ct)=>Apply(db,expectedSourceSchemaHash,null,ct);
    static async Task Apply(OrganizationDatabase db,string expectedSourceSchemaHash,ServerOperationRecord? job,CancellationToken ct)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromMinutes(15));ct=deadline.Token;
        var known=db.Database.GetMigrations().ToArray();var applied=(await db.Database.GetAppliedMigrationsAsync(ct)).ToArray();
        if(applied.Length==0||!applied.SequenceEqual(known.Take(applied.Length))||SchemaHash(applied)!=expectedSourceSchemaHash)throw new InvalidOperationException("The exact approved source schema is unavailable. No update was applied.");
        if(applied.Length==known.Length)return;
        var assembly=db.GetService<IMigrationsAssembly>();var generator=db.GetService<IMigrationsSqlGenerator>();
        foreach(var migration in known.Skip(applied.Length)){
            var compiled=assembly.CreateMigration(assembly.Migrations[migration],db.Database.ProviderName!);
            if(generator.Generate(compiled.UpOperations,db.Model).Any(c=>c.TransactionSuppressed))throw new InvalidOperationException("This upgrade cannot be applied atomically. A separately reviewed staged restore/promotion is required.");
        }
        var script=db.GetService<IMigrator>().GenerateScript(applied[^1],known[^1],MigrationsSqlGenerationOptions.NoTransactions);
        if(script.Length>4194304)throw new InvalidOperationException("The compiled migration exceeds the bounded update contract.");
        await db.Database.OpenConnectionAsync(ct);
        try{
            await using var tx=await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout='5s'; SET LOCAL statement_timeout='15min'; SELECT pg_advisory_xact_lock(7026002)",ct);
            var connection=(NpgsqlConnection)db.Database.GetDbConnection();var transaction=(NpgsqlTransaction)tx.GetDbTransaction();
            var tables=new List<string>();
            await using(var query=new NpgsqlCommand("SELECT quote_ident(schemaname)||'.'||quote_ident(tablename) FROM pg_tables WHERE schemaname='organization' OR (schemaname='public' AND tablename='__EFMigrationsHistory') ORDER BY schemaname,tablename",connection,transaction)){
                await using var reader=await query.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct)){if(tables.Count>=256)throw new InvalidOperationException("The organization schema exceeds the bounded update contract.");tables.Add(reader.GetString(0));}
            }
            // All existing application tables are protected, including legacy writers that don't
            // understand a new maintenance flag. A busy instance fails closed at the same deadline.
            // Identifiers come only from PostgreSQL quote_ident, never a request, manifest or filesystem input.
            await using(var tableLock=new NpgsqlCommand("LOCK TABLE "+string.Join(',',tables)+" IN ACCESS EXCLUSIVE MODE",connection,transaction))await tableLock.ExecuteNonQueryAsync(ct);
            if(!(await db.Database.GetAppliedMigrationsAsync(ct)).SequenceEqual(applied))throw new InvalidOperationException("Another process changed the source schema. Refresh the approved update.");
            if(job is not null)await RequireApprover(db,job,ct);
            var before=await OrganizationRecoveryEngine.Digests(connection,transaction,ct);
            if(job is not null){var checkpoint=JsonSerializer.Deserialize<ServerBackupManifest>(await db.ServerBackups.AsNoTracking().Where(b=>b.Id==job.BackupId).Select(b=>b.ManifestJson).SingleAsync(ct),WireJson.Options)!;var original=await OrganizationRecoveryEngine.Digests(connection,transaction,ct,job.Id);if(original.Evidence!=checkpoint.EvidenceDigest||original.Packages!=checkpoint.ReceivedPackages||original.Audit!=checkpoint.AuditDigest||original.Events!=checkpoint.AuditEvents)throw new InvalidOperationException("Organization evidence or history changed after update preparation. Prepare a fresh checkpoint before applying this update.");}
            var identity=await IdentityDigest(connection,transaction,ct);
            var operationalQueries=await OperationalQueries(connection,transaction,ct);
            var operational=await OperationalDigest(operationalQueries,connection,transaction,ct);
            await using(var command=new NpgsqlCommand(script,connection,transaction){CommandTimeout=900})await command.ExecuteNonQueryAsync(ct);
            var after=await OrganizationRecoveryEngine.Digests(connection,transaction,ct);
            if(before!=after||identity!=await IdentityDigest(connection,transaction,ct)||operational!=await OperationalDigest(operationalQueries,connection,transaction,ct)||!(await db.Database.GetAppliedMigrationsAsync(ct)).SequenceEqual(known))throw new InvalidOperationException("Upgrade changed original evidence/audit/administrator identity or organization operational history, or failed exact schema verification. The complete transaction is rolled back.");
            if(job is not null){db.Audit.Add(new(){TimestampUtc=DateTimeOffset.UtcNow,ActorId=job.ActorId,ActorName=job.ActorName,Action="server-schema-update-applied",ResourceId=job.Id,Revision=known.Length});await db.SaveChangesAsync(ct);}
            await tx.CommitAsync(ct);
        }finally{await db.Database.CloseConnectionAsync();}
    }
}
