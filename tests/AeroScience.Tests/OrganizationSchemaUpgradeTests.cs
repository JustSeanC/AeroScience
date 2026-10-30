using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Organization;
using AeroScience.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
namespace AeroScience.Tests;
public sealed partial class OrganizationConnectionIntegrationTests
{
    [PostgresFact]public async Task FailedCompiledMigrationRollsBackSchemaWithoutLosingReceivedEvidenceIdentityAuditOrMfa()
    {
        await using var f=await SubmissionFixture.Create(protectedIdentity:true);var card=await f.Card(ObservationResearch.Template);var attempt=await f.Record(card.Prepared,2,notes:true);var approved=await f.ReviewApprove(f.Draft(attempt));using var delivery=f.Delivery();Assert.Equal(SubmissionDeliveryState.ServerReceived,(await delivery.Send(approved.Id,default)).State);
        var enrollment=await f.Server.Post("/api/account-security/enroll",new AccountEnrollment("ResearchOnly!2026Flight"));var key=enrollment.GetProperty("setupKey").GetString()!;
        await f.Server.Post("/api/account-security/enable",new AccountCode(OrganizationServerTests.AuthenticatorCode(key,DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30+1)));
        using var scope=f.Server.App.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();var users=scope.ServiceProvider.GetRequiredService<UserManager<OrganizationUser>>();
        var original=await db.Users.AsNoTracking().SingleAsync(u=>u.TwoFactorEnabled);var org=(await db.Organizations.AsNoTracking().SingleAsync()).PublicId;
        var evidence=await db.Submissions.AsNoTracking().Select(s=>new{s.Id,s.PayloadSha256,s.PayloadJson,s.ReceiptId}).ToArrayAsync();var audit=await db.Audit.AsNoTracking().OrderBy(a=>a.Id).ToArrayAsync();
        var migrations=db.Database.GetMigrations().ToArray();var target=migrations[^1];var previous=migrations[^2];
        var assembly=db.GetService<IMigrationsAssembly>();var migration=assembly.CreateMigration(assembly.Migrations[target],db.Database.ProviderName!);
        var addedTables=migration.UpOperations.OfType<CreateTableOperation>().ToArray();var addedColumns=migration.UpOperations.OfType<AddColumnOperation>().ToArray();Assert.NotEmpty(addedTables.Select(t=>t.Name).Concat(addedColumns.Select(c=>c.Name)));
        async Task AssertTargetSchema(bool applied){
            foreach(var table in addedTables)Assert.Equal(applied?1:0,await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema={table.Schema??"public"} AND table_name={table.Name}").SingleAsync());
            foreach(var column in addedColumns)Assert.Equal(applied?1:0,await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_schema={column.Schema??"public"} AND table_name={column.Table} AND column_name={column.Name}").SingleAsync());
        }
        await AssertTargetSchema(true);await db.GetService<IMigrator>().MigrateAsync(previous);await AssertTargetSchema(false);
        var source=(await db.Database.GetAppliedMigrationsAsync()).ToArray();var hash=OrganizationSchemaUpgrade.SchemaHash(source);
        // Actual PostgreSQL rejects the last history insertion AFTER the compiled DDL ran.
        // The entire batch must roll back, not only the statement that failed.
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION public.refuse_fixture_upgrade() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'isolated failed-upgrade fixture'; END $$; CREATE TRIGGER refuse_fixture_upgrade BEFORE INSERT ON public.\"__EFMigrationsHistory\" FOR EACH ROW EXECUTE FUNCTION public.refuse_fixture_upgrade()");
        try{
            var failure=await Assert.ThrowsAsync<PostgresException>(()=>OrganizationSchemaUpgrade.ApplyKnownMigrations(db,hash,default));Assert.Equal("P0001",failure.SqlState);
            Assert.Equal(source,(await db.Database.GetAppliedMigrationsAsync()).ToArray());Assert.DoesNotContain(target,await db.Database.GetAppliedMigrationsAsync());
            await AssertTargetSchema(false);
            Assert.Equal(JsonSerializer.Serialize(evidence),JsonSerializer.Serialize(await db.Submissions.AsNoTracking().Select(s=>new{s.Id,s.PayloadSha256,s.PayloadJson,s.ReceiptId}).ToArrayAsync()));Assert.Equal(JsonSerializer.Serialize(audit),JsonSerializer.Serialize(await db.Audit.AsNoTracking().OrderBy(a=>a.Id).ToArrayAsync()));
            var retained=await users.FindByIdAsync(original.Id.ToString());Assert.NotNull(retained);Assert.Equal(original.PasswordHash,retained.PasswordHash);Assert.Equal(original.SecurityStamp,retained.SecurityStamp);Assert.Equal(key,await users.GetAuthenticatorKeyAsync(retained));Assert.True(await users.CheckPasswordAsync(retained,"ResearchOnly!2026Flight"));Assert.Equal(org,(await db.Organizations.AsNoTracking().SingleAsync()).PublicId);
        }finally{await db.Database.ExecuteSqlRawAsync("DROP TRIGGER refuse_fixture_upgrade ON public.\"__EFMigrationsHistory\"; DROP FUNCTION public.refuse_fixture_upgrade()");}
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION public.refuse_identity_change() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN UPDATE organization.\"AspNetUsers\" SET \"PasswordHash\"='fixture must roll back'; RETURN NEW; END $$; CREATE TRIGGER refuse_identity_change BEFORE INSERT ON public.\"__EFMigrationsHistory\" FOR EACH ROW EXECUTE FUNCTION public.refuse_identity_change()");
        try{var identityFailure=await Assert.ThrowsAsync<InvalidOperationException>(()=>OrganizationSchemaUpgrade.ApplyKnownMigrations(db,hash,default));Assert.Contains("administrator identity",identityFailure.Message);Assert.Equal(original.PasswordHash,(await db.Users.AsNoTracking().SingleAsync(u=>u.Id==original.Id)).PasswordHash);Assert.Equal(source,(await db.Database.GetAppliedMigrationsAsync()).ToArray());await AssertTargetSchema(false);}
        finally{await db.Database.ExecuteSqlRawAsync("DROP TRIGGER refuse_identity_change ON public.\"__EFMigrationsHistory\"; DROP FUNCTION public.refuse_identity_change()");}
        await OrganizationSchemaUpgrade.ApplyKnownMigrations(db,hash,default);Assert.Equal(migrations,(await db.Database.GetAppliedMigrationsAsync()).ToArray());await AssertTargetSchema(true);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>OrganizationSchemaUpgrade.ApplyKnownMigrations(db,hash,default));
        // Current-schema rechecks are harmless; an unrelated/stale source approval cannot mutate it.
        await OrganizationSchemaUpgrade.ApplyKnownMigrations(db,OrganizationSchemaUpgrade.SchemaHash(migrations),default);
    }
}
