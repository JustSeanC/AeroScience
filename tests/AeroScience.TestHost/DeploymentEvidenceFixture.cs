using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;

namespace AeroScience.TestHost;
// Test executable only. Generates retained synthetic pilot documentation using the real P16/P17 stores.
public static class DeploymentEvidenceFixture
{
    sealed record Input(string Origin,Guid ConnectionId,OrganizationClaimOriginal Original,SubmissionCapabilities Capabilities);
    public static async Task Run(string directory)
    {
        if(Directory.Exists(directory))throw new InvalidOperationException("Use a new disposable fixture directory.");
        var text=await Console.In.ReadToEndAsync();if(text.Length>65536)throw new ArgumentException("Bounded fixture metadata required.");
        var input=JsonSerializer.Deserialize<Input>(text,WireJson.Options)??throw new ArgumentException("Fixture input missing.");
        if(input.Origin!="https://organization.test")throw new ArgumentException("This fixture only supports the reserved local deployment origin.");
        Directory.CreateDirectory(directory);var paths=new StorageManager(new PreferenceStore(directory)).Initialize();var store=new MissionStore(Path.Combine(paths.MissionDirectory,"missions.db"));store.Initialize();using var science=new ScienceStore(paths.ScienceLibraryDirectory);science.Initialize();store.ScienceLibrary=science;
        var source=MissionStore.VerifiedOrganizationAssignment(input.ConnectionId,input.Origin,input.Capabilities.OrganizationId,input.Original);var preview=store.PreviewOrganizationMission(source,null);var claim=input.Original.Claim;
        var prepared=store.SaveOrganizationMission(input.ConnectionId,claim.Id,source,new(1,Guid.NewGuid(),null,preview.PlanSha256,claim.IssuedSha256,claim.Revision,[],"copy"));
        static TelemetryFrame Frame(DateTimeOffset at)=>new(at,TelemetrySource.Simulated,"SYNTHETIC local deployment fixture, no installed MSFS",40,-75,3000,null,120,120,120,0,0,0,0,0);
        var start=DateTimeOffset.UtcNow;await Task.Delay(2100);var flight=store.Create("Synthetic deployment observation flight",Frame(start),start,plan:prepared.Plan);
        for(var i=0;i<=2;i++){store.Append(flight,Frame(start.AddSeconds(i)));store.AddMarker(flight,new("observation","Synthetic documented atmospheric observation "+i),start.AddSeconds(i),Frame(start.AddSeconds(i)),"UNSHARED-PRIVATE-CONTEXT");}
        store.Finish(flight,MissionStatus.Completed,start.AddSeconds(2),"complete");var attempt=store.EvaluateResearchAttempt(new(Guid.NewGuid(),flight,start,start.AddSeconds(2),CardId:prepared.Card.Id),science:science);
        if(attempt.Outcome!=ResearchAttemptOutcome.CollectionComplete)throw new InvalidOperationException("Synthetic documentation evidence did not meet its real evaluator.");
        store.CacheSubmissionCapabilities(input.ConnectionId,input.Origin,input.Capabilities);
        var row=store.SaveSubmissionDraft(new(1,Guid.NewGuid(),0,flight,[attempt.Id],null,"Explicit synthetic deployment evidence",input.ConnectionId),science);var package=store.PreviewSavedSubmission(row.Id,science);
        var approved=store.ApproveSubmission(row.Id,new(1,row.Revision,package.ManifestSha256,package.PayloadSha256,package.PayloadBytes,input.ConnectionId,true),science,input.Capabilities.PilotId,input.Capabilities.OrganizationId);
        var wire=JsonSerializer.Serialize(new SubmissionUpload(store.ApprovedSubmissionDocument(row.Id,science),approved.Consent!),WireJson.Options);
        if(wire.Contains("UNSHARED-PRIVATE-CONTEXT",StringComparison.Ordinal))throw new InvalidOperationException("Private context was not excluded.");
        Console.WriteLine(wire);
    }
}
