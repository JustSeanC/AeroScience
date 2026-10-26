using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AeroScience.Shared;

namespace AeroScience.Missions;

// Assessment of existing observations/products. No provider, download, import or raster reads.
public static class SurveyResearch
{
    public const string Template="terrain-temperature-survey",Evaluator="terrain-temperature-survey-1";
    public static string Instrument(SurveyMeasurement m)=>m switch{SurveyMeasurement.AirTemperature=>"ambient-temperature",SurveyMeasurement.TerrainElevation=>"terrain-scanner",_=>"thermal-imager"};
    public static string Label(SurveyMeasurement m)=>m switch{SurveyMeasurement.AirTemperature=>"Air Temperature",SurveyMeasurement.TerrainElevation=>"Terrain Elevation",_=>"Land Surface Temperature"};
    public static void Validate(SurveyResearchDefinition? r)
    {
        if(r is null||r.Version!=1||r.Measurements is not{Length:>=1 and <=3}||r.Measurements.Any(m=>!Enum.IsDefined(m))||r.Measurements.Distinct().Count()!=r.Measurements.Length
            ||string.IsNullOrWhiteSpace(r.Question)||r.Question.Length>500||r.Question.Any(char.IsControl)||r.SamplingStrategy!="recorded-locations"
            ||r.MinimumValidSamples is <2 or >100000||r.MinimumLocations is <1 or >100||!double.IsFinite(r.LocationSpacingMetres)||r.LocationSpacingMetres is <1 or >50000
            ||r.MaximumSourceAgeDays is <1 or >36500)
            throw new ArgumentException("Select one to three distinct survey measurements, 2–100,000 valid samples, 1–100 locations spaced 1–50,000 m apart, and an optional 1–36,500 day source-age limit.");
    }
    public static string? TerrainDatum(string datum)=>datum switch{
        "NAVD88"=>"NAVD88","EGM96"=>"EGM96","EGM2008"=>"EGM2008","WGS84 ellipsoid"=>"WGS84 ellipsoid",
        "EGM2008 geoid (DSM, not bare-earth)"=>"EGM2008 geoid (DSM, not bare-earth)",_=>null};
    sealed class Group(string key,string unit,string datum,double spacing){public string Key=key,Unit=unit,Datum=datum;public long Count;public double Min=double.PositiveInfinity,Max=double.NegativeInfinity;public ResearchLocationIndex Locations=new(spacing);}
    sealed class Objective(SurveyMeasurement measurement){public SurveyMeasurement Measurement=measurement;public long Valid,Excluded,Unavailable,Seen;public int Stride=1;public Dictionary<string,Group> Groups=[];public Dictionary<string,double> Unique=[];public List<SurveyResearchSample> Preview=[];public HashSet<string> Issues=[];}
    public sealed record Calculation(SurveyResearchSummary Summary,string Hash,SurveyResearchSample[] Preview,bool Downsampled);
    public static Calculation Calculate(MissionStore missions,ScienceStore science,PersonalResearchCard card,ResearchAttemptRequest window,IEnumerable<DateTimeOffset> breaks,CancellationToken token=default)
    {
        Validate(card.Survey);var rule=card.Survey!;token.ThrowIfCancellationRequested();
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);void Hash(object value)=>hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value,WireJson.Options)+"\n"));
        Hash(Evaluator);Hash(science.LibraryId);Hash(card);Hash(new{window.FlightId,window.StartUtc,window.EndUtc});
        var objectives=rule.Measurements.Order().ToDictionary(m=>m,m=>new Objective(m));var sources=new Dictionary<Guid,SurveySourceSummary>();var sourceIds=new HashSet<Guid>();long rows=0,products=0,gaps=0;
        foreach(var edge in breaks){token.ThrowIfCancellationRequested();Hash(new{Break=edge});gaps++;}
        void Add(Objective o,SurveyResearchSample sample,string compatibility,string key){
            sample=sample with{Compatibility=compatibility};
            if(o.Unique.TryGetValue(key,out var previous)){if(previous!=sample.Value)throw new InvalidOperationException("Overlapping retained samples disagree for the same original scene/pixel and scientific reference. Review source versions; no conflicting values were silently pooled.");return;}o.Unique.Add(key,sample.Value);if(o.Unique.Count>200000)throw new InvalidOperationException("This survey analysis exceeds 200,000 distinct samples per measurement. Choose a shorter evidence interval; retained data is not limited.");
            if(!o.Groups.TryGetValue(compatibility,out var group)){if(o.Groups.Count>=128)throw new InvalidOperationException("Choose a shorter interval with at most 128 compatible source groups.");o.Groups[compatibility]=group=new(compatibility,sample.Unit,sample.Datum,rule.LocationSpacingMetres);}
            group.Count++;group.Locations.Locate(sample.Position);group.Min=Math.Min(group.Min,sample.Value);group.Max=Math.Max(group.Max,sample.Value);o.Valid++;
            if(o.Seen++%o.Stride==0)o.Preview.Add(sample);if(o.Preview.Count>1000){o.Preview=o.Preview.Where((_,i)=>i%2==0).ToList();o.Stride=checked(o.Stride*2);}
        }
        if(objectives.TryGetValue(SurveyMeasurement.AirTemperature,out var air)){
            DateTimeOffset? previous=null;Guid? session=null;
            foreach(var o in science.ResearchObservationWindow(window.FlightId,window.StartUtc,window.EndUtc).Where(o=>o.InstrumentId=="ambient-temperature"&&o.MeasurementId=="temperature")){
                token.ThrowIfCancellationRequested();Hash(o);rows++;
                if(!AtmosphericResearch.Usable(o,"degC")){air.Excluded++;air.Issues.Add("Air readings require valid original simulator quality, units and a fresh recorded aircraft position/altitude.");continue;}
                if(previous is {} p&&((o.TimestampUtc-p).TotalSeconds is <=0 or >5||o.SessionId!=session))gaps++;
                previous=o.TimestampUtc;session=o.SessionId;
                Add(air,new(SurveyMeasurement.AirTemperature,o.Value,o.Unit,new(o.Latitude!.Value,o.Longitude!.Value),o.TimestampUtc,null,null,o.Id,null,null,null,"Aircraft MSL context; air temperature has no vertical datum"),"simulator-air-degC",o.TimestampUtc.ToString("O",CultureInfo.InvariantCulture));
            }
        }
        if(objectives.Keys.Any(m=>m!=SurveyMeasurement.AirTemperature))foreach(var p in science.ResearchProductWindow(window.FlightId,window.StartUtc,window.EndUtc)){
            token.ThrowIfCancellationRequested();if(p.FlightPacketUtc<window.StartUtc||p.FlightPacketUtc>window.EndUtc)continue;
            var kind=p.InstrumentId=="terrain-scanner"?SurveyMeasurement.TerrainElevation:p.InstrumentId=="thermal-imager"?SurveyMeasurement.LandSurfaceTemperature:(SurveyMeasurement?)null;
            if(kind is null||!objectives.TryGetValue(kind.Value,out var objective))continue;
            products++;Hash(p);if(products>200000)throw new InvalidOperationException("Choose a shorter survey interval with at most 200,000 acquisition products.");
            try{
                if(p.Bytes>33554432||p.LibraryId!=science.LibraryId||p.SourceClass!=ScienceProvenance.ExternalDataset)throw new InvalidDataException("Unsupported scientific product size, identity or derived origin.");
                var data=science.ReadProduct(p.Id);var m=data.Source.Manifest;var band=data.Bands.SingleOrDefault(b=>b.Id==(kind==SurveyMeasurement.TerrainElevation?m.Bands[0].Id:"ST_B10"))??throw new InvalidDataException("Required original numerical band is missing.");
                var sourceBand=m.Bands.SingleOrDefault(b=>b.Id==band.Id);
                if(sourceBand is null||sourceBand.BadBand||sourceBand.Unit!=band.Unit||data.Source.ManifestSha256 is not{Length:64}||!data.Source.ManifestSha256.All(Uri.IsHexDigit))throw new InvalidDataException("Original band quality, units or source-hash provenance is unavailable.");
                if(data.FormatVersion!=1||p.SourceId!=data.Source.Id||data.SampleLocations.Length is <1 or >100000||data.Bands.Any(b=>b.Values.Length!=data.SampleLocations.Length))throw new InvalidDataException("Incompatible original sample/source geometry.");
                if(p.IntentId is {} intent){var retained=missions.ScienceIntent(intent);Hash(new{Intent=intent,retained});if(retained is not{State:"Published"}||retained.ProductId!=p.Id||retained.ProductSha256!=p.Sha256||retained.Intent.SessionId!=p.SessionId)throw new InvalidDataException("An original published session/intent receipt is unavailable; post-flight reconstructions are not live collection.");}
                sourceIds.Add(data.Source.Id);if(sources.Count<128&&!sources.ContainsKey(data.Source.Id))sources.Add(data.Source.Id,new(data.Source.Id,m.Name,m.Provider,m.Citation,m.SceneId,m.ObservationUtc,m.VerticalDatum,band.Unit,band.OriginalGrid,data.Source.ManifestSha256,PreflightReadiness.Hash(data.Source.Files)));
                if(rule.MaximumSourceAgeDays is {} age&&(m.ObservationUtc is not {} observed||observed>p.FlightPacketUtc||(p.FlightPacketUtc-observed).TotalDays>age))throw new InvalidDataException("Source observation date is unknown or outside the selected age requirement; publication date is not substituted.");
                string datum,compatibility;double?[] values;double?[]? uncertainty=null;
                if(kind==SurveyMeasurement.TerrainElevation){
                    datum=TerrainDatum(m.VerticalDatum)??throw new InvalidDataException("Terrain vertical datum is unsupported or unknown. Raw source-native elevations remain viewable; no MSL equivalence or valid datum is invented.");
                    if(m.Kind!=RemoteKind.Terrain||band.Unit!="m"||m.Bands.Length!=1)throw new InvalidDataException("Terrain objective requires original DEM/DSM metres, not LiDAR returns.");
                    values=band.Values;compatibility=PreflightReadiness.Hash(new{datum,band.Unit,band.OriginalGrid.Crs,band.OriginalGrid.PixelWidth,band.OriginalGrid.PixelHeight,band.OriginalGrid.RowX,band.OriginalGrid.ColumnY,Product=m.Acquisition?.Workflow??("manual:"+data.Source.ManifestSha256)});
                }else{
                    datum="Historical land surface; not aircraft air temperature";
                    if(m.Kind!=RemoteKind.Thermal||band.Unit!="K"||m.ObservationUtc is null||!Regex.IsMatch(m.SceneId,@"^L[COT]0[89]_L2SP_\d{6}_\d{8}_(?:\d{8}_)?02_T[12]$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50)))throw new InvalidDataException("Surface temperature requires a dated supported Landsat Collection 2 L2SP scene and original Kelvin values.");
                    var original=m.Bands.SingleOrDefault(b=>b.Id=="ST_B10");if(original is null||original.Scale!=.00341802||original.Offset!=149)throw new InvalidDataException("Landsat scaling metadata is unsupported.");
                    var copies=data.Bands.Select(b=>b with{Values=b.Values.ToArray()}).ToArray();
                    foreach(var id in new[]{"QA_PIXEL","QA_RADSAT"}){var qa=copies.SingleOrDefault(b=>b.Id==id);if(qa is null||qa.Unit!="1"||PreflightReadiness.Hash(qa.OriginalGrid)!=PreflightReadiness.Hash(band.OriginalGrid))throw new InvalidDataException("Required aligned original Landsat fill/cloud/saturation QA is missing or incompatible.");}
                    values=RemoteCalculations.Apply(RemoteKind.Thermal,copies)["temperature"];
                    var uncertaintyBand=data.Bands.SingleOrDefault(b=>b.Id=="ST_QA");var uncertaintyMetadata=m.Bands.SingleOrDefault(b=>b.Id=="ST_QA");if(uncertaintyBand?.Unit=="K"&&uncertaintyMetadata is{Unit:"K",Scale:.01,Offset:0,BadBand:false})uncertainty=uncertaintyBand.Values;
                    if(uncertainty is null)objective.Issues.Add("Per-pixel ST_QA uncertainty was not retained by this source. No numerical uncertainty was invented.");
                    compatibility="landsat-c2-l2sp-K";
                }
                var grid=band.OriginalGrid;var pixelIdentity=PreflightReadiness.Hash(new{m.Provider,m.SceneId,m.ObservationUtc,m.VerticalDatum,m.Kind,band.Unit,sourceBand.Scale,sourceBand.Offset,grid.Crs,grid.PixelWidth,grid.PixelHeight,grid.RowX,grid.ColumnY});
                var precision=new[]{grid.PixelWidth,grid.PixelHeight,grid.RowX,grid.ColumnY}.Select(Math.Abs).Where(v=>v>0).Min()*1e-8;
                for(var i=0;i<values.Length;i++){
                    token.ThrowIfCancellationRequested();var point=data.SampleLocations[i];
                    if(values[i] is not {} value||!double.IsFinite(value)||sourceBand.NoData is {} nodata&&value==nodata*sourceBand.Scale+sourceBand.Offset||!MissionPlanning.Position(point.Latitude,point.Longitude)){objective.Excluded++;continue;}
                    try{var pixel=ScientificSupport.Pixel(grid,point);
                        // Normalize derived identity to 1e-8 of a native pixel, never the
                        // original numeric values or recorded coordinates. Overlapping
                        // cropped originals of the same scene cannot add extra science.
                        var x=grid.OriginX+(pixel.Column+.5)*grid.PixelWidth+(pixel.Row+.5)*grid.RowX;var y=grid.OriginY+(pixel.Row+.5)*grid.PixelHeight+(pixel.Column+.5)*grid.ColumnY;
                        var key=pixelIdentity+":"+Math.Round(x/precision).ToString("R",CultureInfo.InvariantCulture)+":"+Math.Round(y/precision).ToString("R",CultureInfo.InvariantCulture);
                        Add(objective,new(kind.Value,value,band.Unit,point,p.FlightPacketUtc,m.ObservationUtc,data.Source.Id,null,p.Id,pixel.Column,pixel.Row,datum,uncertainty?[i] is {} u&&double.IsFinite(u)&&u>=0?u:null),compatibility,key);
                    }catch(InvalidDataException){objective.Excluded++;objective.Issues.Add("A saved sample falls outside its original native grid.");}
                }
            }catch(Exception e)when(e is IOException or InvalidDataException or ArgumentException){objective.Unavailable++;objective.Issues.Add(e.Message);Hash(new{Unavailable=p.Id,Reason=e.Message});}
        }
        var results=objectives.Values.Select(o=>new SurveyObjectiveSummary(o.Measurement,o.Groups.Values.Any(g=>g.Count>=rule.MinimumValidSamples&&g.Locations.Count>=rule.MinimumLocations)?ResearchAttemptOutcome.CollectionComplete:o.Valid>0?ResearchAttemptOutcome.PartialCollection:ResearchAttemptOutcome.InsufficientEvidence,
            o.Valid,o.Excluded,o.Unavailable,o.Groups.Values.OrderBy(g=>g.Key).Select(g=>new SurveyGroupSummary(g.Key,g.Unit,g.Datum,g.Count,g.Locations.Count,g.Min,g.Max)).ToArray(),o.Issues.Order().ToArray())).ToArray();
        var summary=new SurveyResearchSummary(1,science.LibraryId,results,sources.Values.OrderBy(s=>s.Id).ToArray(),sourceIds.Count>sources.Count,rows,products,gaps,
            ["Collection completeness is assessed separately for each selected quantity. One compatible source group must meet its sample/location goals; incompatible terrain datums, resolution or source types are not pooled.",
             "Native source pixels are deduplicated across acquisitions. Requested sample locations do not establish surrounding raster or flight-track coverage. No percentage of an entire survey area is claimed by this point-sampling strategy.",
             "DEM/DSM elevations are not airborne LiDAR. Landsat values are historical satellite surface temperatures in Kelvin, not an aircraft thermal camera or simulator air temperature in Celsius. Observation date, flight receipt and processing time remain distinct.",
             "Unknown terrain datums are not credited. Current USGS composite exports can lack a declared datum; supported Copernicus EGM2008 DSM and explicitly supported datum-defined sources remain separate. Uncertainty remains Unknown when ST_QA is absent.",
             "Optional source-age limits use observation dates, never publication dates. Changing altitude, weather, dates and source resolution confound comparison; collecting data does not prove a hypothesis.",
             "Only independently retained science observations and verified published numeric products are used. Deleted external originals are never read or reconstructed. Missing/corrupt retained products invalidate verified plots; collected-science deletion prevents reevaluation."]);
        return new(summary,Convert.ToHexStringLower(hash.GetHashAndReset()),objectives.Values.SelectMany(o=>o.Preview).ToArray(),objectives.Values.Any(o=>o.Stride>1));
    }
    public static (ResearchAttemptOutcome Outcome,string Explanation) Outcome(SurveyResearchSummary s,bool interrupted)
    {
        if(!s.Objectives.Any(o=>o.ValidSamples>0))return(ResearchAttemptOutcome.InsufficientEvidence,"No selected objective has source-qualified retained measurements. A flight path alone is not scientific coverage.");
        if(!interrupted&&s.Objectives.All(o=>o.Outcome==ResearchAttemptOutcome.CollectionComplete))return(ResearchAttemptOutcome.CollectionComplete,"Collection Complete: every selected objective meets its independent sample and location requirements. This is evidence collection, not proof of a hypothesis.");
        return(ResearchAttemptOutcome.PartialCollection,"Partial Collection: one or more selected objectives are incomplete or the attempt was interrupted. Available air temperature cannot substitute for missing terrain or historical surface-temperature evidence.");
    }
}
