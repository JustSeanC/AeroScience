using AeroScience.Shared;
namespace AeroScience.Missions;

public sealed record ScientificOverviewBin(string InstrumentId,string? Measurement,string? Unit,long Records,long ValidRecords,Coordinates Center,GeographicBounds Bounds,string Representation);
public sealed record ScientificOverviewCategory(string InstrumentId,string? Measurement,string? Unit,long Acquisitions,long AtmosphericValues,long ValidAtmosphericValues,long SavedValidSamplePositions,long KnownMissingSamplePositions,long UnknownSampleCounts,DateTimeOffset? FirstFlightUtc,DateTimeOffset? LastFlightUtc,DateTimeOffset? FirstSourceUtc,DateTimeOffset? LastSourceUtc,long UnknownSourceDates);
public sealed record ScientificMapOverview(ScientificOverviewCategory[] Categories,ScientificOverviewBin[] Bins,GeographicBounds? Extent,long Acquisitions,long AtmosphericValues,DateTimeOffset CapturedUtc,string Method);

public sealed partial class ScienceStore
{
    /// <summary>Bounded-memory overview of the complete indexed query scope. It does
    /// not read original raster values, fetch sources or interpolate a science image.</summary>
    public ScientificMapOverview MapOverview(ScienceMapQuery query,CancellationToken token=default)
    {
        if(query.Area is {} area&&(area.West < -180||area.East>180||area.South < -90||area.North>90||area.South>=area.North||new[]{area.West,area.East,area.South,area.North}.Any(v=>!double.IsFinite(v))))throw new ArgumentException("Invalid geographic overview bounds.");
        var captured=DateTimeOffset.UtcNow;
        var scope=query.Area??new GeographicBounds(-180,-90,180,90);
        var columns=12;var rows=8;
        var width=scope.East>=scope.West?scope.East-scope.West:360-scope.West+scope.East;
        var stepX=Math.Max(1e-8,width/columns);var stepY=Math.Max(1e-8,(scope.North-scope.South)/rows);
        var bins=new Dictionary<(string Instrument,string? Measurement,string? Unit,int X,int Y),OverviewAccumulator>();
        var categories=new Dictionary<(string Instrument,string? Measurement,string? Unit),OverviewAccumulator>();
        GeographicBounds? extent=null;
        void Add(string instrument,string? measurement,string? unit,Coordinates center,Coordinates[] boundary,bool valid,DateTimeOffset flight,DateTimeOffset? observed,bool remote,long? samples=null,long missing=0)
        {
            var left=boundary.Min(p=>p.Longitude);var right=boundary.Max(p=>p.Longitude);var bottom=boundary.Min(p=>p.Latitude);var top=boundary.Max(p=>p.Latitude);
            var bounds=new GeographicBounds(left,bottom,right,top);
            extent=extent is null?bounds:new(Math.Min(extent.West,left),Math.Min(extent.South,bottom),Math.Max(extent.East,right),Math.Max(extent.North,top));
            var longitude=center.Longitude;if(scope.West>scope.East&&longitude<scope.West)longitude+=360;
            var x=Math.Clamp((int)Math.Floor((longitude-scope.West)/stepX),0,columns-1);var y=Math.Clamp((int)Math.Floor((center.Latitude-scope.South)/stepY),0,rows-1);
            var key=(instrument,measurement,unit,x,y);if(!bins.TryGetValue(key,out var bin)){bin=new();bins.Add(key,bin);}bin.Add(center with{Longitude=longitude},bounds,valid,flight,observed,remote,samples,missing);
            var categoryKey=(instrument,measurement,unit);if(!categories.TryGetValue(categoryKey,out var category)){category=new();categories.Add(categoryKey,category);}category.Add(center,bounds,valid,flight,observed,remote,samples,missing);
        }
        var pageQuery=query with{After=0,Limit=100,Offset=0};
        while(true)
        {
            token.ThrowIfCancellationRequested();var page=MapPage(pageQuery);
            foreach(var product in page.Products)
            {
                token.ThrowIfCancellationRequested();
                var boundary=product.Footprint.Boundary;if(boundary.Length==0)continue;
                Add(product.InstrumentId,null,null,product.Footprint.Center,boundary,product.ValidSampleCount is >0,product.FlightPacketUtc,product.SourceObservationUtc,true,product.ValidSampleCount,product.ValidSampleCount is {} valid?Math.Max(0,(long)product.Width*product.Height-valid):0);
            }
            if(!page.HasMore)break;pageQuery=pageQuery with{After=page.Next};
        }
        pageQuery=query with{After=0,Limit=500,Offset=0};
        while(true)
        {
            token.ThrowIfCancellationRequested();var page=AtmosphereMap(pageQuery);
            foreach(var observation in page.Items)
            {
                token.ThrowIfCancellationRequested();if(observation.Latitude is not {} lat||observation.Longitude is not {} lon)continue;
                var point=new Coordinates(lat,lon);Add(observation.InstrumentId,observation.MeasurementId,observation.Unit,point,[point],observation.Quality==ObservationQuality.Valid,observation.TimestampUtc,null,false);
            }
            if(!page.HasMore)break;pageQuery=pageQuery with{After=page.Next};
        }
        var totals=categories.Select(pair=>new ScientificOverviewCategory(pair.Key.Instrument,pair.Key.Measurement,pair.Key.Unit,pair.Value.Acquisitions,pair.Value.Atmosphere,pair.Key.Measurement is null?0:pair.Value.Valid,pair.Value.Samples,pair.Value.Missing,pair.Value.UnknownSamples,pair.Value.FirstFlight,pair.Value.LastFlight,pair.Value.FirstSource,pair.Value.LastSource,pair.Value.UnknownDates)).ToArray();
        return new(totals,bins.Select(pair=>new ScientificOverviewBin(pair.Key.Instrument,pair.Key.Measurement,pair.Key.Unit,pair.Value.Count,pair.Value.Valid,new(pair.Value.Latitude/pair.Value.Count,((pair.Value.Longitude/pair.Value.Count+180)%360+360)%360-180),pair.Value.Bounds!,pair.Key.Measurement is null?"Modeled footprint locations of saved historical-source acquisitions; not valid-pixel coverage":"Aggregated recorded simulator observation locations; not a measured value at the centroid")).ToArray(),extent,totals.Sum(c=>c.Acquisitions),totals.Sum(c=>c.AtmosphericValues),captured,"Complete selected indexed scope, automatically traversed with bounded keyset pages. Each category uses at most 12×8 geographic bins; no acquisition viewing ceiling. Bins are an overview, not scientific interpolation. Counts distinguish acquisitions, atmospheric values and saved sample positions. Original values, quality and source dates remain in detailed records and exports. Live recording can add records after this query.");
    }
    private sealed class OverviewAccumulator
    {
        public long Count,Valid,Acquisitions,Atmosphere,Samples,Missing,UnknownSamples,UnknownDates;
        public double Latitude,Longitude;public GeographicBounds? Bounds;
        public DateTimeOffset? FirstFlight,LastFlight,FirstSource,LastSource;
        public void Add(Coordinates point,GeographicBounds bounds,bool valid,DateTimeOffset flight,DateTimeOffset? observed,bool remote,long? samples,long missing)
        {
            Count++;if(valid)Valid++;Latitude+=point.Latitude;Longitude+=point.Longitude;
            Bounds=Bounds is null?bounds:new(Math.Min(Bounds.West,bounds.West),Math.Min(Bounds.South,bounds.South),Math.Max(Bounds.East,bounds.East),Math.Max(Bounds.North,bounds.North));
            FirstFlight=FirstFlight is null||flight<FirstFlight?flight:FirstFlight;LastFlight=LastFlight is null||flight>LastFlight?flight:LastFlight;
            if(remote){Acquisitions++;if(samples is {} count)Samples+=count;else UnknownSamples++;Missing+=missing;if(observed is {} date){FirstSource=FirstSource is null||date<FirstSource?date:FirstSource;LastSource=LastSource is null||date>LastSource?date:LastSource;}else UnknownDates++;}
            else Atmosphere++;
        }
    }
}
