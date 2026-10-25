using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using System.Net;
using System.Security.Cryptography;
using Xunit;
namespace AeroScience.Tests;
[Collection(ScientificIoCollection.Name)]
public sealed class ScientificSourceReuseTests:IDisposable
{
 readonly string root=Path.Combine(Path.GetTempPath(),"scientific-reuse-"+Guid.NewGuid().ToString("N"));
 readonly ScienceStore science;readonly ScientificAccessSettings settings;readonly AutomaticScientificData automatic;readonly ScientificCatalog catalog;readonly Handler handler=new();
 readonly GeographicBounds area=new(-75.01,39.99,-74.99,40.01);
 public ScientificSourceReuseTests(){var paths=new StorageManager(new PreferenceStore(root)).Initialize();science=new(paths.ScienceLibraryDirectory);science.Initialize();settings=new(root,()=>true);settings.Save(new(true,true));automatic=new(science,settings,new(handler));catalog=new(automatic,science,settings);}
 sealed class Handler:HttpMessageHandler
 {
  public int Numeric;public string? LastBbox;
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken token)
  {
   if(r.RequestUri!.AbsolutePath.Contains("exportImage"))
   {
    Numeric++;var query=System.Web.HttpUtility.ParseQueryString(r.RequestUri.Query);LastBbox=query["bbox"];var p=LastBbox!.Split(',').Select(v=>double.Parse(v,System.Globalization.CultureInfo.InvariantCulture)).ToArray();var file=Path.Combine(Path.GetTempPath(),"reuse-response-"+Guid.NewGuid().ToString("N")+".tif");
    try{RemoteFixtureFiles.GeoTiff(file,123.25f,10,(p[2]-p[0])/10,west:p[0],north:p[3]);return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(File.ReadAllBytes(file))});}finally{File.Delete(file);}
   }
   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"pixelType\":\"F32\",\"bandCount\":1,\"description\":\"SYNTHETIC cumulative reuse fixture; not genuine acceptance\"}")});
  }
 }
 GeographicBounds[] Quarters()=>[new(area.West,area.South,-75,40),new(-75,area.South,area.East,40),new(area.West,40,-75,area.North),new(-75,40,area.East,area.North)];
 async Task<ScientificSceneSelection> Selection(){var item=Assert.Single((await catalog.Search(new("usgs-3dep",area))).Items);return new(item.Provider,item.Scene,item.Revision,item.Bands.Select(b=>b.Id).ToArray());}
 async Task<ScientificSource> Import(GeographicBounds part,ScientificSceneSelection selection,bool legacy=false,string? datum=null,float value=123.25f,bool guardBorder=false)
 {
  var d=(await automatic.Provider("usgs-3dep").Discover(RemoteKind.Terrain,part,settings.Get().Preferences,new(1048576),default))!;
  var folder=Path.Combine(root,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);var pixel=(part.East-part.West)/10;RemoteFixtureFiles.GeoTiff(Path.Combine(folder,"elevation.tif"),value,guardBorder?12:10,pixel,west:part.West-(guardBorder?pixel:0),north:part.North+(guardBorder?pixel:0));
  var provenance=new AutomaticSourceProvenance("usgs-3dep-numeric",d.Catalog,d.Item,[],part,DateTimeOffset.UtcNow,"Synthetic native file; no flight observation","Original fixture nodata applies","Synthetic native grid; not NASA/FAA data",0,[],d.ProviderMetadataJson,100,value==-9999?0:100,legacy?null:selection.Revision);
  return await science.ImportSourceAsync(d.Manifest with{VerticalDatum=datum??d.Manifest.VerticalDatum,Acquisition=provenance},folder);
 }
 bool Verify(ScientificSource source)=>source.Files.All(f=>{using var file=File.OpenRead(Path.Combine(science.DirectoryPath,source.RelativeDirectory,f.RelativePath));return file.Length==f.Bytes&&Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant()==f.Sha256;});
 async Task<LibraryDownloadJob> Terminal(Guid id){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));while(true){var j=science.LibraryJob(id);if(j.State is "Completed" or "Failed" or "Interrupted")return j;await Task.Delay(20,timeout.Token);}}
 [Fact]public async Task FourVerifiedNativeWindowsUnionOfflineWithoutTransferAndSurviveRestart()
 {
  var selection=await Selection();var originals=new List<ScientificSource>();foreach(var q in Quarters())originals.Add(await Import(q,selection,legacy:true));
  var plan=ScientificSourceSelection.ReuseArea(science,area,_=>true,Verify);Assert.Empty(plan.Missing);Assert.Empty(plan.QualityLimited);Assert.False(plan.Uncertain);Assert.Equal(4,plan.Reused.Select(r=>r.Source.Id).Distinct().Count());
  settings.Save(new());using var jobs=new ScientificLibraryJobs(science,catalog,automatic,settings);var preview=await jobs.Preview(new(1,[area],[selection]));Assert.Equal(0,preview.Preview.EstimatedTransferBytes);Assert.Equal(originals.Sum(s=>s.Files.Sum(f=>f.Bytes)),preview.Preview.ReusableBytes);Assert.Equal(4,preview.Preview.Work.Length);
  await jobs.Approve(preview.Id,preview.Preview.SelectionHash);var completed=await Terminal(preview.Id);Assert.Equal("Completed",completed.State);Assert.Equal(0,completed.DownloadedBytes);Assert.Equal(0,handler.Numeric);Assert.Equal(originals.Select(s=>s.Id).Order(),completed.Items.Select(i=>i.SourceId!.Value).Order());
  science.Dispose();using var reopened=new ScienceStore(science.DirectoryPath);reopened.Initialize();Assert.Equal("Completed",reopened.LibraryJob(preview.Id).State);Assert.Equal(4,reopened.CountSources());Assert.All(reopened.StreamSources(),s=>Assert.Equal(s.ManifestSha256,originals.Single(o=>o.Id==s.Id).ManifestSha256));
 }
 [Fact]public async Task OnlyMissingQuarterIsDownloadedAndRepeatSurveyTransfersZeroBytes()
 {
  var selection=await Selection();foreach(var q in Quarters().Take(3))await Import(q,selection);
  using var jobs=new ScientificLibraryJobs(science,catalog,automatic,settings);var preview=await jobs.Preview(new(1,[area],[selection]));var missing=Assert.Single(preview.Preview.Work,w=>w.ReuseSourceId is null);Assert.Equal(Quarters()[3],missing.Area);Assert.Equal(3,preview.Preview.Work.Count(w=>w.ReuseSourceId is not null));
  await jobs.Approve(preview.Id,preview.Preview.SelectionHash);var completed=await Terminal(preview.Id);Assert.Equal("Completed",completed.State);Assert.Equal(1,handler.Numeric);Assert.True(completed.DownloadedBytes>0);var bytes=completed.DownloadedBytes;
  var repeat=await jobs.Preview(new(1,[area],[selection]));Assert.Equal(0,repeat.Preview.EstimatedTransferBytes);await jobs.Approve(repeat.Id,repeat.Preview.SelectionHash);Assert.Equal("Completed",(await Terminal(repeat.Id)).State);Assert.Equal(0,science.LibraryJob(repeat.Id).DownloadedBytes);Assert.Equal(1,handler.Numeric);Assert.True(bytes>0);
 }
 [Fact]public async Task DatumMismatchAndNodataDoNotCreateFalseCombinedCoverage()
 {
  var selection=await Selection();foreach(var q in Quarters().Take(3))await Import(q,selection);await Import(Quarters()[3],selection,datum:"Scientifically incompatible vertical datum");
  var plan=ScientificSourceSelection.ReuseArea(science,area,_=>true,Verify);Assert.NotEmpty(plan.Missing);Assert.Contains(plan.Limitations,l=>l.Contains("another date, datum"));
  await Import(Quarters()[3],selection,value:-9999);var invalid=ScientificSourceSelection.ReuseArea(science,area,_=>true,Verify);Assert.NotEmpty(invalid.QualityLimited);using var jobs=new ScientificLibraryJobs(science,catalog,automatic,settings);await Assert.ThrowsAsync<InvalidOperationException>(()=>jobs.Preview(new(1,[area],[selection])));Assert.Equal(0,handler.Numeric);
 }
 [Fact]public async Task DeletedAndModifiedOriginalsCannotBeUsedByTheUnion()
 {
  var selection=await Selection();var list=new List<ScientificSource>();foreach(var q in Quarters())list.Add(await Import(q,selection));var path=Path.Combine(science.DirectoryPath,list[3].RelativeDirectory,list[3].Files[0].RelativePath);File.Delete(path);
  Assert.NotEmpty(ScientificSourceSelection.ReuseArea(science,area,_=>true,Verify).Missing);
  path=Path.Combine(science.DirectoryPath,list[0].RelativeDirectory,list[0].Files[0].RelativePath);var data=File.ReadAllBytes(path);data[^1]^=1;File.WriteAllBytes(path,data);Assert.Throws<IOException>(()=>ScientificSourceSelection.ReuseArea(science,area,_=>true,Verify));
  using var cancel=new CancellationTokenSource();cancel.Cancel();Assert.Throws<OperationCanceledException>(()=>ScientificSourceSelection.ReuseArea(science,area,_=>true,Verify,cancel.Token));
 }
 [Fact]public async Task AutomaticPreparationUsesTheSameVerifiedUnionWithoutNetworkConsent()
 {
  var selection=await Selection();var list=new List<ScientificSource>();foreach(var q in Quarters())list.Add(await Import(q,selection));settings.Save(new());automatic.Prepare(area,[RemoteKind.Terrain]);using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));ScientificDataReadiness status;
  do{status=automatic.Snapshot().Single();if(status.Status is not("Queued" or "Downloading"))break;await Task.Delay(20,timeout.Token);}while(true);
  Assert.Equal("Ready",status.Status);Assert.Null(status.SourceId);Assert.Equal(list.Select(s=>s.Id).Order(),status.SourceIds!.Order());Assert.Equal(0,status.DownloadedBytes);Assert.Equal(0,handler.Numeric);
 }
 [Fact]public async Task AutomaticPreparationRequestsOnlyTheRealMissingWindow()
 {
  var selection=await Selection();foreach(var q in Quarters().Take(3))await Import(q,selection);automatic.Prepare(area,[RemoteKind.Terrain]);using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));ScientificDataReadiness status;
  do{status=automatic.Snapshot().Single();if(status.Status is not("Queued" or "Downloading"))break;await Task.Delay(20,timeout.Token);}while(true);
  Assert.Equal("Ready",status.Status);Assert.Equal(1,handler.Numeric);var actual=handler.LastBbox!.Split(',').Select(v=>double.Parse(v,System.Globalization.CultureInfo.InvariantCulture)).ToArray();Assert.Equal(new[]{Quarters()[3].West,Quarters()[3].South,Quarters()[3].East,Quarters()[3].North},actual);Assert.Equal(4,status.SourceIds!.Length);Assert.True(status.ReusedBytes>0);Assert.True(status.DownloadedBytes>0);
 }
 [Theory][InlineData("date")][InlineData("units")][InlineData("scale")][InlineData("resolution")]
 public async Task ScientificDifferencesCannotBeHiddenByGeographicUnion(string changed)
 {
  foreach(var (q,index) in Quarters().Select((q,i)=>(q,i))){var folder=Path.Combine(root,Guid.NewGuid().ToString("N"));var path=RemoteFixtureFiles.Create(folder,RemoteKind.Terrain);var m=ScienceStore.ReadSourceManifest(path);RemoteFixtureFiles.GeoTiff(Path.Combine(folder,"elevation.tif"),123.25f,index==3&&changed=="resolution"?5:10,index==3&&changed=="resolution"?.002:.001,west:q.West,north:q.North);
   var provenance=new AutomaticSourceProvenance("stac-cog-native-subset","SYNTHETIC catalog","SYNTHETIC common tile",[],q,DateTimeOffset.UtcNow,"Synthetic compatibility contract","Native QA applies","Synthetic native grid",0,[],CatalogRevision:"SYNTHETIC exact processing revision");
   m=m with{Acquisition=provenance,ObservationUtc=index==3&&changed=="date"?m.ObservationUtc!.Value.AddDays(1):m.ObservationUtc,Bands=m.Bands.Select(b=>b with{Unit=index==3&&changed=="units"?"ft":b.Unit,Scale=index==3&&changed=="scale"?2:b.Scale}).ToArray()};if(index==3&&changed=="units")await Assert.ThrowsAsync<InvalidDataException>(()=>science.ImportSourceAsync(m,folder));else await science.ImportSourceAsync(m,folder);
  }
  var plan=ScientificSourceSelection.ReuseArea(science,area,_=>true,Verify);Assert.NotEmpty(plan.Missing);Assert.Equal(3,plan.Reused.Select(r=>r.Source.Id).Distinct().Count());Assert.Equal(0,handler.Numeric);
 }
 [Fact]public async Task RealCloudMaskPreventsFootprintFromBecomingValidCoverageAndReadBudgetIsExplicit()
 {
  var folder=Path.Combine(root,"cloudy");var m=ScienceStore.ReadSourceManifest(RemoteFixtureFiles.Create(folder,RemoteKind.Multispectral,cloudy:true));m=m with{Acquisition=new("stac-cog-native-subset","SYNTHETIC catalog","SYNTHETIC scene",[],area,DateTimeOffset.UtcNow,"Fixture","QA cloud flag 9","Synthetic grids",0,[],CatalogRevision:"synthetic")};await science.ImportSourceAsync(m,folder);
  var plan=ScientificSourceSelection.ReuseArea(science,area,_=>true,Verify);Assert.Empty(plan.Reused);Assert.NotEmpty(plan.QualityLimited);Assert.NotEmpty(plan.Missing);
  var budget=new ScientificReuseContext{RemainingReads=0};var bounded=ScientificSourceSelection.ReuseArea(science,area,_=>true,Verify,context:budget);Assert.True(bounded.Uncertain);Assert.Empty(bounded.Reused);Assert.Equal(0,handler.Numeric);
 }
 [Fact]public async Task SimultaneousExactPreparationSharesOneVerifiedOriginalAndOneTransfer()
 {
  var selected=await Selection();var d=(await automatic.Provider("usgs-3dep").Discover(RemoteKind.Terrain,area,settings.Get().Preferences,new(1048576),default))! with{CatalogRevision=selected.Revision};var first=new ScientificDownloadBudget(1048576);var second=new ScientificDownloadBudget(1048576);
  var acquired=await Task.WhenAll(automatic.PrepareSource(d,first,default),automatic.PrepareSource(d,second,default));Assert.Equal(acquired[0]!.Id,acquired[1]!.Id);Assert.Equal(1,handler.Numeric);Assert.Equal(1,science.CountSources());Assert.True(first.Bytes+second.Bytes>0);Assert.True(first.Bytes==0||second.Bytes==0);
 }
 [Fact]public async Task ActivePreparationProtectsEveryContributingSourceAndRemovalInvalidatesItsSavedRegion()
 {
  var selected=await Selection();var plan=new MissionPlan(Guid.NewGuid(),"Synthetic multi-source dependency route",Waypoints:[new("A",40.005,-75.015),new("B",40.015,-75.005)],Instruments:[new("terrain-scanner",true)],UpdatedUtc:DateTimeOffset.UtcNow);
  // Seed the actual backend plan cell (0.04°), not an assumed Library cell (0.02°).
  // Native edge pixels include a real one-pixel crop border, as COG subset windows do.
  var full=Assert.Single(ScientificPreparationScope.Preview(plan).Areas);var c=full.Center;var originals=new List<ScientificSource>();foreach(var q in new GeographicBounds[]{new(full.West,full.South,c.Longitude,c.Latitude),new(c.Longitude,full.South,full.East,c.Latitude),new(full.West,c.Latitude,c.Longitude,full.North),new(c.Longitude,c.Latitude,full.East,full.North)})originals.Add(await Import(q,selected,guardBorder:true));
  settings.Save(new());var preparation=new ScientificPlanData(automatic,settings);preparation.Prepare(plan,true);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
  while(!deadline.IsCancellationRequested){preparation.Tick();if(preparation.Regions(plan.Id).Any(r=>r.Status=="Ready"&&r.SourceIds?.Length==4))break;try{await Task.Delay(20,deadline.Token);}catch(OperationCanceledException){break;}}
  Assert.True(preparation.Regions(plan.Id).Any(r=>r.Status=="Ready"&&r.SourceIds?.Length==4),System.Text.Json.JsonSerializer.Serialize(new{regions=preparation.Regions(plan.Id),activity=automatic.Snapshot()},WireJson.Options));
  Assert.All(originals,s=>Assert.NotEmpty(preparation.SourceDeletionDependencies(s)));preparation.Control(plan.Id,"pause");Assert.All(originals,s=>Assert.Empty(preparation.SourceDeletionDependencies(s)));automatic.ForgetDeletedSources();preparation.SourcesRemoved([originals[0].Id]);Assert.Contains(preparation.Regions(plan.Id),r=>r.Status=="Dataset Required"&&r.SourceIds is null);
 }
 [Fact]public async Task OfficialStyleRoundedTiffEdgesUseExistingRequestCoverageWithoutChangingOriginalGrid()
 {
  var selected=await Selection();var target=new GeographicBounds(-75.04,40,-75.02,40.02);var list=new List<ScientificSource>();
  foreach(var part in new GeographicBounds[]{new(-75.04,40,-75.03,40.02),new(-75.03,40,-75.02,40.02)})
  {
   var d=(await automatic.Provider("usgs-3dep").Discover(RemoteKind.Terrain,part,settings.Get().Preferences,new(1048576),default))!;var folder=Path.Combine(root,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
   RemoteFixtureFiles.GeoTiff(Path.Combine(folder,"elevation.tif"),123.25f,256,7.812499999998446e-05,west:part.West-.005,north:40.02000000000001);
   var provenance=new AutomaticSourceProvenance("usgs-3dep-numeric",d.Catalog,d.Item,[],part,DateTimeOffset.UtcNow,"SYNTHETIC reproduction of original TIFF encoding, not genuine evidence",d.Quality,d.Resolution,0,[],d.ProviderMetadataJson,81,81,selected.Revision);list.Add(await science.ImportSourceAsync(d.Manifest with{Acquisition=provenance},folder));
  }
  var grids=list.Select(s=>s.Grids["elevation"]).ToArray();Assert.All(grids,g=>Assert.True(g.OriginY+g.Height*g.PixelHeight>40));
  var result=ScientificSourceSelection.ReuseArea(science,target,_=>true,Verify);Assert.Empty(result.Missing);Assert.Empty(result.QualityLimited);Assert.Equal(2,result.Reused.Select(r=>r.Source.Id).Distinct().Count());Assert.Equal(grids,list.Select(s=>science.Source(s.Id)!.Grids["elevation"]).ToArray());Assert.Equal(0,handler.Numeric);
 }
 public void Dispose(){automatic.Dispose();science.Dispose();try{Directory.Delete(root,true);}catch(IOException){}}
}
