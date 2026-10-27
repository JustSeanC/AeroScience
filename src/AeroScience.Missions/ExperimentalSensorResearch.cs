using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

// A small typed analysis of retained science, not an acquisition driver or scripting engine.
public static class ExperimentalSensorResearch
{
    public const string Template="experimental-sensor-checkout",Evaluator="modeled-air-temperature-1";
    public static void Validate(ExperimentalSensorDefinition? r)
    {
        if(r is null||r.Version!=1||string.IsNullOrWhiteSpace(r.Question)||r.Question.Length>500||r.Question.Any(char.IsControl)
            ||new[]{r.ResponseSeconds,r.Gain,r.OffsetCelsius,r.MinimumInputCelsius,r.MaximumInputCelsius,r.SamplingSeconds,r.MinimumDurationSeconds,r.MinimumInputSpanCelsius,r.MaximumTrackingErrorCelsius,r.MaximumGapSeconds}.Any(n=>!double.IsFinite(n))
            ||r.ResponseSeconds is <.1 or >120||r.Gain is <.1 or >3||r.OffsetCelsius is <-50 or >50
            ||r.MinimumInputCelsius is <-150 or >100||r.MaximumInputCelsius is <-100 or >150||r.MinimumInputCelsius>=r.MaximumInputCelsius
            ||r.SamplingSeconds is <1 or >30||r.MaximumGapSeconds is <1 or >60||r.SamplingSeconds>r.MaximumGapSeconds
            ||r.MinimumSamples is <3 or >3600||r.MinimumDurationSeconds is <2 or >3600||r.MinimumInputSpanCelsius is <0 or >100||r.MaximumTrackingErrorCelsius is <.001 or >200)
            throw new ArgumentException("Choose a supported finite temperature model: response 0.1–120 s, gain 0.1–3, offset ±50 °C; ordered input limits within −150…150 °C; sample 1–30 s, gap 1–60 s (not shorter than sampling), 3–3,600 samples, 2–3,600 s duration, input span 0–100 °C and tracking error 0.001–200 °C.");
    }
    public static readonly string[] Limitations=[
        "Fictional model-derived output, not a simulator-reported instrument, measured hardware accuracy or certification. Only original retained ambient-temperature / temperature readings in degC with valid quality, simulator provenance, no calibration and a source identity are inputs. Position is not required or invented.",
        "Version 1: target = gain × input + offset. At each segment's first accepted sample, output = target. Later output = previousTarget + (previousOutput − previousTarget) × exp(−elapsedSeconds / responseSeconds): causal zero-order hold of the previous sampled target. No interpolation of recorded inputs is claimed.",
        "Select the first valid original reading, then the first original reading at least SamplingSeconds later. No synthetic timestamps, noise, latency or inferred inputs. Invalid/out-of-range readings, non-increasing times, session changes, recorded pause/connection edges and gaps beyond MaximumGapSeconds reset state and sampling. Reset points are not dynamic-response evidence.",
        "Pass requires one unbroken supported interval near both requested boundaries, enough modeled samples/duration/input span, and maximum absolute output-to-current-calibrated-target deviation within the declared tracking limit. This is a fictional model tracking criterion, not independently measured detector accuracy. Incomplete coverage cannot pass.",
        "Preview points are bounded; all original temperature inputs and break events contribute to the evidence hash and assessment. Full originals remain in Science Results. Deleted collected science cannot be regenerated from raw telemetry."
    ];
    public static (ExperimentalSensorSummary Summary,string Hash,ExperimentalSensorPoint[] Preview,bool Downsampled) Calculate(
        IEnumerable<ScienceObservation> observations,PersonalResearchCard card,Guid library,IEnumerable<DateTimeOffset> breaks,ResearchAttemptRequest window,CancellationToken token=default)
    {
        Validate(card.ExperimentalSensor);var r=card.ExperimentalSensor!;token.ThrowIfCancellationRequested();
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Hash(string s)=>hash.AppendData(Encoding.UTF8.GetBytes(s+"\n"));
        Hash(Evaluator);Hash(library.ToString());Hash(PreflightReadiness.Hash(card));Hash(PreflightReadiness.Hash(new{window.FlightId,window.StartUtc,window.EndUtc}));
        long originals=0,valid=0,excluded=0,count=0,segments=0,edgesCount=0;double duration=0;double? min=null,max=null,error=null;
        ScienceObservation? previous=null,lastSample=null;DateTimeOffset? first=null,last=null;double output=0,target=0;
        var preview=new List<ExperimentalSensorPoint>();long stride=1;
        using var edges=breaks.GetEnumerator();var hasEdge=edges.MoveNext();
        void Reset(){previous=null;lastSample=null;}
        void Edge(){Hash("break:"+edges.Current.ToString("O",CultureInfo.InvariantCulture));if(edges.Current>window.StartUtc&&edges.Current<window.EndUtc){edgesCount++;Reset();}hasEdge=edges.MoveNext();}
        foreach(var o in observations){
            token.ThrowIfCancellationRequested();while(hasEdge&&edges.Current<=o.TimestampUtc)Edge();
            if(o.InstrumentId!="ambient-temperature"||o.MeasurementId!="temperature")continue;
            originals++;Hash(JsonSerializer.Serialize(o,WireJson.Options));
            if(o.MissionId!=window.FlightId||o.TimestampUtc<window.StartUtc||o.TimestampUtc>window.EndUtc||o.TimestampUtc.Offset!=TimeSpan.Zero
                ||o.Id<=0||o.DatasetId==Guid.Empty||o.Provenance!=ScienceProvenance.SimulatorReported||o.Calibration is not null||o.Quality!=ObservationQuality.Valid
                ||o.Unit!="degC"||!double.IsFinite(o.Value)||o.Value<r.MinimumInputCelsius||o.Value>r.MaximumInputCelsius||string.IsNullOrWhiteSpace(o.Source)){
                excluded++;Reset();continue;
            }
            valid++;
            if(previous is {} prior&&((o.TimestampUtc-prior.TimestampUtc).TotalSeconds is var dt&&(dt<=0||dt>r.MaximumGapSeconds)||o.SessionId!=prior.SessionId||o.DatasetId!=prior.DatasetId||o.Source!=prior.Source)){
                edgesCount++;Reset();
            }
            previous=o;
            if(lastSample is {} sampled&&(o.TimestampUtc-sampled.TimestampUtc).TotalSeconds<r.SamplingSeconds)continue;
            var nextTarget=r.Gain*o.Value+r.OffsetCelsius;
            if(lastSample is {} priorSample){var elapsed=(o.TimestampUtc-priorSample.TimestampUtc).TotalSeconds;output=target+(output-target)*Math.Exp(-elapsed/r.ResponseSeconds);duration+=elapsed;}
            else{segments++;output=nextTarget;}
            target=nextTarget;lastSample=o;first??=o.TimestampUtc;last=o.TimestampUtc;
            min=Math.Min(min??o.Value,o.Value);max=Math.Max(max??o.Value,o.Value);var deviation=Math.Abs(output-target);error=Math.Max(error??deviation,deviation);
            var point=new ExperimentalSensorPoint(o.Id,o.DatasetId,o.SessionId,o.TimestampUtc,o.Value,target,output,deviation,segments,o.Source,o.Provenance,o.Quality);
            if(count++%stride==0)preview.Add(point);
            if(preview.Count>1000){preview=preview.Where((_,i)=>i%2==0).ToList();stride=checked(stride*2);}
        }
        while(hasEdge){token.ThrowIfCancellationRequested();Edge();}
        var covered=segments==1&&edgesCount==0&&excluded==0&&first is {} a&&last is {} b
            &&(a-window.StartUtc).TotalSeconds<=r.MaximumGapSeconds&&(window.EndUtc-b).TotalSeconds<=r.MaximumGapSeconds;
        return(new(1,library,originals,valid,excluded,count,segments,edgesCount,duration,min,max,error,covered,Limitations),Convert.ToHexStringLower(hash.GetHashAndReset()),preview.ToArray(),stride>1);
    }
    public static (ResearchAttemptOutcome Outcome,string Explanation) Outcome(ExperimentalSensorSummary s,ExperimentalSensorDefinition r,bool interrupted)
    {
        if(s.ModeledSamples<2)return(ResearchAttemptOutcome.InsufficientEvidence,"Insufficient original temperature evidence for a modeled response. Start scientific collection; raw flight telemetry is never substituted.");
        if(interrupted||!s.IntervalCovered||s.ModeledSamples<r.MinimumSamples||s.SupportedSeconds<r.MinimumDurationSeconds||s.MaximumInputCelsius-s.MinimumInputCelsius<r.MinimumInputSpanCelsius)
            return(ResearchAttemptOutcome.PartialCollection,"Partial checkout: original input coverage, uninterrupted duration, sample count or temperature variation is insufficient. Gaps, resets and paused/interrupted attempts cannot establish a pass.");
        return s.MaximumTrackingErrorCelsius<=r.MaximumTrackingErrorCelsius
            ?(ResearchAttemptOutcome.Pass,"Declared fictional-model tracking criterion met on sufficient retained original inputs. This is not evidence of actual hardware accuracy or a simulator instrument.")
            :(ResearchAttemptOutcome.Fail,"Declared fictional-model tracking limit exceeded on sufficient original inputs. Inspect the modeled lag and frozen configuration; this is not a hardware-accuracy test.");
    }
}

public sealed partial class MissionStore
{
    public ExperimentalSensorReview ReviewExperimentalSensorEvidence(PersonalResearchAttempt result,ScienceStore? science,CancellationToken token=default)
    {
        if(result.ExperimentalSensor is null||result.Card.TemplateId!=ExperimentalSensorResearch.Template||result.EvaluatorVersion!=ExperimentalSensorResearch.Evaluator||result.Card.Version!=6)
            throw new NotSupportedException("Select a supported experimental sensor result.");
        if(result.EndUtc<result.StartUtc||result.EndUtc-result.StartUtc>TimeSpan.FromHours(24))throw new NotSupportedException("Select a sensor interval of up to 24 hours.");
        if(ScienceDeleted(result.FlightId))throw new InvalidOperationException("Original collected science and dependent modeled results were permanently deleted.");
        if(science is null)throw new InvalidOperationException("Reconnect the Scientific Data Library to inspect original sensor inputs.");
        using var lease=science.BeginAnalysis();
        var c=ExperimentalSensorResearch.Calculate(science.ResearchObservationWindow(result.FlightId,result.StartUtc,result.EndUtc),result.Card,science.LibraryId,
            PersonalPauseTimes(result.FlightId,result.StartUtc,result.EndUtc,true),new(result.Id,result.FlightId,result.StartUtc,result.EndUtc),token);
        var interrupted=Detail(result.FlightId)?.Mission.Status!=MissionStatus.Completed||result.RecordedAttemptId is {} marked&&RecordedResearchAttempt(marked).Status==ResearchAttemptStatus.Interrupted;
        var outcome=ExperimentalSensorResearch.Outcome(c.Summary,result.Card.ExperimentalSensor!,interrupted);
        var matches=result.Evidence.Sha256==c.Hash&&result.Evidence.PlanSha256==PreflightReadiness.Hash(Research(result.FlightId).PlanSnapshot!)
            &&PreflightReadiness.Hash(result.ExperimentalSensor with{Limitations=[]})==PreflightReadiness.Hash(c.Summary with{Limitations=[]})&&result.Outcome==outcome.Outcome;
        return new(matches,c.Summary,matches?c.Preview:[],c.Downsampled,matches?"Original input, frozen definition, event and plan hashes match. Plotted sensor outputs are reproducible model-derived values; inputs are retained simulator observations.":"Retained original evidence changed or is missing. No modeled or input plot is presented as verified. The saved result remains historical.");
    }
}
