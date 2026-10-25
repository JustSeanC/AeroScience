using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;
namespace AeroScience.Tests;
[Collection(ScientificIoCollection.Name)]
public sealed class ScientificSourceInspectionTests:IDisposable
{
 readonly string root=Path.Combine(Path.GetTempPath(),"source-inspection-"+Guid.NewGuid().ToString("N"));readonly ScienceStore science;readonly AutomaticScientificData automatic;
 public ScientificSourceInspectionTests(){var paths=new StorageManager(new PreferenceStore(root)).Initialize();science=new(paths.ScienceLibraryDirectory);science.Initialize();automatic=new(science,new(root));}
 async Task<ScientificSource> Source(RemoteKind kind=RemoteKind.Terrain,bool cloudy=false)=>await science.ImportSourceAsync(RemoteFixtureFiles.Create(Path.Combine(root,Guid.NewGuid().ToString("N")),kind,cloudy));
 JsonElement Read(ScientificSource s,string band)=>JsonSerializer.SerializeToElement(science.InspectSource(s.Id,new(new(-75.01,39.99,-74.99,40.01),band),x=>automatic.VerifyRetainedSource(x)),WireJson.Options);
 [Fact]public async Task RetainedNumericInspectionPreservesOriginalsNativeCoordinatesUnitsAndNoFlightCredit()
 {
  var source=await Source();var result=Read(source,"elevation");Assert.Equal(123.25,result.GetProperty("minimum").GetDouble());Assert.Equal("m",result.GetProperty("unit").GetString());var samples=result.GetProperty("samples").EnumerateArray().ToArray();Assert.InRange(samples.Length,1,576);Assert.All(samples,s=>{Assert.Equal(123.25,s.GetProperty("value").GetDouble());Assert.Equal(123.25,s.GetProperty("decodedSourceValue").GetDouble());});
  Assert.Equal(samples.Length,samples.Select(s=>(s.GetProperty("column").GetInt32(),s.GetProperty("row").GetInt32())).Distinct().Count());Assert.True(automatic.VerifyRetainedSource(source));Assert.Empty(science.Products());Assert.Empty(science.Datasets());
 }
 [Fact]public async Task SourceCloudQualityIsSeparateFromOriginalDecodedValuesAndNoFalseColorScale()
 {
  var source=await Source(RemoteKind.Multispectral,true);var result=Read(source,"B04");Assert.Equal(JsonValueKind.Null,result.GetProperty("minimum").ValueKind);Assert.Empty(result.GetProperty("layer").GetProperty("features").EnumerateArray());Assert.All(result.GetProperty("samples").EnumerateArray(),s=>{Assert.Equal("Quality excluded",s.GetProperty("quality").GetString());Assert.Equal(JsonValueKind.Number,s.GetProperty("decodedSourceValue").ValueKind);Assert.Equal(JsonValueKind.Null,s.GetProperty("value").ValueKind);});
 }
 [Fact]public async Task ReadBoundsCancellationAndOriginalIntegrityFailClosed()
 {
  var source=await Source();Assert.Throws<ArgumentException>(()=>science.InspectSource(source.Id,new(new(-75.01,39.99,-74.99,40.01),"elevation",10000),_=>true));Assert.Throws<ArgumentException>(()=>Read(source,"unsupported"));
  using var cancel=new CancellationTokenSource();cancel.Cancel();Assert.Throws<OperationCanceledException>(()=>science.InspectSource(source.Id,new(new(-75.01,39.99,-74.99,40.01),"elevation"),_=>true,cancel.Token));
  var path=Path.Combine(science.DirectoryPath,source.RelativeDirectory,source.Files[0].RelativePath);var bytes=File.ReadAllBytes(path);bytes[^1]^=1;File.WriteAllBytes(path,bytes);Assert.Throws<IOException>(()=>Read(source,"elevation"));Assert.Empty(science.Products());
 }
 [Fact]public async Task NdviIsExplicitlyDerivedAndDoesNotModifySourceReflectance()
 {
  var source=await Source(RemoteKind.Multispectral);var result=Read(source,"ndvi");Assert.True(result.GetProperty("derived").GetBoolean());Assert.Equal("1",result.GetProperty("unit").GetString());Assert.InRange(result.GetProperty("minimum").GetDouble(),-1,1);Assert.True(automatic.VerifyRetainedSource(source));
 }
 [Theory]
 [InlineData("EPSG:32618",500040,4429920,40.019486199181074,-74.99953126152131)]
 [InlineData("EPSG:32755",345000,6250000,-33.878965845662975,145.32402781796569)]
 [InlineData("EPSG:32633",500000,9100000,81.95674023788764,14.999999999999982)]
 public void InspectionNativeCentersMatchIndependentProjInBothHemispheres(string crs,double x,double y,double latitude,double longitude)
 {
  var grid=new RasterGrid(1,1,crs,x-5,y+5,10,-10);var p=ScientificRaster.InspectionGridPoint(grid,.5,.5);Assert.InRange(Math.Abs(p.Latitude-latitude),0,1e-11);Assert.InRange(Math.Abs(p.Longitude-longitude),0,1e-11);Assert.Equal(x-5,grid.OriginX);Assert.Equal(y+5,grid.OriginY);
 }
 public void Dispose(){automatic.Dispose();science.Dispose();try{Directory.Delete(root,true);}catch(IOException){}}
}
