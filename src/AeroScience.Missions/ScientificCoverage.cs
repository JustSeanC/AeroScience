using AeroScience.Shared;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Union;
using Geometry=NetTopologySuite.Geometries.Geometry;
using Coordinates=AeroScience.Shared.Coordinates;

namespace AeroScience.Missions;

public sealed record ScientificCoverageRequest(string InstrumentId,string? BandId=null);
public sealed record CoverageSourceResult(Guid SourceId,string Band,string Unit,double ValidSupportSquareMetres,double? SurveyPercent,object ValidSupportGeoJson,long SavedValidSamples,object? ValidGapsGeoJson=null);
public sealed record ScientificCoverageResult(string InstrumentId,double? SurveySquareMetres,double GeometricSquareMetres,double? GeometricSurveyPercent,
    object GeometricGeoJson,object? GapsGeoJson,CoverageSourceResult[] Sources,string[] Methods,long Acquisitions,string Algorithm="scientific-coverage-1",
    double? ValidDataSquareMetres=null,double? ValidDataSurveyPercent=null,object? ValidDataGeoJson=null,object? ValidDataGapsGeoJson=null);

// Regional azimuthal-equidistant coordinates derived with the existing WGS84 GeographicLib solver. Polygon overlays use NTS, not a coverage-inflating display raster.
public sealed class ScientificLocalGeometry(Coordinates origin)
{
    private readonly GeometryFactory factory=new();
    public Coordinate Project(Coordinates p)
    {
        var metres=Geodesy.Distance(origin,p);if(metres>100000)throw new InvalidOperationException("This regional analysis supports geometry within 100 km of its origin. Select a smaller survey region.");
        var azimuth=Geodesy.Bearing(origin,p)*Math.PI/180;return new(metres*Math.Sin(azimuth),metres*Math.Cos(azimuth));
    }
    public Coordinates Unproject(Coordinate p)=>Geodesy.Direct(origin,Math.Atan2(p.X,p.Y)*180/Math.PI,Math.Sqrt(p.X*p.X+p.Y*p.Y));
    public Polygon Polygon(IEnumerable<Coordinates> points)
    {
        var p=points.Select(Project).ToList();if(p.Count<3)throw new ArgumentException("A geographic footprint requires at least three points.");
        if(!p[0].Equals2D(p[^1]))p.Add(p[0].Copy());var polygon=factory.CreatePolygon(p.ToArray());
        if(!polygon.IsValid)throw new InvalidDataException("Invalid footprint/polygon topology; no coverage percentage was inferred.");return polygon;
    }
    public Geometry Empty=>factory.CreatePolygon();
    public object GeoJson(Geometry geometry)
    {
        Coordinate[] Ring(LineString line)=>line.Coordinates.Select(p=>{var g=Unproject(p);return new Coordinate(origin.Longitude+(g.Longitude-origin.Longitude+540)%360-180,g.Latitude);}).ToArray();
        var geographic=new List<Geometry>();
        void Convert(Geometry g)
        {
            if(g is Polygon p&&!p.IsEmpty)
            {
                var polygon=factory.CreatePolygon(factory.CreateLinearRing(Ring(p.ExteriorRing)),Enumerable.Range(0,p.NumInteriorRings).Select(i=>factory.CreateLinearRing(Ring(p.GetInteriorRingN(i)))).ToArray());
                // Inverse projection can separate mathematically shared vertices by
                // a few floating-point bits. Snap the export representation only
                // to 1e-10 degree (~0.012 mm); retain unsnapped local areas above.
                var snapped=NetTopologySuite.Precision.GeometryPrecisionReducer.ReducePointwise(polygon,new PrecisionModel(1e10));
                var repaired=NetTopologySuite.Geometries.Utilities.GeometryFixer.Fix(snapped);
                Geometry Back(Geometry candidate)
                {
                    if(candidate.IsEmpty)return Empty;
                    if(candidate is Polygon part)return Polygon(part.ExteriorRing.Coordinates.Select(c=>new Coordinates(c.Y,(c.X+540)%360-180)))
                        .Difference(Union(Enumerable.Range(0,part.NumInteriorRings).Select(i=>(Geometry)Polygon(part.GetInteriorRingN(i).Coordinates.Select(c=>new Coordinates(c.Y,(c.X+540)%360-180))))));
                    return Union(Enumerable.Range(0,candidate.NumGeometries).Select(i=>Back(candidate.GetGeometryN(i))));
                }
                if(!repaired.IsValid||Math.Abs(Back(repaired).Area-p.Area)>Math.Max(.001,p.Length*.0001))
                    throw new InvalidOperationException("Geographic export exceeded its numerical topology tolerance; select a smaller analysis region.");
                geographic.Add(repaired);
            }
            else for(var i=0;i<g.NumGeometries;i++){var child=g.GetGeometryN(i);if(!ReferenceEquals(child,g))Convert(child);}
        }
        Convert(geometry);var combined=factory.BuildGeometry(geographic);var polygons=new List<double[][][]>();
        double[][] CoordinatesOf(LineString ring)=>ring.Coordinates.Select(p=>new[]{p.X,p.Y}).ToArray();
        void Output(Geometry g)
        {if(g is Polygon p&&!p.IsEmpty)polygons.Add(new[]{CoordinatesOf(p.ExteriorRing)}.Concat(Enumerable.Range(0,p.NumInteriorRings).Select(i=>CoordinatesOf(p.GetInteriorRingN(i)))).ToArray());else for(var i=0;i<g.NumGeometries;i++){var child=g.GetGeometryN(i);if(!ReferenceEquals(child,g))Output(child);}}
        // Split the representation at the antimeridian rather than drawing a regional polygon around the world.
        for(var band=-1;band<=1;band++)
        {
            var clip=factory.ToGeometry(new Envelope(-180+band*360,180+band*360,-90,90));var part=combined.Intersection(clip);
            Output(NetTopologySuite.Geometries.Utilities.AffineTransformation.TranslationInstance(-band*360,0).Transform(part));
        }
        return new{type="MultiPolygon",coordinates=polygons.ToArray()};
    }
    public static Geometry Union(IEnumerable<Geometry> values)
    {var list=values.Where(g=>!g.IsEmpty).ToArray();return list.Length==0?new GeometryFactory().CreatePolygon():UnaryUnionOp.Union(list);}
}
public static class ScientificSupport
{
    public static (string Id,string Unit,double?[] Values,RasterGrid[] Grids) Band(RemoteProductData d,string? id)
    {
        if(id=="temperature-celsius")
        {
            var kelvin=Band(d,"temperature");return(id,"degC",kelvin.Values.Select(v=>v-273.15).ToArray(),kelvin.Grids);
        }
        id??=d.Source.Manifest.Kind switch{RemoteKind.Thermal=>"temperature",RemoteKind.Multispectral=>"ndvi",RemoteKind.Terrain=>"elevation",_=>d.Bands.First(b=>b.WavelengthNm is not null&&b.Values.Any(v=>v.HasValue)).Id};
        if(d.Derived.TryGetValue(id,out var values))
        {
            var grids=id=="ndvi"?d.Bands.Where(b=>b.Id is "B04" or "B08").Select(b=>b.OriginalGrid).ToArray():[d.Bands.First(b=>b.Id is not("QA_PIXEL" or "QA_RADSAT" or "SCL")).OriginalGrid];
            return(id,id=="ndvi"?"1":id=="temperature"?"K":"m",values,grids);
        }
        var band=d.Bands.SingleOrDefault(b=>b.Id==id)??throw new ArgumentException("The requested band is not present in this saved acquisition.");
        if(band.Id is "QA_PIXEL" or "QA_RADSAT" or "SCL")throw new ArgumentException("Select a scientific measurement rather than a quality mask.");
        return(band.Id,band.Unit,band.Values,[band.OriginalGrid]);
    }
    public static (int Column,int Row) Pixel(RasterGrid grid,Coordinates p)
    {
        var point=ScientificRaster.Project(p,grid.Crs);var x=point.X-grid.OriginX;var y=point.Y-grid.OriginY;var determinant=grid.PixelWidth*grid.PixelHeight-grid.RowX*grid.ColumnY;
        var col=(x*grid.PixelHeight-y*grid.RowX)/determinant;var row=(y*grid.PixelWidth-x*grid.ColumnY)/determinant;
        if(!double.IsFinite(col)||!double.IsFinite(row)||col<0||row<0||col>=grid.Width||row>=grid.Height)throw new InvalidDataException("Saved sample lies outside its original pixel grid.");
        return(Math.Min(grid.Width-1,(int)Math.Floor(col+1e-8)),Math.Min(grid.Height-1,(int)Math.Floor(row+1e-8)));
    }
    public static Coordinates[] Cell(RasterGrid grid,int col,int row)=>[ScientificRaster.GridPoint(grid,col,row),ScientificRaster.GridPoint(grid,col+1,row),ScientificRaster.GridPoint(grid,col+1,row+1),ScientificRaster.GridPoint(grid,col,row+1)];
}
public static class ScientificCoverage
{
    public static ScientificCoverageResult Calculate(ScienceStore science,MissionPlan? plan,Guid mission,ScientificCoverageRequest request,CancellationToken token=default,Action<long>? progress=null)
    {
        if(!RemoteAcquisition.Instruments.ContainsKey(request.InstrumentId))throw new ArgumentException("Choose a remote-sensing instrument.");
        var first=science.Products(mission,1,0,request.InstrumentId).FirstOrDefault()??throw new InvalidOperationException("No saved acquisition exists for this instrument; scientific coverage is unavailable.");
        var local=new ScientificLocalGeometry(plan?.SurveyAreas?.FirstOrDefault()?.Vertices[0]??first.Footprint.Center);
        var survey=plan?.SurveyAreas is {Length:>0} areas?ScientificLocalGeometry.Union(areas.Select(a=>local.Polygon(a.Vertices))):null;
        Geometry geometry=local.Empty;var geometricBatch=new List<Geometry>();
        var groups=new Dictionary<(Guid Source,string Band,string Unit),(Geometry Union,List<Geometry> Batch,long Samples)>();
        var fullCells=new HashSet<string>(StringComparer.Ordinal);long acquisitions=0,processed=0;
        foreach(var product in science.AllProducts(mission).Where(p=>p.InstrumentId==request.InstrumentId))
        {
            token.ThrowIfCancellationRequested();acquisitions++;progress?.Invoke(acquisitions);
            Geometry footprint=local.Polygon(product.Footprint.Boundary);if(survey is not null)footprint=footprint.Intersection(survey);
            if(footprint.IsEmpty)continue;geometricBatch.Add(footprint);
            if(geometricBatch.Count>=128){geometry=geometry.Union(ScientificLocalGeometry.Union(geometricBatch));geometricBatch.Clear();}
            var data=science.ReadProduct(product.Id);var band=ScientificSupport.Band(data,request.BandId);var key=(product.SourceId,band.Id,band.Unit);
            if(!groups.TryGetValue(key,out var group))group=(local.Empty,[],0);
            if(data.SampleLocations.Length!=band.Values.Length)throw new InvalidDataException("Saved sample geometry and values have inconsistent lengths.");
            for(var i=0;i<band.Values.Length;i++)
            {
                token.ThrowIfCancellationRequested();if(band.Values[i] is not {} value||!double.IsFinite(value))continue;group.Samples++;
                if(++processed>8000000)throw new InvalidOperationException("This analysis exceeds eight million saved values. Select a shorter mission or smaller survey; no partial coverage result was accepted.");
                Geometry cell=footprint;var ids=new List<string>();
                foreach(var grid in band.Grids)
                {
                    var pixel=ScientificSupport.Pixel(grid,data.SampleLocations[i]);
                    ids.Add($"{product.SourceId:N}:{band.Id}:{grid.Crs}:{grid.OriginX:R}:{grid.OriginY:R}:{pixel.Column}:{pixel.Row}");
                    cell=cell.Intersection(local.Polygon(ScientificSupport.Cell(grid,pixel.Column,pixel.Row)));
                }
                if(cell.IsEmpty)continue;
                var id=string.Join('|',ids);
                if(fullCells.Contains(id))continue;
                // Only skip later duplicates after a complete native support cell was included. Partial footprint intersections must still be unioned.
                var native=local.Polygon(ScientificSupport.Cell(band.Grids[0],ScientificSupport.Pixel(band.Grids[0],data.SampleLocations[i]).Column,ScientificSupport.Pixel(band.Grids[0],data.SampleLocations[i]).Row));
                if(cell.Covers(native))fullCells.Add(id);
                if(fullCells.Count>500000)throw new InvalidOperationException("Coverage exceeds the bounded native-cell index. Select a smaller survey region.");
                group.Batch.Add(cell);
                if(group.Batch.Count>=256){group.Union=group.Union.Union(ScientificLocalGeometry.Union(group.Batch));group.Batch.Clear();}
                if(group.Union.NumPoints>1000000)throw new InvalidOperationException("Coverage topology exceeds the bounded geometry limit; no coarse replacement or inflated percentage was generated.");
            }
            groups[key]=group;
        }
        geometry=geometry.Union(ScientificLocalGeometry.Union(geometricBatch));
        Geometry allValid=local.Empty;
        var sourceResults=groups.OrderBy(g=>g.Key.Source).ThenBy(g=>g.Key.Band).Select(g=>
        {var union=g.Value.Union.Union(ScientificLocalGeometry.Union(g.Value.Batch));allValid=allValid.Union(union);return new CoverageSourceResult(g.Key.Source,g.Key.Band,g.Key.Unit,union.Area,survey?.Area is >0?100*union.Area/survey.Area:null,local.GeoJson(union),g.Value.Samples,survey is null?null:local.GeoJson(survey.Difference(union)));}).ToArray();
        return new(request.InstrumentId,survey?.Area,geometry.Area,survey?.Area is >0?100*geometry.Area/survey.Area:null,local.GeoJson(geometry),survey is null?null:local.GeoJson(survey.Difference(geometry)),sourceResults,
            ["scientific-coverage-1: vector union/intersection, no coarse coverage raster; duplicate observations never add overlapping area twice.",
             "Denominator is the union of saved survey polygons. Without a survey polygon percentages are unavailable. Geometric gaps describe modeled footprints; they do not establish missing valid-data support.",
             "Valid support is the union of saved finite, QA-filtered measurement source pixels intersected with their actual modeled acquisition footprints and survey. NDVI support intersects both required red/NIR native pixels. Unvisited pixels and missing bands do not contribute.",
             "Per-source results retain distinct scenes/dates/units/datums. Overall valid-data coverage is only the geographic union of actual saved support across those sources; it does not establish a single-date, uniform-calibration image or combine numerical measurements.",
             "Regional WGS84 azimuthal-equidistant plane, ≤100 km radius. Area distortion is approximately ≤0.005% at this radius; source pixel edges use corner chords. Curved-edge/terrain-footprint uncertainty is additional, so areas are approximate, not cadastral or validated complete sensor coverage.",
             "GeoJSON representation snaps inverse-projection coordinates to 1e−10 degree (~0.012 mm) to resolve floating-point shared-edge topology, with an area-change guard of perimeter×0.1 mm or 0.001 m². Local union area calculations remain unsnapped; antimeridian geometry is split.",
             "Aircraft local weather does not override historical source QA. Native retained product-grid resolution may be finer than the original physical sensor resolution (e.g. Landsat TIRS); no additional independent resolving power is claimed. Source dates and footprint limitations remain authoritative. Flight-plan adherence is calculated separately."],acquisitions,
             ValidDataSquareMetres:allValid.Area,ValidDataSurveyPercent:survey?.Area is >0?100*allValid.Area/survey.Area:null,ValidDataGeoJson:local.GeoJson(allValid),ValidDataGapsGeoJson:survey is null?null:local.GeoJson(survey.Difference(allValid)));
    }
}
