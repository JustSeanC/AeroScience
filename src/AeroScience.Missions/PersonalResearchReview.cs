using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;

namespace AeroScience.Missions;

public enum ResearchAttemptKind { Marked, PostflightInterval }
public sealed record ResearchHistoryItem(Guid Id, ResearchAttemptKind Kind, Guid FlightId, string FlightName,
    DateTimeOffset FlightStartedUtc, MissionStatus FlightStatus, PersonalResearchCard Card,
    DateTimeOffset StartUtc, DateTimeOffset? EndUtc, RecordedResearchAttempt? MarkedAttempt, PersonalResearchAttempt? Result);
public sealed record ResearchHistoryPage(ResearchHistoryItem[] Items, long Total, int Offset, int? NextOffset);
public sealed record ResearchOverview(PersonalResearchCard Card, long MarkedAttempts, long PostflightIntervals,
    long EvaluatedAttempts, long Pass, long Fail, long InsufficientEvidence, long AwaitingEvaluation,
    long Interrupted, long InProgress, long SavedEvaluations, bool CurrentRevisionDemonstrated,
    bool CurrentRevisionDemoPass, ResearchHistoryItem? MostRecent,long CompletedCollections=0,long PartialCollections=0);
public sealed record ResearchAttemptSelection(Guid Id, ResearchAttemptKind Kind, Guid? ResultId);
public sealed record ResearchComparisonRequest(ResearchAttemptSelection[] Attempts);
public sealed record ResearchEvidenceBreak(DateTimeOffset TimestampUtc, string Kind, double? Seconds);
public sealed record ResearchEvidenceAudit(bool MatchesSavedEvidence, long RetainedSamples, long ExpectedSamples,
    long GapCount, long PauseOrConnectionEdges, long InvalidSamples, double? MaximumGapSeconds,
    TelemetrySource[] Sources, ReplaySample[] Preview, ResearchEvidenceBreak[] Breaks, bool MoreBreaks);
public sealed record ResearchComparisonEntry(ResearchHistoryItem Attempt, ResearchEvidenceAudit? Evidence,AtmosphericResearchReview? Atmosphere=null);
public sealed record ResearchComparison(PersonalResearchCard Card, ResearchComparisonEntry[] Entries,
    bool DifferentRevisions, bool DifferentRequirements, bool DifferentEvaluators, bool DifferentSources, string[] Notices);

public sealed partial class MissionStore
{
    // Read projection over existing records. A new evaluation is not a new attempt.
    // P01 manual intervals have no marker identity: identical flight/revision/UTC
    // intervals are grouped, while overlapping but different intervals stay explicit.
    const string ResearchHistorySql = """
        WITH ranked AS (
          SELECT e.*, ROW_NUMBER() OVER(PARTITION BY
            COALESCE(json_extract(payload_json,'$.recordedAttemptId'),
              'interval:'||mission_id||':'||card_revision||':'||json_extract(payload_json,'$.startUtc')||':'||json_extract(payload_json,'$.endUtc'))
            ORDER BY created_utc DESC,id DESC) AS position
          FROM personal_research_attempts e WHERE card_id=$card
        ), history AS (
          SELECT p.id,'Marked' AS kind,p.mission_id,f.card_json,p.payload_json AS marked_json,e.payload_json AS result_json,
            p.started_utc AS at,json_extract(p.payload_json,'$.endUtc') AS ended,p.status
          FROM personal_card_attempts p JOIN personal_flight_cards f ON f.mission_id=p.mission_id AND f.card_id=p.card_id
          LEFT JOIN ranked e ON json_extract(e.payload_json,'$.recordedAttemptId')=p.id AND e.position=1
          WHERE p.card_id=$card
          UNION ALL
          SELECT e.id,'PostflightInterval',e.mission_id,json_extract(e.payload_json,'$.card'),NULL,e.payload_json,
            json_extract(e.payload_json,'$.startUtc'),json_extract(e.payload_json,'$.endUtc'),'Recorded'
          FROM ranked e WHERE e.position=1 AND json_extract(e.payload_json,'$.recordedAttemptId') IS NULL
        ), selected AS (
          SELECT h.*,m.name AS flight_name,m.started_utc AS flight_started,m.status AS flight_status
          FROM history h JOIN missions m ON m.id=h.mission_id
        )
        """;

    static SqliteCommand ResearchReviewCommand(SqliteConnection db,SqliteTransaction tx,Guid card,string query)
    {
        var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText=ResearchHistorySql+query;
        cmd.Parameters.AddWithValue("$card",card.ToString());return cmd;
    }
    static ResearchHistoryItem ReadResearchHistory(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)),
        Enum.Parse<ResearchAttemptKind>(r.GetString(1)),Guid.Parse(r.GetString(2)),r.GetString(3),Parse(r.GetString(4)),
        Enum.Parse<MissionStatus>(r.GetString(5)),JsonSerializer.Deserialize<PersonalResearchCard>(r.GetString(6),WireJson.Options)!,
        Parse(r.GetString(7)),r.IsDBNull(8)?null:Parse(r.GetString(8)),
        r.IsDBNull(9)?null:JsonSerializer.Deserialize<RecordedResearchAttempt>(r.GetString(9),WireJson.Options),
        r.IsDBNull(10)?null:JsonSerializer.Deserialize<PersonalResearchAttempt>(r.GetString(10),WireJson.Options));
    const string ResearchHistoryColumns="id,kind,mission_id,flight_name,flight_started,flight_status,card_json,at,ended,marked_json,result_json";

    public ResearchHistoryPage ResearchHistory(Guid card,int offset=0)
    {
        if(offset<0)throw new ArgumentException("Invalid research history page.");
        _=ResearchCard(card);using var db=Open();using var tx=db.BeginTransaction(deferred:true);
        using var cmd=ResearchReviewCommand(db,tx,card,"SELECT COUNT(*) FROM selected");
        var total=(long)cmd.ExecuteScalar()!;
        cmd.CommandText=ResearchHistorySql+"SELECT "+ResearchHistoryColumns+" FROM selected ORDER BY at DESC,id DESC LIMIT 20 OFFSET $offset";
        cmd.Parameters.AddWithValue("$offset",offset);using var r=cmd.ExecuteReader();var rows=new List<ResearchHistoryItem>();
        while(r.Read())rows.Add(ReadResearchHistory(r));
        return new(rows.ToArray(),total,offset,offset+(long)rows.Count<total?checked(offset+rows.Count):null);
    }
    public ResearchOverview ResearchOverview(Guid cardId)
    {
        var card=ResearchCard(cardId);using var db=Open();using var tx=db.BeginTransaction(deferred:true);
        using var cmd=ResearchReviewCommand(db,tx,cardId,"""
            SELECT COUNT(*) FILTER(WHERE kind='Marked'),COUNT(*) FILTER(WHERE kind='PostflightInterval'),
              COUNT(result_json),COUNT(*) FILTER(WHERE json_extract(result_json,'$.outcome')='pass'),
              COUNT(*) FILTER(WHERE json_extract(result_json,'$.outcome')='fail'),
              COUNT(*) FILTER(WHERE json_extract(result_json,'$.outcome')='insufficientEvidence'),
              COUNT(*) FILTER(WHERE result_json IS NULL AND status<>'Recording'),COUNT(*) FILTER(WHERE status='Interrupted'),
              COUNT(*) FILTER(WHERE status='Recording'),
              (SELECT COUNT(*) FROM personal_research_attempts WHERE card_id=$card),
              COUNT(*) FILTER(WHERE json_extract(card_json,'$.revision')=$revision AND status<>'Interrupted'
                AND json_extract(result_json,'$.outcome')='pass' AND json_extract(result_json,'$.evidence.source')='simulator'
                AND json_extract(result_json,'$.evidence.sampleCount')>1
                AND (SELECT COUNT(*) FROM telemetry t WHERE t.mission_id=selected.mission_id
                  AND t.sequence BETWEEN json_extract(result_json,'$.evidence.firstSequence') AND json_extract(result_json,'$.evidence.lastSequence'))=json_extract(result_json,'$.evidence.sampleCount')),
              COUNT(*) FILTER(WHERE json_extract(card_json,'$.revision')=$revision AND status<>'Interrupted'
                AND json_extract(result_json,'$.outcome')='pass' AND json_extract(result_json,'$.evidence.source')='simulated'),
              COUNT(*) FILTER(WHERE json_extract(result_json,'$.outcome')='collectionComplete'),
              COUNT(*) FILTER(WHERE json_extract(result_json,'$.outcome')='partialCollection')
            FROM selected
            """);
        cmd.Parameters.AddWithValue("$revision",card.Revision);long[] counts=new long[14];
        using(var r=cmd.ExecuteReader()){r.Read();for(var i=0;i<counts.Length;i++)counts[i]=r.GetInt64(i);}
        cmd.CommandText=ResearchHistorySql+"SELECT "+ResearchHistoryColumns+" FROM selected ORDER BY at DESC,id DESC LIMIT 1";
        ResearchHistoryItem? recent;using(var r=cmd.ExecuteReader())recent=r.Read()?ReadResearchHistory(r):null;
        return new(card,counts[0],counts[1],counts[2],counts[3],counts[4],counts[5],counts[6],counts[7],counts[8],counts[9],counts[10]>0,counts[11]>0,recent,counts[12],counts[13]);
    }

    public ResearchComparison CompareResearch(Guid cardId,ResearchComparisonRequest request,CancellationToken token=default,ScienceStore? science=null)
    {
        if(request.Attempts is null||request.Attempts.Length is <2 or >8||request.Attempts.Any(a=>a is null||a.Id==Guid.Empty||!Enum.IsDefined(a.Kind))
            ||request.Attempts.Select(a=>(a.Kind,a.Id)).Distinct().Count()!=request.Attempts.Length)
            throw new ArgumentException("Choose 2–8 distinct attempts from this experiment. This limits one comparison, not your retained results.");
        var card=ResearchCard(cardId);using var db=Open();using var tx=db.BeginTransaction(deferred:true);
        token.ThrowIfCancellationRequested();
        var predicates=Enumerable.Range(0,request.Attempts.Length).Select(i=>$"(id=$id{i} AND kind=$kind{i})");
        using var selectionCommand=ResearchReviewCommand(db,tx,cardId,"SELECT "+ResearchHistoryColumns+" FROM selected WHERE "+string.Join(" OR ",predicates));
        for(var i=0;i<request.Attempts.Length;i++){
            selectionCommand.Parameters.AddWithValue("$id"+i,request.Attempts[i].Id.ToString());selectionCommand.Parameters.AddWithValue("$kind"+i,request.Attempts[i].Kind.ToString());
        }
        var chosen=new Dictionary<(ResearchAttemptKind,Guid),ResearchHistoryItem>();
        using(var reader=selectionCommand.ExecuteReader())while(reader.Read()){var row=ReadResearchHistory(reader);chosen.Add((row.Kind,row.Id),row);}
        var rows=new List<ResearchComparisonEntry>();
        foreach(var selection in request.Attempts){
            token.ThrowIfCancellationRequested();
            var row=chosen.GetValueOrDefault((selection.Kind,selection.Id))??throw new ArgumentException("An attempt or flight is no longer available in this experiment. Refresh its history.");
            if(row.Result?.Id!=selection.ResultId)throw new InvalidOperationException("A selected result changed. Refresh the experiment before comparing.");
            rows.Add(AtmosphericResearch.IsCollectionCard(card)?new(row,null,row.Result is {} atmospheric?ReviewAtmosphericEvidence(atmospheric,science,token):null)
                :new(row,row.Result is {} result?AuditResearchEvidence(db,tx,result,token):null));
        }
        var revisions=rows.Select(r=>r.Attempt.Card.Revision).Distinct().Count()>1;
        var requirements=rows.Select(r=>!AtmosphericResearch.IsCollectionCard(card)?PreflightReadiness.Hash(r.Attempt.Card.FlightRegime!):PreflightReadiness.Hash((object?)r.Attempt.Card.AtmosphericTransect??r.Attempt.Card.AtmosphericProfile!)).Distinct().Count()>1;
        var evaluators=rows.Where(r=>r.Attempt.Result is not null).Select(r=>r.Attempt.Result!.EvaluatorVersion).Distinct().Count()>1;
        var sources=!AtmosphericResearch.IsCollectionCard(card)?rows.SelectMany(r=>r.Evidence?.Sources??[]).Distinct().ToArray():rows.Where(r=>r.Attempt.Result?.Evidence.Source is not null).Select(r=>r.Attempt.Result!.Evidence.Source!.Value).Distinct().ToArray();
        if(AtmosphericResearch.IsCollectionCard(card)){
            if(sources.Length>1)throw new ArgumentException("Compare atmospheric profiles with the same simulator/demo provenance. Synthetic fixtures and simulator flights are not interchangeable.");
            return new(card,rows.ToArray(),revisions,requirements,evaluators,false,
                ["Only like-for-like original simulator atmospheric quantities and units are compared. Points remain separate observations; no missing altitude is interpolated or filled.",
                 "Different dates, places and changing simulator weather can explain differences. Collection completeness does not establish a hypothesis or certification.",
                 sources.Contains(TelemetrySource.Simulated)?"Synthetic/demo observations: not an installed MSFS scientific acceptance test.":"Recorded simulator observations are not independent real-world atmospheric measurements."]);
        }
        var notices=new List<string>{"Each result belongs to its own frozen card and recorded interval. No averages, success rates or performance trends are inferred. Overlapping intervals may reference the same samples and are not independent trials."};
        if(revisions)notices.Add("Different card revisions are selected. Review each target, tolerance and hold requirement; these are not automatically identical tests.");
        if(revisions&&!requirements)notices.Add("These revision numbers differ, but their numeric Flight-Regime requirements are identical. The original revision identities are still preserved.");
        if(evaluators)notices.Add("Different evaluator versions are selected. Their rules may differ.");
        if(sources.Contains(TelemetrySource.Simulated))notices.Add("Synthetic/demo telemetry is present. It does not demonstrate an installed MSFS flight or genuine scientific-source acceptance.");
        if(rows.Any(r=>r.Attempt.Result is null))notices.Add("Some attempts have not been evaluated. They have no outcome or achieved-hold result.");
        if(rows.Any(r=>r.Evidence?.MatchesSavedEvidence==false))notices.Add("Some retained evidence does not match the saved result. The historical result is preserved; its hold is not plotted as verified evidence.");
        return new(card,rows.ToArray(),revisions,requirements,evaluators,sources.Length>1,notices.ToArray());
    }

    ResearchEvidenceAudit AuditResearchEvidence(SqliteConnection db,SqliteTransaction tx,PersonalResearchAttempt result,CancellationToken token)
    {
        if(result.EndUtc<result.StartUtc||result.EndUtc-result.StartUtc>TimeSpan.FromHours(24))throw new NotSupportedException("This result exceeds the supported 24-hour evidence review window.");
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="SELECT sequence,payload_json FROM telemetry WHERE mission_id=$flight AND timestamp_utc >= $start AND timestamp_utc <= $end ORDER BY sequence";
        cmd.Parameters.AddWithValue("$flight",result.FlightId.ToString());cmd.Parameters.AddWithValue("$start",Stamp(result.StartUtc));cmd.Parameters.AddWithValue("$end",Stamp(result.EndUtc));
        long count=0,first=0,last=0,gaps=0,edges=0,invalid=0;DateTimeOffset? previous=null;double? maxGap=null;
        var sources=new HashSet<TelemetrySource>();var preview=new List<ReplaySample>();var tail=new Queue<ReplaySample>();var breaks=new List<ResearchEvidenceBreak>();
        using var pauses=PersonalPauseTimes(result.FlightId,result.StartUtc,result.EndUtc).GetEnumerator();var hasPause=pauses.MoveNext();
        void Pause(){AppendResearchBreak(hash,pauses.Current);edges++;if(breaks.Count<20)breaks.Add(new(pauses.Current,"Recorded pause, connection or gap edge",null));hasPause=pauses.MoveNext();}
        void Gap(DateTimeOffset at,double seconds,string kind){gaps++;maxGap=Math.Max(maxGap??0,seconds);if(breaks.Count<20)breaks.Add(new(at,kind,seconds));}
        using(var r=cmd.ExecuteReader())while(r.Read()){
            token.ThrowIfCancellationRequested();var sequence=r.GetInt64(0);var json=r.GetString(1);var frame=JsonSerializer.Deserialize<TelemetryFrame>(json,WireJson.Options)!;
            while(hasPause&&pauses.Current<=frame.TimestampUtc){token.ThrowIfCancellationRequested();Pause();}
            AppendResearchSample(hash,sequence,json);if(count++==0)first=sequence;last=sequence;sources.Add(frame.Source);
            if(!frame.IsValid||frame.IsPaused||!Enum.IsDefined(frame.Source)||frame.IndicatedAirspeedKnots<0)invalid++;
            if(previous is {} prior){var gap=(frame.TimestampUtc-prior).TotalSeconds;maxGap=Math.Max(maxGap??0,gap);if(gap>FlightRegimeEvaluation.MaximumGapSeconds||gap<=0)Gap(frame.TimestampUtc,gap,"Recorded sample gap or non-increasing timestamp");}
            else if((frame.TimestampUtc-result.StartUtc).TotalSeconds>FlightRegimeEvaluation.MaximumGapSeconds)Gap(frame.TimestampUtc,(frame.TimestampUtc-result.StartUtc).TotalSeconds,"No sample near the beginning of this interval");
            previous=frame.TimestampUtc;var sample=new ReplaySample(sequence,frame);if(preview.Count<6)preview.Add(sample);tail.Enqueue(sample);if(tail.Count>6)tail.Dequeue();
        }
        while(hasPause){token.ThrowIfCancellationRequested();Pause();}
        if(previous is {} end&&(result.EndUtc-end).TotalSeconds>FlightRegimeEvaluation.MaximumGapSeconds)Gap(result.EndUtc,(result.EndUtc-end).TotalSeconds,"No sample near the end of this interval");
        foreach(var sample in tail)if(preview.All(p=>p.Sequence!=sample.Sequence))preview.Add(sample);
        // Reuse the original frozen plan; no raw measurements or results are rewritten.
        cmd.CommandText="SELECT plan_snapshot_json FROM mission_research WHERE mission_id=$flight";
        var plan=cmd.ExecuteScalar() as string;
        var planHash=PreflightReadiness.Hash(plan is null?null!:JsonSerializer.Deserialize<MissionPlan>(plan,WireJson.Options)!);
        var matches=count==result.Evidence.SampleCount&&first==result.Evidence.FirstSequence&&last==result.Evidence.LastSequence
            &&Convert.ToHexString(hash.GetHashAndReset()).Equals(result.Evidence.Sha256,StringComparison.OrdinalIgnoreCase)
            &&planHash==result.Evidence.PlanSha256;
        return new(matches,count,result.Evidence.SampleCount,gaps,edges,invalid,maxGap,sources.Order().ToArray(),preview.ToArray(),breaks.ToArray(),gaps+edges>breaks.Count);
    }
    static void AppendResearchSample(IncrementalHash hash,long sequence,string json)=>hash.AppendData(Encoding.UTF8.GetBytes(sequence.ToString(CultureInfo.InvariantCulture)+":"+json+"\n"));
    static void AppendResearchBreak(IncrementalHash hash,DateTimeOffset at)=>hash.AppendData(Encoding.UTF8.GetBytes("break:"+Stamp(at)+"\n"));
}
