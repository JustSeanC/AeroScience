using AeroScience.Shared;
namespace AeroScience.Missions;

// Sampling indicators only: no footprint, swath, interpolation, area or coverage percentage.
public static class CampaignCoverageRules
{
    public const int Version=1,MaximumBands=64,MaximumCells=256;
    public static readonly string[] Methods=[
        "Only currently approved complete-card P19 evidence contributes. Received, pending, rejected, changed or unverifiable evidence does not.",
        "Point observations remain points. Grid cells indicate qualifying samples, never complete-area sensor coverage. No area percentage, swath or interpolated measurements are inferred.",
        "Original identity and content hashes deduplicate repeated uploads. Conflicting identities fail closed. Separate flights/dates/pilots are counted independently; repeated uploads are not repeat measurements.",
        "Compatibility keeps exact published requirement revisions, variable meaning, original units/MSL reference, instrument, provenance/category and source semantics separate. Different groups are not added together to satisfy a goal.",
        "Human approval and client measurement outcomes remain distinct. Synthetic fixtures, pilot documentation, simulator readings, modeled outputs and historical datasets are never interchangeable.",
        "Altitude bands are irregular observed sampling, not continuous profiles. Bands/cells use lower-inclusive/upper-exclusive boundaries, with the outermost target upper boundary included.",
        "No target defined means observed evidence only, not completion. Contributor pseudonyms retain P19 policy; distinct authorized accounts are not physical independence or hardware attestation."
    ];
    public static CoverageVariable[] Variables()=>AtmosphericResearch.Quantities.Select(q=>new CoverageVariable("science:"+q.Instrument+":"+q.Measurement,q.Measurement.Replace('-',' '),q.Instrument,q.Unit,"Original supported simulator reading",true)).Concat([
        new("quantitative:ias","Indicated airspeed","recorded-telemetry","knots IAS","Approved quantitative IAS samples; not geographic science coverage",true),
        new("quantitative:altitude-msl","MSL altitude","recorded-telemetry","feet MSL","Approved original MSL observations; no interpolation",true),
        new("pilot-observation:observation","Pilot observations","pilot-documentation","documented entries","Written documentation counts; not numerical sensor measurements",true)
    ]).ToArray();
    public static void Validate(CoverageGoalDefinition d)
    {
        static bool Text(string? s,int n,bool required=true)=>s is not null&&s.Length<=n&&(!required||!string.IsNullOrWhiteSpace(s))&&!s.Any(c=>char.IsControl(c)&&c is not('\n' or '\r' or '\t'));
        if(d is null||d.Version!=Version||!Text(d.Name,120)||!Text(d.Question,2000)||d.OpportunityId==Guid.Empty||d.PublicationRevision<1||!Hash(d.PublicationSha256)||!Text(d.Variable,120)||!Text(d.Instrument,80)||!Text(d.Unit,40)||!Enum.IsDefined(d.Category)||d.Category==CoverageEvidenceCategory.Unknown||d.RequirementKey is not null&&!Text(d.RequirementKey,120)||d.SourceSemanticsSha256 is not null&&!Hash(d.SourceSemanticsSha256))throw new ArgumentException("Name the coverage goal, choose the exact published card, and select a supported variable, units and source category.");
        var v=Variables().FirstOrDefault(v=>v.Id==d.Variable);if(v is null&&!(d.Variable.StartsWith("pilot-observation:",StringComparison.Ordinal)&&d.Variable.Length is >18 and <=80&&d.Variable[18..].All(c=>char.IsAsciiLetterOrDigit(c)||c=='-')&&d.Instrument=="pilot-documentation"&&d.Unit=="documented entries")||v is not null&&(v.Instrument!=d.Instrument||v.Unit!=d.Unit))throw new ArgumentException("Use a supported original variable with its original instrument and units. Unsupported raster/model products cannot establish coverage.");
        if(d.Instrument=="pilot-documentation"&&d.Category!=CoverageEvidenceCategory.PilotObservation||d.Instrument!="pilot-documentation"&&d.Category==CoverageEvidenceCategory.PilotObservation)throw new ArgumentException("Keep Pilot Observation documentation separate from measured sensor variables.");
        if(d.MinimumObservations is <1 or >1000000||d.MinimumFlights is <1 or >10000||d.MinimumDates is <1 or >10000||d.MinimumPilots is <1 or >10000)throw new ArgumentException("Choose positive bounded observation, flight, UTC-date and independent-pilot requirements.");
        if(d.FromUtc?.Offset!=TimeSpan.Zero&&d.FromUtc is not null||d.ThroughUtc?.Offset!=TimeSpan.Zero&&d.ThroughUtc is not null||d.FromUtc is {} from&&d.ThroughUtc is {} to&&from>to)throw new ArgumentException("Use an ordered UTC observation window; source observation dates are not publication dates.");
        if(d.Altitude is {} a&&(!double.IsFinite(a.MinimumFeetMsl)||!double.IsFinite(a.MaximumFeetMsl)||!double.IsFinite(a.BandFeet)||a.MinimumFeetMsl< -2000||a.MaximumFeetMsl>100000||a.MaximumFeetMsl<=a.MinimumFeetMsl||a.BandFeet<10||a.BandFeet>20000||BandCount(a)>MaximumBands))throw new ArgumentException("Choose an MSL altitude range with 1–64 bands, each at least 10 feet wide. AGL/flight levels cannot be substituted.");
        if(d.Grid is {} g&&(!new[]{g.South,g.West,g.North,g.East}.All(double.IsFinite)||g.South< -80||g.North>80||g.North<=g.South||g.West< -180||g.East>180||g.East<=g.West||g.Rows is <1 or >16||g.Columns is <1 or >16||g.Rows*g.Columns>MaximumCells))throw new ArgumentException("Choose a non-wrapping target rectangle between 80°S and 80°N with up to 16 rows/columns and 256 sampling cells. Dateline/polar targets are unsupported.");
        if((d.Grid?.Rows??1)*(d.Grid?.Columns??1)*(d.Altitude is {} alt?BandCount(alt):1)>1024)throw new ArgumentException("Use at most 1,024 band/cell combinations per coverage goal.");
    }
    public static bool Hash(string value)=>value is {Length:64}&&value.All(char.IsAsciiHexDigit);
    public static int BandCount(CoverageAltitudeTarget a)=>(int)Math.Ceiling((a.MaximumFeetMsl-a.MinimumFeetMsl)/a.BandFeet);
    public static int? Band(CoverageAltitudeTarget a,double? altitude)
    {if(altitude is not {} v||!double.IsFinite(v)||v<a.MinimumFeetMsl||v>a.MaximumFeetMsl)return null;return Math.Min(BandCount(a)-1,(int)Math.Floor((v-a.MinimumFeetMsl)/a.BandFeet));}
    public static (int Row,int Column)? Cell(CoverageSamplingGrid g,double? latitude,double? longitude)
    {if(latitude is not {} lat||longitude is not {} lon||!double.IsFinite(lat)||!double.IsFinite(lon)||lat<g.South||lat>g.North||lon<g.West||lon>g.East)return null;return(Math.Min(g.Rows-1,(int)Math.Floor((lat-g.South)/(g.North-g.South)*g.Rows)),Math.Min(g.Columns-1,(int)Math.Floor((lon-g.West)/(g.East-g.West)*g.Columns)));}
    public static bool Satisfied(CoverageCounts c,CoverageGoalDefinition d)=>c.Observations>=d.MinimumObservations&&c.Flights>=d.MinimumFlights&&c.Dates>=d.MinimumDates&&c.Pilots>=d.MinimumPilots;
}
