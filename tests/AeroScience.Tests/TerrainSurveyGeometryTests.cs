using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using NetTopologySuite.Geometries;
using Xunit;
using Coordinates = AeroScience.Shared.Coordinates;

namespace AeroScience.Tests;

// Controlled pose fixtures exercise the model, not genuine sensor/provider acceptance.
public sealed class TerrainSurveyGeometryTests
{
    static readonly DateTimeOffset Utc = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    static readonly Guid Session = Guid.Parse("00000000-0000-0000-0000-000000000001");
    static readonly Guid Config = Guid.Parse("00000000-0000-0000-0000-000000000002");
    static readonly Coordinates Origin = new(40, -75);
    internal static TelemetryFrame Frame(int second, double north = 0, double east = 0, double agl = 1000,
        double heading = 0, double bank = 0, Coordinates? origin = null)
    {
        var p = Geodesy.Direct(Geodesy.Direct(origin ?? Origin, 0, north), 90, east);
        return new(Utc.AddSeconds(second), TelemetrySource.Simulated, "SYNTHETIC corridor fixture", p.Latitude, p.Longitude,
            5000, agl, 200, 200, 400, heading, heading, 0, bank, 0);
    }
    static TerrainSurveyInput Input(TelemetryFrame f, PayloadConfiguration? payload = null, Guid? session = null, Guid? config = null) => new(f,
        new("pose-"+f.TimestampUtc.Ticks, new string('a',64), f.TimestampUtc, f.TimestampUtc.Ticks, EvidenceOrigin.SyntheticFixture), session ?? Session,
        new(config ?? Config, 1, Utc, new("terrain-scanner",true,1000,payload ?? new(InstrumentProfiles.Generic("terrain-scanner")))));
    static TerrainSurveyRecord[] Build(IEnumerable<TerrainSurveyInput> inputs, TerrainSurveyOptions? options = null)
    {
        var o = options ?? new(); TerrainSurveyGeometryBuilder.Validate(o);
        return new TerrainSurveyGeometryBuilder(o,default).Build(inputs).ToArray();
    }
    static TerrainSurveyChunk[] Chunks(IEnumerable<TerrainSurveyRecord> records) => records.Where(r=>r.Chunk is not null).Select(r=>r.Chunk!).ToArray();
    static NetTopologySuite.Geometries.Geometry Geographic(TerrainSurveyGeometry g)
    {
        var f = new GeometryFactory();
        return f.CreateMultiPolygon(g.Coordinates.Select(p=>f.CreatePolygon(f.CreateLinearRing(p[0].Select(c=>new Coordinate(c[0],c[1])).ToArray()),p.Skip(1).Select(r=>f.CreateLinearRing(r.Select(c=>new Coordinate(c[0],c[1])).ToArray())).ToArray())).ToArray());
    }
    static void Valid(TerrainSurveyChunk c)
    {
        Assert.True(Geographic(c.Geometry).IsValid); Assert.True(c.LocalUnionSquareMetres>0); Assert.InRange(c.Vertices,4,20000);
        foreach(var p in c.Geometry.Coordinates) for(var r=0;r<p.Length;r++)
        {
            var ring=p[r]; Assert.Equal(ring[0],ring[^1]); double area=0;
            for(var i=0;i<ring.Length-1;i++){area+=(ring[i][0]-ring[0][0])*(ring[i+1][1]-ring[0][1])-(ring[i+1][0]-ring[0][0])*(ring[i][1]-ring[0][1]);Assert.InRange(Math.Abs(ring[i+1][0]-ring[i][0]),0,180);}
            Assert.Equal(r==0,area>0);
        }
        foreach(var w in c.SourceSearchWindows){Assert.InRange(w.West,-180,180);Assert.InRange(w.East,-180,180);Assert.True(w.West<=w.East);Assert.InRange(w.East-w.West,0,5);}
    }
    [Fact] public void StraightLevelSweepMatchesOriginalFootprintAndNativeFeetConversion()
    {
        var chunk=Assert.Single(Chunks(Build([Input(Frame(0)),Input(Frame(5,1000))]))); Valid(chunk);
        var width=2*1000*.3048*Math.Tan(15*Math.PI/180); var length=2*1000*.3048*Math.Tan(1.5*Math.PI/180);
        var expected=width*(1000+length);Assert.InRange(chunk.LocalUnionSquareMetres,expected*.998,expected*1.002);
        Assert.Equal(20,Assert.Single(chunk.Legs).InterpolatedSteps);Assert.InRange(chunk.Legs[0].GeodesicMetres,999.999,1000.001);
        Assert.Equal("generic.terrain",chunk.GeometryProfile.Profile.Id);Assert.False(chunk.UsesLegacyDefaultProfile);
    }
    [Theory][InlineData(.00000001,20)][InlineData(.000001,21)]
    public void SubdivisionBoundaryRejectsFloatingRoundoffButRetainsRealFidelityExcess(double excessMetres,int expectedSteps)
    {
        var chunk=Assert.Single(Chunks(Build([Input(Frame(0)),Input(Frame(5,1000+excessMetres))])));Valid(chunk);
        Assert.Equal(expectedSteps,Assert.Single(chunk.Legs).InterpolatedSteps);
        Assert.InRange(chunk.Legs[0].GeodesicMetres,1000+excessMetres-.000000005,1000+excessMetres+.000000005);
    }
    [Fact] public void HeadingOrientsInstrumentRatherThanSubstitutingGpsTrack()
    {
        var a=Frame(0);var b=Frame(2,100);
        var original=Assert.Single(Chunks(Build([Input(a),Input(b)])));
        var altered=Assert.Single(Chunks(Build([Input(a with{GroundTrackDegrees=90}),Input(b with{GroundTrackDegrees=90})])));
        Assert.Equal(original.GeometrySha256,altered.GeometrySha256);
        var sideways=Assert.Single(Chunks(Build([Input(a with{HeadingDegrees=90}),Input(b with{HeadingDegrees=90})])));
        Assert.True(sideways.LocalUnionSquareMetres<original.LocalUnionSquareMetres*.5);
    }
    [Fact] public void IncreasingAndDecreasingAglUseChangingAuthoritativeFootprints()
    {
        double Area(double[] heights)=>Chunks(Build(heights.Select((h,i)=>Input(Frame(i*2,i*150,agl:h))))).Sum(c=>c.LocalUnionSquareMetres);
        var low=Area([500,500,500]);var varying=Area([500,1000,500]);var high=Area([1000,1000,1000]);
        Assert.True(varying>low*1.3);Assert.True(varying<high);Assert.Equal(varying,Area([500,1000,500]),6);
    }
    [Theory][InlineData(15)][InlineData(90)]
    public void TurnsUseValidBoundedLoftsAndDoNotFillWholeTurnHull(double turn)
    {
        var inputs=new[]{Input(Frame(0)),Input(Frame(2,300)),Input(Frame(4,300,300,heading:turn))};
        var c=Assert.Single(Chunks(Build(inputs)));Valid(c);Assert.Equal(2,c.Legs.Length);
        var offPath=Geodesy.Direct(Geodesy.Direct(Origin,0,50),90,250);
        Assert.False(Geographic(c.Geometry).Covers(new Point(offPath.Longitude,offPath.Latitude)));
    }
    [Fact] public void BankingUsesSensorRayIntersectionAndHorizonAttitudesRemainGaps()
    {
        var level=Assert.Single(Chunks(Build([Input(Frame(0)),Input(Frame(2,200))])));
        var banked=Assert.Single(Chunks(Build([Input(Frame(0,bank:35)),Input(Frame(2,200,bank:35))])));Valid(banked);
        Assert.True(banked.LocalUnionSquareMetres>level.LocalUnionSquareMetres*1.25);
        var invalid=Build([Input(Frame(0)),Input(Frame(1,100,bank:89)),Input(Frame(2,200)),Input(Frame(3,300))]);
        Assert.Single(Chunks(invalid));Assert.Contains(invalid,r=>r.Gap?.Reason.Contains("ground intersection")==true);
        Assert.All(Chunks(invalid).SelectMany(c=>c.Legs),l=>Assert.True(l.From.ObservedUtc>=Utc.AddSeconds(2)));
    }
    [Fact] public void RieglReferencePreservesVersionedGeometryAndDoesNotGenerateReturns()
    {
        var generic=Assert.Single(Chunks(Build([Input(Frame(0)),Input(Frame(2,200))])));
        var payload=new PayloadConfiguration(InstrumentProfiles.Catalog.Single(p=>p.Id=="riegl.vq-1560-ii-s.nominal"));
        var riegl=Assert.Single(Chunks(Build([Input(Frame(0),payload),Input(Frame(2,200),payload)])));Valid(riegl);
        Assert.True(riegl.LocalUnionSquareMetres>generic.LocalUnionSquareMetres*2);Assert.Equal(1,riegl.GeometryProfile.Profile.Version);
        Assert.Equal(payload.Profile.MeasurementRateHz,riegl.GeometryProfile.Profile.MeasurementRateHz);
        Assert.Contains(riegl.GeometryProfile.Profile.Limitations,s=>s.Contains("return",StringComparison.OrdinalIgnoreCase));
    }
    [Fact] public void FastMotionChangesSupportNotAcquisitionCountsAndShortRecordingIsTruthful()
    {
        var slow=Assert.Single(Chunks(Build([Input(Frame(0)),Input(Frame(5,100))])));
        var fast=Assert.Single(Chunks(Build([Input(Frame(0)),Input(Frame(5,1000))])));
        Assert.True(fast.LocalUnionSquareMetres>slow.LocalUnionSquareMetres*8);
        var single=Build([Input(Frame(0))]);Assert.Empty(Chunks(single));Assert.Contains(single,r=>r.Gap?.Reason.Contains("No adjacent")==true);
    }
    [Fact] public void SessionConfigurationAndExplicitPauseGapsNeverBridgeSeparatedSegments()
    {
        var session2=Guid.NewGuid();var config2=Guid.NewGuid();
        var rows=Build([Input(Frame(0)),Input(Frame(1,100)),new(Gap:new("Paused",Utc.AddSeconds(1),Utc.AddSeconds(5),Session)),Input(Frame(5,500)),Input(Frame(6,600)),
            Input(Frame(7,700),session:session2),Input(Frame(8,800),session:session2),Input(Frame(9,900),session:session2,config:config2),Input(Frame(10,1000),session:session2,config:config2)]);
        var chunks=Chunks(rows);Assert.Equal(4,chunks.Length);Assert.All(chunks,Valid);
        Assert.Equal(4,chunks.Sum(c=>c.Legs.Length));Assert.Contains(rows,r=>r.Gap?.Reason=="Scientific session changed");Assert.Contains(rows,r=>r.Gap?.Reason=="Frozen instrument configuration changed");
    }
    [Theory][InlineData("agl")][InlineData("time")][InlineData("jump")][InlineData("duplicate")][InlineData("latitude")][InlineData("paused")]
    public void InvalidAndDiscontinuousInputsHaveExplicitExclusions(string kind)
    {
        var bad=Frame(2,200);bad=kind switch{"agl"=>bad with{AltitudeAglFeet=null},"time"=>bad with{TimestampUtc=Utc.AddSeconds(20)},"jump"=>bad with{Longitude=-70},"duplicate"=>bad with{TimestampUtc=Utc.AddSeconds(1)},"latitude"=>bad with{Latitude=86},"paused"=>bad with{IsPaused=true},_=>bad};
        var records=Build([Input(Frame(0)),Input(Frame(1,100)),Input(bad),Input(Frame(3,300)),Input(Frame(4,400))]);
        Assert.Contains(records,r=>r.Gap is not null);Assert.All(Chunks(records),Valid);
        Assert.DoesNotContain(Chunks(records).SelectMany(c=>c.Legs),l=>l.From.ObservedUtc==Utc.AddSeconds(1)&&l.To.ObservedUtc==Utc.AddSeconds(2));
    }
    [Fact] public void AntimeridianSplitAndHighLatitudePolicyProduceUsableSourceWindows()
    {
        var o=new Coordinates(40,179.999);var c=Assert.Single(Chunks(Build([Input(Frame(0,heading:90,origin:o)),Input(Frame(2,east:400,heading:90,origin:o))])));Valid(c);
        Assert.True(c.Geometry.Coordinates.Length>=2);Assert.Contains(c.SourceSearchWindows,w=>w.West< -179);Assert.Contains(c.SourceSearchWindows,w=>w.East>179);
        var high=new Coordinates(84,20);Valid(Assert.Single(Chunks(Build([Input(Frame(0,origin:high)),Input(Frame(2,100,origin:high))]))));
        Assert.Empty(Chunks(Build([Input(Frame(0,origin:new(86,20))),Input(Frame(2,100,origin:new(86,20)))])));
    }
    [Fact] public void RepeatedPassesUnionRatherThanInflatingLocalArea()
    {
        var one=Assert.Single(Chunks(Build([Input(Frame(0)),Input(Frame(2,200))])));
        var twice=Assert.Single(Chunks(Build([Input(Frame(0)),Input(Frame(2,200)),Input(Frame(4)),Input(Frame(6,200))])));Valid(twice);
        Assert.InRange(twice.LocalUnionSquareMetres,one.LocalUnionSquareMetres*.99999,one.LocalUnionSquareMetres*1.00001);
    }
    [Fact] public void LowAglExportRetainsValidWindingWithoutGlobalCoordinateCancellation()
    {
        var c=Assert.Single(Chunks(Build([Input(Frame(0,agl:1)),Input(Frame(1,1,agl:1))])));Valid(c);
        Assert.InRange(c.LocalUnionSquareMetres,.16,.18);
    }
    [Fact] public void HourLongRegionalProcessingYieldsBeforeReadingTheWholeMission()
    {
        var produced=0;IEnumerable<TerrainSurveyInput> Source(){for(var i=0;i<=3600;i++){produced++;yield return Input(Frame(i,i*50));}}
        var options=new TerrainSurveyOptions(MaximumLegsPerChunk:16,ChunkRadiusMetres:2000);
        var builder=new TerrainSurveyGeometryBuilder(options,default);var chunks=0;long legs=0;
        foreach(var r in builder.Build(Source()))if(r.Chunk is {} c)
        {
            if(chunks++==0)Assert.InRange(produced,2,18);
            Assert.InRange(c.Legs.Length,1,16);Assert.InRange(c.Vertices,4,options.MaximumVerticesPerChunk);legs+=c.Legs.Length;
        }
        var summary=builder.Summary(0);Assert.Equal(3600,legs);Assert.True(chunks>=225);Assert.Equal(3601,summary.InputRecords);
        Assert.InRange(summary.PeakChunkLegs,1,16);Assert.InRange(summary.ModeledPoses,7200,10800);
    }
    [Fact] public void LongMultiLegFlightStreamsBoundedRegionalChunksWithStableGeometry()
    {
        var input=Enumerable.Range(0,385).Select(i=>Input(Frame(i, i*50))).ToArray();
        var a=Chunks(Build(input,new(MaximumLegsPerChunk:16,ChunkRadiusMetres:2000)));
        var b=Chunks(Build(input,new(MaximumLegsPerChunk:16,ChunkRadiusMetres:2000)));
        Assert.Equal(384,a.Sum(c=>c.Legs.Length));Assert.True(a.Length>=24);Assert.All(a,c=>{Valid(c);Assert.InRange(c.Legs.Length,1,16);});
        Assert.Equal(a.Select(c=>c.GeometrySha256),b.Select(c=>c.GeometrySha256));
        Assert.All(a.SelectMany(c=>c.Legs),l=>Assert.InRange(l.InterpolatedSteps,1,2));
    }
    [Fact] public void FidelityPoseVertexChunkAndInputBudgetsFailExplicitly()
    {
        var inputs=new[]{Input(Frame(0)),Input(Frame(5,1000))};
        Assert.Contains(Build(inputs,new(MaximumStepsPerLeg:2)),r=>r.Gap?.Reason.Contains("fidelity")==true);
        Assert.Throws<TerrainSurveyBudgetException>(()=>Build(inputs,new(MaximumModeledPoses:2)));
        Assert.Throws<TerrainSurveyBudgetException>(()=>Build(inputs,new(MaximumVerticesPerChunk:16)));
        Assert.Throws<TerrainSurveyBudgetException>(()=>Build(inputs,new(MaximumArchiveRows:1)));
        Assert.Throws<TerrainSurveyBudgetException>(()=>Build([Input(Frame(0)),Input(Frame(1,100)),Input(Frame(2,200))],new(MaximumChunks:1,MaximumLegsPerChunk:1)));
        Assert.Throws<ArgumentException>(()=>TerrainSurveyGeometryBuilder.Validate(new(MaximumGapSeconds:double.NaN)));
        Assert.Throws<ArgumentException>(()=>TerrainSurveyGeometryBuilder.Validate(new(MaximumOutputBytes:long.MaxValue)));
    }
}
