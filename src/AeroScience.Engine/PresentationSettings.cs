using AeroScience.Missions;
using AeroScience.Shared;

namespace AeroScience.Engine;

public static class PresentationSettings
{
    public static void MapPresentationSettings(this WebApplication app)
    {
        var instanceId=Guid.NewGuid();
        IResult View(HttpContext context, PreferenceStore store)
        {
            try {
                var saved=store.Load().Presentation;var value = PresentationPreferences.Effective(saved);
                return Results.Ok(new { version = 1, instanceId, preferences = value, effectiveLocale = PresentationText.EffectiveLocale(value.Locale),
                    catalogVersion = PresentationText.CatalogVersion, canWrite = AccessControl.IsLoopback(context) && (saved is null || saved.Version==1),
                    locales = PresentationText.Locales, preview = value.Locale != "en-US",
                    warning = saved is {Version:not 1}?"Newer presentation settings require a newer companion. Saved settings are preserved.":null });
            }catch(Exception e)when(e is System.Text.Json.JsonException or IOException or UnauthorizedAccessException){return Results.Problem("Saved profile settings need recovery. The original file was preserved.",statusCode:503);}
        }
        app.MapGet("/api/settings/presentation", (HttpContext context, PreferenceStore store) => View(context, store));
        app.MapPost("/api/settings/presentation", (HttpContext context, PreferenceStore store, PresentationChange change) =>
        {
            if (!AccessControl.IsLoopback(context)) return Results.StatusCode(403);
            try{store.SetPresentation(change);return View(context, store);}
            catch(System.Text.Json.JsonException){return Results.Problem("Saved profile settings need recovery. The original file was preserved.",statusCode:503);}
        });
    }
}
