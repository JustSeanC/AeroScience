using System.Net;
using System.Net.Http.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace AeroScience.Tests;
public sealed partial class ApiTests
{
    [Fact] public async Task SurveyViewerIsPairedCsrfProtectedReadOnlyAndBoundToExactArtifact()
    {
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync($"/api/missions/{Guid.NewGuid()}/survey-results")).StatusCode);
        await Pair(client);var missions=factory.Services.GetRequiredService<MissionStore>();var science=factory.Services.GetRequiredService<ScienceStore>();
        var f=await AeroScience.TestHost.TerrainCompletionFixture.Create(missions,science,directory);var job=science.TerrainJob(f.JobId);science.ControlTerrainJob(job.Id,new(Guid.NewGuid(),job.Revision,"start"));var a=TerrainElevations.Process(science,missions,job.Id);
        var before=ScientificCatalog.Hash(MissionEvaluationArchive.Build(missions,science,f.MissionId));
        var manifest=await client.GetFromJsonAsync<SurveyViewManifest>($"/api/science/artifacts/{a.Id}/survey-manifest",WireJson.Options);Assert.Equal(a.Sha256,manifest!.Sha256);
        client.DefaultRequestHeaders.Remove("X-AeroScience-CSRF");Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync($"/api/science/artifacts/{a.Id}/survey-page",new SurveyViewQuery(a.Sha256),WireJson.Options)).StatusCode);await Pair(client);
        var page=await client.PostAsJsonAsync($"/api/science/artifacts/{a.Id}/survey-page",new SurveyViewQuery(a.Sha256,Limit:4),WireJson.Options);page.EnsureSuccessStatusCode();Assert.InRange((await page.Content.ReadFromJsonAsync<SurveyViewPage>(WireJson.Options))!.Values.Length,1,4);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync($"/api/science/artifacts/{a.Id}/survey-page",new SurveyViewQuery(a.Sha256,Limit:1025),WireJson.Options)).StatusCode);
        Assert.Equal(before,ScientificCatalog.Hash(MissionEvaluationArchive.Build(missions,science,f.MissionId)));Assert.Empty(science.LibraryJobs());
    }
}
