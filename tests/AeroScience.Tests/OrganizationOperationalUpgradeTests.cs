using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Organization;
using AeroScience.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AeroScience.Tests;

public sealed partial class OrganizationConnectionIntegrationTests
{
    [PostgresFact]public async Task CompiledUpgradeCannotRewriteAuthoritativeOrganizationEventsWhilePreservingEvidenceAndIdentity()
    {
        await using var f=await SubmissionFixture.Create(protectedIdentity:true);
        var card=await f.Card(ObservationResearch.Template);var attempt=await f.Record(card.Prepared,2,notes:true);
        var approved=await f.ReviewApprove(f.Draft(attempt));using var delivery=f.Delivery();
        Assert.Equal(SubmissionDeliveryState.ServerReceived,(await delivery.Send(approved.Id,default)).State);
        using var scope=f.Server.App.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();
        var migrations=db.Database.GetMigrations().ToArray();var previous=migrations[^2];
        await db.GetService<IMigrator>().MigrateAsync(previous);
        var source=(await db.Database.GetAppliedMigrationsAsync()).ToArray();var hash=OrganizationSchemaUpgrade.SchemaHash(source);
        var events=await db.Events.AsNoTracking().OrderBy(e=>e.Sequence).ToArrayAsync();Assert.NotEmpty(events);
        // The existing evidence/audit/identity checks alone cannot detect this data loss.
        // It occurs inside the real compiled DDL transaction, before the final history insert.
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION public.damage_fixture_events() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN UPDATE organization.\"Events\" SET \"Meaning\"='damaged event authority'; RETURN NEW; END $$; CREATE TRIGGER damage_fixture_events BEFORE INSERT ON public.\"__EFMigrationsHistory\" FOR EACH ROW EXECUTE FUNCTION public.damage_fixture_events()");
        try{
            var failure=await Assert.ThrowsAsync<InvalidOperationException>(()=>OrganizationSchemaUpgrade.ApplyKnownMigrations(db,hash,default));
            Assert.Contains("organization operational history",failure.Message);
            Assert.Equal(source,(await db.Database.GetAppliedMigrationsAsync()).ToArray());
            Assert.Equal(JsonSerializer.Serialize(events),JsonSerializer.Serialize(await db.Events.AsNoTracking().OrderBy(e=>e.Sequence).ToArrayAsync()));
            Assert.Single(await db.Submissions.ToArrayAsync());
        }finally{await db.Database.ExecuteSqlRawAsync("DROP TRIGGER damage_fixture_events ON public.\"__EFMigrationsHistory\"; DROP FUNCTION public.damage_fixture_events()");}
        await OrganizationSchemaUpgrade.ApplyKnownMigrations(db,hash,default);
        Assert.Equal(migrations,(await db.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Equal(JsonSerializer.Serialize(events),JsonSerializer.Serialize(await db.Events.AsNoTracking().OrderBy(e=>e.Sequence).ToArrayAsync()));
    }
}
