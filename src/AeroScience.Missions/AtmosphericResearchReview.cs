using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

public sealed partial class MissionStore
{
    public PersonalResearchAttempt ResearchAttempt(Guid id)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT payload_json FROM personal_research_attempts WHERE id=$id";cmd.Parameters.AddWithValue("$id",id.ToString());
        return JsonSerializer.Deserialize<PersonalResearchAttempt>(cmd.ExecuteScalar() as string??throw new ArgumentException("Saved research result is no longer available."),WireJson.Options)!;
    }
    public AtmosphericResearchReview ReviewAtmosphericEvidence(PersonalResearchAttempt result,ScienceStore? science,CancellationToken token=default)
    {
        if(result.AtmosphericProfile is null||result.Card.AtmosphericProfile is null||result.EvaluatorVersion!=AtmosphericResearch.Evaluator)
            throw new NotSupportedException("Select a supported atmospheric collection result.");
        if(result.EndUtc<result.StartUtc||result.EndUtc-result.StartUtc>TimeSpan.FromHours(24))throw new NotSupportedException("Select an atmospheric interval of up to 24 hours.");
        if(ScienceDeleted(result.FlightId))throw new InvalidOperationException("Collected science and dependent atmospheric results were permanently deleted.");
        if(science is null)throw new InvalidOperationException("Reconnect the Scientific Data Library to inspect original observations.");
        using var lease=science.BeginAnalysis();
        var window=new ResearchAttemptRequest(result.Id,result.FlightId,result.StartUtc,result.EndUtc);
        var calculated=AtmosphericResearch.Calculate(science.ResearchObservationWindow(result.FlightId,result.StartUtc,result.EndUtc),result.Card,science.LibraryId,PersonalPauseTimes(result.FlightId,result.StartUtc,result.EndUtc,true),token,window);
        var interrupted=Detail(result.FlightId)?.Mission.Status!=MissionStatus.Completed
            ||result.RecordedAttemptId is {} marked&&RecordedResearchAttempt(marked).Status==ResearchAttemptStatus.Interrupted;
        var outcome=AtmosphericResearch.Outcome(calculated.Summary,result.Card.AtmosphericProfile,interrupted);
        var summaryMatches=PreflightReadiness.Hash(result.AtmosphericProfile with{Limitations=[]})==PreflightReadiness.Hash(calculated.Summary with{Limitations=[]});
        var matches=result.Evidence.Sha256==calculated.Hash&&result.Evidence.PlanSha256==PreflightReadiness.Hash(Research(result.FlightId).PlanSnapshot!)
            &&summaryMatches&&outcome.Outcome==result.Outcome;
        return new(matches,calculated.Summary,matches?calculated.Preview:[],calculated.Downsampled,matches
            ?"Original observation, definition, event and frozen plan hashes match. Chart points are actual retained science readings, not interpolated flight context."
            :"Retained evidence changed or is missing. The saved result is historical; no verified profile is plotted. Refresh or evaluate the retained evidence again.");
    }
}
