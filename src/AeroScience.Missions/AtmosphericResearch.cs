using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

// Uses retained instrument observations, never the aircraft's raw atmospheric context
// to backfill a science session that was not collecting.
public static class AtmosphericResearch
{
    public const string Template="vertical-atmospheric-profile",Evaluator="atmospheric-collection-1";
    public static readonly (string Instrument,string Measurement,string Unit)[] Quantities=[
        ("ambient-temperature","temperature","degC"),("ambient-pressure","pressure","inHg"),
        ("ambient-wind","wind-speed","kt"),("ambient-wind","wind-direction","degT"),("ambient-density","density","slug/ft3")];
    public static void Validate(AtmosphericResearchDefinition? rule)
    {
        if(rule is null||rule.Version!=1||string.IsNullOrWhiteSpace(rule.Question)||rule.Question.Length>500||rule.Question.Any(char.IsControl)
            ||!double.IsFinite(rule.BandWidthFeet)||rule.BandWidthFeet is <100 or >5000||rule.RequiredBands is <2 or >20
            ||rule.MinimumReadingsPerBand is <2 or >600||!double.IsFinite(rule.MaximumGapSeconds)||rule.MaximumGapSeconds is <1 or >60)
            throw new ArgumentException("Choose 2–20 neighboring altitude bands, 100–5,000 feet wide, with 2–600 valid readings per band and a 1–60 second gap limit.");
    }
    sealed class Quantity {public long Count,Excluded;public double? Min,Max;public void Add(double v){Count++;Min=Math.Min(Min??v,v);Max=Math.Max(Max??v,v);}}
    sealed class Band {public long Count,Run,Longest;public double? Min,Max;}
    public static (AtmosphericResearchSummary Summary,string Hash,ScienceObservation[] Preview,bool Downsampled) Calculate(
        IEnumerable<ScienceObservation> observations,PersonalResearchCard card,Guid library,IEnumerable<DateTimeOffset> breaks,CancellationToken token=default,ResearchAttemptRequest? window=null)
    {
        token.ThrowIfCancellationRequested();var rule=card.AtmosphericProfile;Validate(rule);var r=rule!;
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Hash(string value)=>hash.AppendData(Encoding.UTF8.GetBytes(value+"\n"));
        Hash(Evaluator);Hash(library.ToString());Hash(PreflightReadiness.Hash(card));
        if(window is not null)Hash(PreflightReadiness.Hash(new{window.FlightId,window.StartUtc,window.EndUtc}));
        var quantities=Quantities.ToDictionary(q=>q.Measurement,_=>new Quantity());var bands=new SortedDictionary<int,Band>();
        var preview=new List<ScienceObservation>();int stride=1;long rows=0,valid=0,excluded=0,gaps=0,previewCount=0;
        DateTimeOffset? previous=null;Guid? session=null;int? previousBand=null;double? minAltitude=null,maxAltitude=null;bool pendingBreak=false;
        using var edges=breaks.GetEnumerator();var hasEdge=edges.MoveNext();
        void Edge(){Hash("break:"+edges.Current.ToString("O",CultureInfo.InvariantCulture));pendingBreak|=previous is not null;previous=null;previousBand=null;hasEdge=edges.MoveNext();}
        foreach(var o in observations){
            token.ThrowIfCancellationRequested();rows++;while(hasEdge&&edges.Current<=o.TimestampUtc)Edge();
            Hash(o.Id.ToString(CultureInfo.InvariantCulture)+":"+JsonSerializer.Serialize(o,WireJson.Options));
            var q=Quantities.FirstOrDefault(q=>q.Instrument==o.InstrumentId&&q.Measurement==o.MeasurementId);
            if(q==default)continue;var quantity=quantities[o.MeasurementId];
            var usable=o.Provenance==ScienceProvenance.SimulatorReported&&o.Calibration is null&&o.Quality==ObservationQuality.Valid&&o.Unit==q.Unit
                &&double.IsFinite(o.Value)&&o.AltitudeMslFeet is {} a&&double.IsFinite(a)&&a is >=-2000 and <=100000
                &&o.PositionTimestampUtc is {} position&&(o.TimestampUtc-position).TotalSeconds is >=0 and <=3
                &&o.Latitude is {} lat&&o.Longitude is {} lon&&MissionPlanning.Position(lat,lon)&&!string.IsNullOrWhiteSpace(o.Source);
            if(!usable){quantity.Excluded++;excluded++;if(o.MeasurementId=="temperature"){pendingBreak|=previous is not null;previous=null;previousBand=null;}continue;}
            quantity.Add(o.Value);
            if(previewCount++%stride==0)preview.Add(o);
            if(preview.Count>1000){preview=preview.Where((_,i)=>i%2==0).ToList();stride=checked(stride*2);}
            if(o.MeasurementId!="temperature")continue;
            valid++;var altitude=o.AltitudeMslFeet!.Value;minAltitude=Math.Min(minAltitude??altitude,altitude);maxAltitude=Math.Max(maxAltitude??altitude,altitude);
            if(pendingBreak){gaps++;pendingBreak=false;}
            var index=(int)Math.Floor(altitude/r.BandWidthFeet);
            if(!bands.TryGetValue(index,out var band))bands[index]=band=new();
            var dt=previous is {} p?(o.TimestampUtc-p).TotalSeconds:double.NaN;
            var continuous=previous is not null&&dt>0&&dt<=r.MaximumGapSeconds&&session==o.SessionId;
            if(previous is not null&&!continuous)gaps++;
            band.Count++;band.Run=continuous&&previousBand==index?band.Run+1:1;band.Longest=Math.Max(band.Longest,band.Run);
            band.Min=Math.Min(band.Min??o.Value,o.Value);band.Max=Math.Max(band.Max??o.Value,o.Value);
            previous=o.TimestampUtc;previousBand=index;session=o.SessionId;
        }
        while(hasEdge){token.ThrowIfCancellationRequested();Edge();}
        // Bounded band summaries cover the entire supported MSL interval; no missing band is filled with values.
        var output=new List<AtmosphericResearchBand>();
        if(bands.Count>0)for(var i=bands.Keys.First();i<=bands.Keys.Last();i++){
            bands.TryGetValue(i,out var b);output.Add(new(i,i*r.BandWidthFeet,(i+1)*r.BandWidthFeet,b?.Count??0,b?.Longest??0,
                b?.Longest>=r.MinimumReadingsPerBand,b?.Min,b?.Max));
        }
        var summary=new AtmosphericResearchSummary(1,library,rows,valid,excluded,minAltitude,maxAltitude,gaps,output.ToArray(),
            Quantities.Select(q=>{var n=quantities[q.Measurement];return new AtmosphericQuantitySummary(q.Instrument,q.Measurement,q.Unit,n.Count,n.Excluded,n.Min,n.Max);}).ToArray(),false,
            ["Only retained simulator-reported instrument observations with valid quality, original units and aircraft position no older than three seconds are included. Calibrated/modelled/external values are not substituted.",
             "Bands are MSL intervals, not prescribed flight levels. Sufficient coverage requires neighboring bands, each with the chosen consecutive valid temperature readings. Pauses, session changes and gaps break a reading run.",
             "These are conditions sampled at the aircraft and recorded UTC, not unsampled altitudes or a regional atmosphere. Horizontal travel and changing simulator weather confound vertical interpretation.",
             "Collection completeness addresses evidence coverage, not proof of a hypothesis or real-world aircraft certification. Pressure, wind and density are optional and remain unavailable when absent.",
             "Chart previews are bounded retained observations, not interpolation. Original values, quality, units, session and observation identities remain available in Science Results & Analysis."]);
        return(summary,Convert.ToHexStringLower(hash.GetHashAndReset()),preview.ToArray(),stride>1);
    }
    public static (ResearchAttemptOutcome Outcome,string Explanation) Outcome(AtmosphericResearchSummary summary,AtmosphericResearchDefinition rule,bool interrupted)
    {
        var run=0;var longest=0;foreach(var b in summary.Bands){run=b.Covered?run+1:0;longest=Math.Max(longest,run);}
        if(summary.ValidTemperatureReadings==0)return(ResearchAttemptOutcome.InsufficientEvidence,"No valid altitude-associated temperature observations were retained. Recording a climb alone is not scientific collection.");
        if(longest>=rule.RequiredBands&&!interrupted)return(ResearchAttemptOutcome.CollectionComplete,$"Completed collection: {longest} neighboring bands have sufficient valid temperature evidence. This does not prove a temperature trend or hypothesis; review individual values and gaps.");
        return(ResearchAttemptOutcome.PartialCollection,$"Partial coverage: {longest} neighboring bands meet the sampling requirement; {rule.RequiredBands} are required. Missing bands and interrupted collection are not inferred from aircraft altitude.");
    }
}

public sealed partial class ScienceStore
{
    internal IEnumerable<ScienceObservation> ResearchObservationWindow(Guid flight,DateTimeOffset start,DateTimeOffset end)
    {
        if(AvailabilityError() is {} error)throw new IOException(error);if(!File.Exists(DatabasePath))yield break;
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT id,payload_json FROM observations WHERE mission_id=$id AND timestamp_utc >= $start AND timestamp_utc <= $end ORDER BY timestamp_utc,id";
        cmd.Parameters.AddWithValue("$id",flight.ToString());cmd.Parameters.AddWithValue("$start",start.ToString("O",CultureInfo.InvariantCulture));cmd.Parameters.AddWithValue("$end",end.ToString("O",CultureInfo.InvariantCulture));
        using var rows=cmd.ExecuteReader();while(rows.Read())yield return JsonSerializer.Deserialize<ScienceObservation>(rows.GetString(1),WireJson.Options)! with{Id=rows.GetInt64(0)};
    }
}
