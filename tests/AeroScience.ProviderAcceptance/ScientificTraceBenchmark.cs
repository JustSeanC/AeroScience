using System.Diagnostics;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
public static class ScientificTraceBenchmark
{
    public static void Run(string library,string output)
    {
        using var science=new ScienceStore(library);var start=new Coordinates(40.005,-74.995);var results=new List<object>();
        var prefs=new ScientificAccessPreferences(false,false,PreferredDateUtc:DateTimeOffset.Parse("2024-09-10T00:00:00Z"),LookbackDays:120);
        foreach(var scenario in new[]{"stationary","slow","moderate-straight","fast-unplanned","turn","route-diversion","lawnmower","repeated-passes","orbit","racetrack","repeat-flight"})
        {
            var watch=Stopwatch.StartNew();var predictor=new ScientificMotionPredictor();var prepared=0;var successes=0;var unavailable=0;var reused=0;var samples=0;double? firstReady=null;
            var now=DateTimeOffset.UtcNow;Coordinates previous=start;var speed=scenario=="stationary"?0:scenario=="slow"?15:scenario=="fast-unplanned"?450:120;
            for(var i=0;i<24;i++)
            {
                var p=scenario switch
                {
                    "stationary"=>start,
                    "orbit"=>Geodesy.Direct(start,i*15,400),
                    "racetrack"=>Geodesy.Direct(start,i<12?90:270,Math.Abs(i%12-6)*70),
                    "lawnmower" or "repeated-passes"=>Geodesy.Direct(Geodesy.Direct(start,0,(i/6)*(scenario=="repeated-passes"?0:80)),90,(i/6)%2==0?(i%6)*80:(5-i%6)*80),
                    "turn" or "route-diversion"=>i<12?Geodesy.Direct(start,90,i*80):Geodesy.Direct(Geodesy.Direct(start,90,11*80),0,(i-11)*80),
                    "repeat-flight"=>Geodesy.Direct(start,90,(i%12)*80),
                    _=>Geodesy.Direct(start,90,i*speed*.514444444*2)
                };
                var track=i==0?90:Geodesy.Bearing(previous,p);var frame=new TelemetryFrame(now.AddSeconds(i*2),TelemetrySource.Simulated,"Synthetic benchmark trajectory over genuine retained data",p.Latitude,p.Longitude,1000,100,speed,speed,speed,track,track,0,0,0);
                var plan=scenario=="route-diversion"?new MissionPlan(Guid.NewGuid(),"Optional route reference",Waypoints:[new("A",start.Latitude,start.Longitude),new("B",start.Latitude,start.Longitude+.1)]):null;
                var prediction=predictor.Predict(frame,frame.TimestampUtc,[RemoteKind.Terrain,RemoteKind.Thermal,RemoteKind.Multispectral],prefs,45,plan);prepared+=prediction.Areas.Length;
                var available=science.Availability(p);
                foreach(var kind in new[]{RemoteKind.Terrain,RemoteKind.Thermal,RemoteKind.Multispectral})
                {
                    var source=ScientificSourceSelection.Select(available,kind,prefs);if(source is null){unavailable++;continue;}reused++;
                    var footprint=SensorGeometry.Footprint(frame,kind==RemoteKind.Terrain?new(30,3,Swath:true):new());var positions=SensorGeometry.Samples(footprint);
                    var bands=source.Manifest.Bands.Select(b=>{using var raster=new ScientificRaster(Path.Combine(library,source.RelativeDirectory),b);return new RemoteBandValues(b.Id,b.Unit,b.WavelengthNm,positions.Select(raster.Read).ToArray(),"Actual original values / synthetic replay trajectory",raster.Grid);}).ToArray();
                    var derived=RemoteCalculations.Apply(kind,bands);var valid=derived.Values.SelectMany(v=>v).Count(v=>v.HasValue);samples+=valid;if(valid>0){successes++;firstReady??=watch.Elapsed.TotalSeconds;}else unavailable++;
                }
                previous=p;
            }
            results.Add(new{scenario,networkBytes=0,httpRequests=0,retainedBytes=science.Sources().Sum(s=>s.Files.Sum(f=>f.Bytes)),sourceReuse=reused,successfulSampleAcquisitions=successes,missingAcquisitionIntervals=unavailable,validValues=samples,predictedAreas=prepared,firstReadySeconds=firstReady,processingSeconds=watch.Elapsed.TotalSeconds,scope="Offline synthetic trajectory replay over actual retained provider data; no flight-time network or MSFS performance claim"});
            Console.WriteLine(scenario+" actual retained-data replay finished");
        }
        File.WriteAllText(Path.Combine(output,"scientific-trace-benchmarks.json"),JsonSerializer.Serialize(new{checkedUtc=DateTimeOffset.UtcNow,evidenceType="genuine-retained-data-synthetic-trajectory-benchmark",windowsMsfs="not tested",networkComparison="not established by offline replay",results},WireJson.Options));
    }
}
