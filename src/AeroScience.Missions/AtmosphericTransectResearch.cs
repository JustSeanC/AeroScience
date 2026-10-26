using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Shared;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Quadtree;
using Coordinates=AeroScience.Shared.Coordinates;

namespace AeroScience.Missions;

// A collection rule over the existing retained observations, not an acquisition engine.
public static class AtmosphericTransectResearch
{
    public const string Template="atmospheric-transect",Evaluator="atmospheric-transect-1";
    public static void Validate(AtmosphericTransectDefinition? r)
    {
        if(r is null||r.Version!=1||string.IsNullOrWhiteSpace(r.Question)||r.Question.Length>500||r.Question.Any(char.IsControl)
            ||!double.IsFinite(r.StationSpacingMetres)||r.StationSpacingMetres is <100 or >50000||r.RequiredStations is <2 or >100
            ||r.MinimumReadingsPerStation is <2 or >600||!double.IsFinite(r.MinimumExtentMetres)||r.MinimumExtentMetres is <100 or >1000000
            ||!double.IsFinite(r.MaximumGapSeconds)||r.MaximumGapSeconds is <1 or >60||!double.IsFinite(r.MaximumStepMetres)||r.MaximumStepMetres is <100 or >100000)
            throw new ArgumentException("Choose 2–100 sampling locations, 100–50,000 m apart, 2–600 readings per location, 100–1,000,000 m extent, a 1–60 second gap and 100–100,000 m maximum recorded step.");
    }
    sealed class Quantity {public long Count,Excluded;public double? Min,Max;public void Add(double v){Count++;Min=Math.Min(Min??v,v);Max=Math.Max(Max??v,v);}}
    sealed class Station(int index,ScienceObservation anchor){public int Index=index;public ScienceObservation Anchor=anchor;public long Count,Longest;}
    sealed class Leg(int id,ScienceObservation first){public int Id=id;public ScienceObservation First=first,Last=first;public long Count;public double Distance,Extent;public HashSet<int> Covered=[];}
    public static (AtmosphericResearchSummary Summary,string Hash,ScienceObservation[] Preview,bool Downsampled,AtmosphericTransectPoint[]? TransectPoints) Calculate(
        IEnumerable<ScienceObservation> observations,PersonalResearchCard card,Guid library,IEnumerable<DateTimeOffset> breaks,CancellationToken token,ResearchAttemptRequest? window)
    {
        Validate(card.AtmosphericTransect);var r=card.AtmosphericTransect!;token.ThrowIfCancellationRequested();
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Hash(string s)=>hash.AppendData(Encoding.UTF8.GetBytes(s+"\n"));
        Hash(Evaluator);Hash(library.ToString());Hash(PreflightReadiness.Hash(card));if(window is not null)Hash(PreflightReadiness.Hash(new{window.FlightId,window.StartUtc,window.EndUtc}));
        var quantities=AtmosphericResearch.Quantities.ToDictionary(q=>q.Measurement,_=>new Quantity());
        var stations=new List<Station>();var locationIndex=new ResearchLocationIndex(r.StationSpacingMetres);var legs=new List<AtmosphericTransectLeg>();
        var datasets=new HashSet<Guid>();var sessions=new HashSet<Guid>();var preview=new List<(ScienceObservation Observation,AtmosphericTransectPoint Point)>();
        long rows=0,valid=0,excluded=0,gaps=0,previewCount=0,totalLegs=0;int stride=1,mostStations=0;int? completeLeg=null;
        ScienceObservation? previous=null;Leg? leg=null;Station? previousStation=null;long run=0;double totalDistance=0,greatestExtent=0;double? minAlt=null,maxAlt=null;
        using var edges=breaks.GetEnumerator();var hasEdge=edges.MoveNext();bool pendingBreak=false;
        static Coordinates Position(ScienceObservation o)=>new(o.Latitude!.Value,o.Longitude!.Value);
        double Distance(ScienceObservation a,ScienceObservation b)=>Geodesy.Distance(Position(a),Position(b));
        void FinishLeg(){if(leg is null)return;mostStations=Math.Max(mostStations,leg.Covered.Count);greatestExtent=Math.Max(greatestExtent,leg.Extent);
            if(leg.Covered.Count>=r.RequiredStations&&leg.Extent>=r.MinimumExtentMetres)completeLeg??=leg.Id;
            if(legs.Count<1000)legs.Add(new(leg.Id,leg.First.Id,leg.Last.Id,leg.Count,leg.Covered.Count,leg.Distance,leg.Extent));leg=null;}
        Station Locate(ScienceObservation o){var location=locationIndex.Locate(Position(o));if(location==stations.Count)stations.Add(new(location,o));return stations[location];}
        void Process(List<ScienceObservation> group){
            if(group.Count==0)return;var at=group[0].TimestampUtc;
            while(hasEdge&&edges.Current<=at){Hash("break:"+edges.Current.ToString("O",CultureInfo.InvariantCulture));pendingBreak|=previous is not null;hasEdge=edges.MoveNext();}
            var usable=new List<ScienceObservation>();
            foreach(var o in group){
                token.ThrowIfCancellationRequested();rows++;Hash(o.Id.ToString(CultureInfo.InvariantCulture)+":"+JsonSerializer.Serialize(o,WireJson.Options));
                var q=AtmosphericResearch.Quantities.FirstOrDefault(q=>q.Instrument==o.InstrumentId&&q.Measurement==o.MeasurementId);if(q==default)continue;
                if(!AtmosphericResearch.Usable(o,q.Unit)){quantities[q.Measurement].Excluded++;excluded++;if(q.Measurement=="temperature")pendingBreak|=previous is not null;continue;}
                quantities[q.Measurement].Add(o.Value);datasets.Add(o.DatasetId);if(o.SessionId is {} session)sessions.Add(session);usable.Add(o);
            }
            var positions=new Dictionary<long,AtmosphericTransectPoint>();
            foreach(var o in usable.Where(o=>o.MeasurementId=="temperature")){
                var dt=previous is null?0:(o.TimestampUtc-previous.TimestampUtc).TotalSeconds;var step=previous is null?0:Distance(previous,o);
                var broken=previous is null||pendingBreak||dt<=0||dt>r.MaximumGapSeconds||o.SessionId!=previous.SessionId||step>r.MaximumStepMetres;
                if(broken){if(previous is not null)gaps++;FinishLeg();leg=new(checked((int)++totalLegs),o);previousStation=null;run=0;}
                else{totalDistance+=step;leg!.Distance+=step;}
                pendingBreak=false;valid++;minAlt=Math.Min(minAlt??o.AltitudeMslFeet!.Value,o.AltitudeMslFeet!.Value);maxAlt=Math.Max(maxAlt??o.AltitudeMslFeet!.Value,o.AltitudeMslFeet!.Value);
                var station=Locate(o);station.Count++;run=previousStation==station?run+1:1;station.Longest=Math.Max(station.Longest,run);
                leg!.Count++;leg.Last=o;leg.Extent=Math.Max(leg.Extent,Distance(leg.First,o));if(run>=r.MinimumReadingsPerStation)leg.Covered.Add(station.Index);
                foreach(var sample in usable.Where(s=>s.Latitude==o.Latitude&&s.Longitude==o.Longitude&&s.PositionTimestampUtc==o.PositionTimestampUtc&&s.SessionId==o.SessionId))
                    positions.TryAdd(sample.Id,new(sample.Id,totalDistance,leg.Id,station.Index));
                previous=o;previousStation=station;
            }
            foreach(var o in usable){if(previewCount++%stride==0)preview.Add((o,positions.GetValueOrDefault(o.Id)??new(o.Id,null,null,null)));
                if(preview.Count>1000){preview=preview.Where((_,i)=>i%2==0).ToList();stride=checked(stride*2);}}
        }
        var packet=new List<ScienceObservation>();DateTimeOffset? packetAt=null;
        foreach(var o in observations){token.ThrowIfCancellationRequested();if(packetAt is not null&&o.TimestampUtc!=packetAt){Process(packet);packet.Clear();}packetAt=o.TimestampUtc;
            if(packet.Count>=128)throw new InvalidOperationException("Too many atmospheric observations share one timestamp. Inspect a shorter valid source interval.");packet.Add(o);}
        Process(packet);FinishLeg();while(hasEdge){token.ThrowIfCancellationRequested();Hash("break:"+edges.Current.ToString("O",CultureInfo.InvariantCulture));hasEdge=edges.MoveNext();}
        var transect=new AtmosphericTransectSummary(1,stations.Count,stations.Count(s=>s.Longest>=r.MinimumReadingsPerStation),totalDistance,greatestExtent,mostStations,completeLeg,totalLegs,
            stations.Take(1000).Select(s=>new AtmosphericTransectStation(s.Index,s.Anchor.Id,s.Count,s.Longest,s.Longest>=r.MinimumReadingsPerStation)).ToArray(),legs.ToArray(),stations.Count>1000||totalLegs>1000,datasets.Count,sessions.Count);
        var summary=new AtmosphericResearchSummary(1,library,rows,valid,excluded,minAlt,maxAlt,gaps,[],
            AtmosphericResearch.Quantities.Select(q=>{var n=quantities[q.Measurement];return new AtmosphericQuantitySummary(q.Instrument,q.Measurement,q.Unit,n.Count,n.Excluded,n.Min,n.Max);}).ToArray(),false,
            ["Only retained valid simulator-reported instrument measurements with original units and aircraft positions no older than three seconds are used. Raw flight context never fills missing science.",
             "Sampling locations are greedy WGS84 stations separated by the chosen minimum spacing. Revisits reuse a station; repeated stationary readings cannot create geographic coverage. Stations do not represent measured surrounding areas.",
             "One supported leg must meet both the covered-location and distance-from-its-first-sample goals. Each location requires consecutive temperature readings. Recorded pause/connection edges, invalid temperature, session changes, time gaps and steps above the configured analysis limit break a leg. This limit is not an aircraft operating limit.",
             "Distance includes only supported links between original temperature samples. Unmeasured gaps are not added. Optional quantities plot against distance only when their original position/time/session matches a valid temperature sample.",
             "Altitude differences, elapsed time, weather changes and measurement configurations can explain differences; geographic correlation is not causation or a controlled experiment. No continuous atmospheric field, hypothesis proof or certification is inferred.",
             "Map and chart previews are bounded original points. Connecting recorded locations illustrates their order, not observations between them. Original UTC, units, quality, source, dataset and session references remain in Science Results & Analysis."],transect);
        return(summary,Convert.ToHexStringLower(hash.GetHashAndReset()),preview.Select(p=>p.Observation).ToArray(),stride>1,preview.Select(p=>p.Point).ToArray());
    }
    public static (ResearchAttemptOutcome Outcome,string Explanation) Outcome(AtmosphericResearchSummary summary,AtmosphericTransectDefinition r,bool interrupted)
    {
        if(summary.ValidTemperatureReadings==0)return(ResearchAttemptOutcome.InsufficientEvidence,"No valid geographically associated temperature science was retained. An aircraft track alone is not an atmospheric transect.");
        if(summary.Transect?.CompleteLegId is {} id&&!interrupted)return(ResearchAttemptOutcome.CollectionComplete,$"Completed collection: supported leg {id} meets {r.RequiredStations} sampled locations and {r.MinimumExtentMetres} m geographic extent. This establishes collection coverage, not a meteorological hypothesis.");
        return(ResearchAttemptOutcome.PartialCollection,"Partial geographic coverage: no completed, uninterrupted attempt establishes both sampling goals. Review locations, extent, gaps and optional missing quantities; no geographic measurements are inferred.");
    }
}
