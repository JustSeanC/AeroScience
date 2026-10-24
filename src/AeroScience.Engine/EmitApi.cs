using System.Text;
using System.Text.Json;
using AeroScience.Shared;
namespace AeroScience.Engine;
public static class EmitApi
{
    public static void MapEmitEndpoints(this WebApplication app)
    {
        app.MapGet("/api/science/emit/import",(EmitLibrary emit,HttpContext c)=>AccessControl.IsLoopback(c)?Results.Ok(emit.Snapshot()):Results.StatusCode(403));
        app.MapPost("/api/science/emit/import",(EmitImportApproval approval,EmitLibrary emit,HttpContext c)=>{if(!AccessControl.IsLoopback(c))return Results.StatusCode(403);emit.Import(approval);return Results.Accepted();});
        app.MapPost("/api/science/emit/import/cancel",(EmitLibrary emit,HttpContext c)=>{if(!AccessControl.IsLoopback(c))return Results.StatusCode(403);emit.Cancel();return Results.Ok(new{cancelled=true});});
        app.MapGet("/api/science/sources/{id:guid}/emit",(Guid id,EmitLibrary emit,HttpContext c)=>emit.Details(id,c.RequestAborted));
        app.MapPost("/api/science/sources/{id:guid}/emit/spectrum",(Guid id,EmitSampleRequest q,EmitLibrary emit,HttpContext c)=>emit.Spectrum(id,q,c.RequestAborted));
        app.MapPost("/api/science/sources/{id:guid}/emit/map",(Guid id,EmitMapRequest q,EmitLibrary emit,HttpContext c)=>emit.Map(id,q,c.RequestAborted));
        app.MapPost("/api/science/sources/{id:guid}/emit/compare",(Guid id,EmitComparisonRequest q,EmitLibrary emit,HttpContext c)=>emit.Compare(id,q,c.RequestAborted));
        app.MapPost("/api/science/sources/{id:guid}/emit/export",async(Guid id,EmitSampleRequest q,EmitLibrary emit,HttpContext c)=>
        {
            var data=await emit.Export(id,q,c.RequestAborted);
            // Bounded, self-contained portable evidence; relative source IDs only.
            return Results.File(JsonSerializer.SerializeToUtf8Bytes(data,WireJson.Options),"application/json","AeroScience-EMIT-sample.json");
        });
    }
}
