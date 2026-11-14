using System.Security.Claims;
using AeroScience.Shared;
using Microsoft.EntityFrameworkCore;

namespace AeroScience.Organization;

public sealed record OrganizationPresentationChange(int Version, long? ExpectedRevision, string? Locale);

// Member-only display preference. Existing origin, antiforgery, identity, MFA
// and session-revocation middleware apply without exemptions. No role writes.
public static class OrganizationPresentationApi
{
    public static void Map(WebApplication app)
    {
        object View(OrganizationUser user) => new { version=1, revision=user.PresentationRevision, locale=user.PresentationLocale,
            catalogVersion=PresentationText.CatalogVersion, catalogIdentity=PresentationText.CatalogIdentity };
        app.MapGet("/api/account/presentation", async (HttpContext context, OrganizationDatabase database) => {
            var id=Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var user=await database.Users.AsNoTracking().SingleAsync(u=>u.Id==id,context.RequestAborted);
            return Results.Ok(View(user));
        }).RequireAuthorization();
        app.MapPost("/api/account/presentation", async (OrganizationPresentationChange change, HttpContext context, OrganizationDatabase database) => {
            if(change.Version!=1 || change.ExpectedRevision is null or <0 or long.MaxValue || change.Locale is not null && !PresentationText.Locales.Any(l=>l.Id==change.Locale))
                throw new ArgumentException("Choose a supported display language and reload the current preference before saving.");
            var id=Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var updated=await database.Users.Where(u=>u.Id==id&&u.PresentationRevision==change.ExpectedRevision)
                .ExecuteUpdateAsync(s=>s.SetProperty(u=>u.PresentationLocale,change.Locale).SetProperty(u=>u.PresentationRevision,u=>u.PresentationRevision+1),context.RequestAborted);
            if(updated!=1) throw new OrganizationConflict("This language preference changed. Reload before saving again.");
            return Results.Ok(View(await database.Users.AsNoTracking().SingleAsync(u=>u.Id==id,context.RequestAborted)));
        }).RequireAuthorization();
    }
}
