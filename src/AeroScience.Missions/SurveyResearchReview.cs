using System.Globalization;
using System.Text.Json;
using AeroScience.Shared;
namespace AeroScience.Missions;

public sealed partial class MissionStore
{
    public SurveyResearchReview ReviewSurveyEvidence(PersonalResearchAttempt result,ScienceStore science,CancellationToken token=default)
    {
        if(result.Survey is null||result.Card.Survey is null||result.EvaluatorVersion!=SurveyResearch.Evaluator||result.EndUtc<result.StartUtc||result.EndUtc-result.StartUtc>TimeSpan.FromHours(24))throw new NotSupportedException("Select a supported retained survey result of up to 24 hours.");
        if(ScienceDeleted(result.FlightId))throw new InvalidOperationException("Collected survey science was permanently deleted; it cannot be reconstructed from flight context.");
        using var lease=science.BeginAnalysis();
        var calculated=SurveyResearch.Calculate(this,science,result.Card,new(result.Id,result.FlightId,result.StartUtc,result.EndUtc),PersonalPauseTimes(result.FlightId,result.StartUtc,result.EndUtc,true),token);
        var interrupted=Detail(result.FlightId)?.Mission.Status!=MissionStatus.Completed||result.RecordedAttemptId is {} marked&&RecordedResearchAttempt(marked).Status==ResearchAttemptStatus.Interrupted;
        var matches=result.Evidence.Sha256==calculated.Hash&&result.Evidence.PlanSha256==PreflightReadiness.Hash(Research(result.FlightId).PlanSnapshot!)
            &&PreflightReadiness.Hash(result.Survey with{Limitations=[]})==PreflightReadiness.Hash(calculated.Summary with{Limitations=[]})&&result.Outcome==SurveyResearch.Outcome(calculated.Summary,interrupted).Outcome;
        return new(matches,calculated.Summary,matches?calculated.Preview:[],calculated.Downsampled,matches
            ?"Original observation/product, definition and frozen plan hashes match. Values are independently retained samples; external originals are not needed to reopen them."
            :"Saved evidence changed or is unavailable. This result is historical; no verified measurement layer is shown.",calculated.Summary.Sources.ToDictionary(s=>s.Id,s=>science.Source(s.Id) is not null));
    }
}
public sealed partial class ScienceStore
{
    internal IEnumerable<RemoteProduct> ResearchProductWindow(Guid flight,DateTimeOffset start,DateTimeOffset end)
    {
        if(AvailabilityError() is {} error)throw new IOException(error);if(!File.Exists(DatabasePath))yield break;
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT payload_json FROM remote_products WHERE mission_id=$id AND flight_utc >= $start AND flight_utc <= $end AND instrument_id IN ('terrain-scanner','thermal-imager') ORDER BY flight_utc,id";
        cmd.Parameters.AddWithValue("$id",flight.ToString());cmd.Parameters.AddWithValue("$start",start.ToString("O",CultureInfo.InvariantCulture));cmd.Parameters.AddWithValue("$end",end.ToString("O",CultureInfo.InvariantCulture));
        using var rows=cmd.ExecuteReader();while(rows.Read())yield return JsonSerializer.Deserialize<RemoteProduct>(rows.GetString(0),WireJson.Options)!;
    }
}
