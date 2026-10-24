using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Engine;
using Xunit;

namespace AeroScience.Tests;

[Collection(ScientificIoCollection.Name)]
public sealed class EmitProductTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-synthetic-emit-"+Guid.NewGuid().ToString("N"));
    private readonly Guid library=Guid.NewGuid();
    private static ScientificHdfReader Reader()=>new(new(Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),"../../..",OperatingSystem.IsWindows()?"dotnet.exe":"dotnet")),["exec","--runtimeconfig",Path.Combine(AppContext.BaseDirectory,"AeroScience.Tests.runtimeconfig.json"),"--depsfile",Path.Combine(AppContext.BaseDirectory,"AeroScience.Tests.deps.json"),typeof(EngineApplication).Assembly.Location]));
    public EmitProductTests(){Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"library.json"),$"{{\"formatVersion\":1,\"libraryId\":\"{library}\"}}");}
    private HdfOriginal Copy(string role,string variant="standard")
    {
        var file=$"EMIT_L2A_{role}_{(role=="MASK"?"003":"002")}_20990101T000000.nc";var relative="Sources/"+Guid.NewGuid().ToString("N")+"/"+file;
        var path=Path.Combine(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.Copy(Path.Combine(AppContext.BaseDirectory,"EmitFixtures",variant,file),path);using var stream=File.OpenRead(path);
        return new(root,library,relative,stream.Length,Convert.ToHexStringLower(SHA256.HashData(stream)));
    }
    private EmitOriginals Bundle(string variant="standard")=>new(Copy("RFL",variant),Copy("RFLUNCERT",variant),Copy("MASK",variant));
    [Fact]public void UnorderedCompanionResolutionIsDeterministicAndMissingOrDuplicateRolesAreNotGuessed()
    {
        var b=Bundle();Assert.Equal(b,EmitProductDecoder.ResolveOriginals([b.Mask,b.Reflectance,b.Uncertainty]));
        Assert.Equal("Companion",Assert.Throws<EmitProductException>(()=>EmitProductDecoder.ResolveOriginals([b.Mask,b.Reflectance])).Code);
        Assert.Equal("Companion",Assert.Throws<EmitProductException>(()=>EmitProductDecoder.ResolveOriginals([b.Mask,b.Reflectance,b.Reflectance])).Code);
    }
    [Fact]public async Task DefinitionUsesAuthenticWriterTypesVersionsLabelsAndUtcIdentityNotResearchFixtureGuesses()
    {
        var decoder=new EmitProductDecoder(Reader());var originals=Bundle();var p=await decoder.OpenAsync(originals);var d=p.Definition;
        Assert.Equal(285,d.Bands);Assert.Equal(3,d.Rows);Assert.Equal(4,d.Columns);Assert.Equal(2,d.GridRows);Assert.Equal(4,d.GridColumns);
        Assert.Equal(new[]{"002","002","003"},d.Sources.Select(s=>s.Version));Assert.Equal(new[]{"EMITL2ARFL","EMITL2ARFL","EMITL2AMASK"},d.Sources.Select(s=>s.Collection));
        Assert.Equal(d.Sources[0].Granule,d.Sources[1].Granule);Assert.StartsWith("EMIT_L2A_RFLUNCERT_002_",d.Sources[1].OriginalFileName);
        Assert.Equal("unitless",d.Variables["RFL"].Units);Assert.Equal("UInt8",d.Variables["RFL:sensor_band_parameters/good_wavelengths"].NumericType);
        Assert.Equal(400,d.WavelengthNm[0]);Assert.Equal((double)(float)(400+284*7.4),d.WavelengthNm[284]);Assert.All(d.FwhmNm,v=>Assert.Equal(8,v));Assert.Equal(0,d.GoodWavelengths[9]);
        Assert.Equal("SpecTf-Cloud Probability",d.MaskLabels[4]);Assert.Equal("SpecTf-Cloud Flag",d.MaskLabels[5]);Assert.Equal("SpecTf-Buffer Distance",d.MaskLabels[6]);
        Assert.Equal(DateTimeOffset.Parse("2099-01-01T00:00:00Z"),d.StartUtc);Assert.Equal(d.StartUtc.AddSeconds(12),d.EndUtc);Assert.Equal(originals.Mask.Sha256,d.Sources[2].Sha256);
        Assert.Contains("CF defaults",d.Variables["RFL"].ScalingOrigin);Assert.Contains("not asserted",d.Acceptance);Assert.DoesNotContain(root,JsonSerializer.Serialize(d));
    }
    [Fact]public async Task NativeValuesUncertaintyAndQualityRemainDistinctIncludingContinuousProbabilityAndOptionalWater()
    {
        var decoder=new EmitProductDecoder(Reader());var p=await decoder.OpenAsync(Bundle());var output=await decoder.ReadNativeAsync(p,new(0,0,2,4,0,5));
        var a=output.Pixels[0];Assert.Equal((double).125f,a.Reflectance[0]);Assert.Equal((double).005f,a.Uncertainty[0]);Assert.Equal(a.Reflectance.ToArray(),a.UsableReflectance.ToArray());Assert.Equal("Usable under selected QA policy",a.State);
        Assert.All(output.Pixels[1].UsableReflectance,v=>Assert.Null(v));Assert.All(output.Pixels[1].BandStates,s=>Assert.Equal("Quality excluded",s));Assert.NotNull(output.Pixels[1].Reflectance[0]);
        Assert.Equal("Usable under selected QA policy",output.Pixels[2].State);Assert.Equal((double).9f,output.Pixels[2].QualityValues[4]); // probability is not a binary flag
        Assert.Equal("Usable under selected QA policy",output.Pixels[3].State);var water=await decoder.ReadNativeAsync(p,new(0,3,1,1,0,1),new(ExcludeWater:true));Assert.Null(water.Pixels[0].UsableReflectance[0]);
        Assert.Equal("Quality excluded",output.Pixels[4].BandStates[0]);Assert.Equal("Quality unknown",output.Pixels[5].State);Assert.Equal("Quality unknown",output.Pixels[6].State);
        Assert.Equal(new[]{.125f,.126f,.127f,.128f,.129f},MemoryMarshal.Cast<byte,float>(a.OriginalReflectanceBytes).ToArray());
    }
    [Fact]public async Task PackedBitOrderExcludedWavelengthsFillAndUncertaintyAreNotCountedAsSupportedMeasurements()
    {
        var decoder=new EmitProductDecoder(Reader());var p=await decoder.OpenAsync(Bundle());var r=await decoder.ReadNativeAsync(p,new(0,0,1,1,8,5));var a=r.Pixels[0];
        Assert.Equal("Interpolated band excluded",a.BandStates[0]);Assert.Equal("Excluded wavelength",a.BandStates[1]);Assert.Equal("Reflectance fill or invalid",a.BandStates[2]);Assert.Equal("Uncertainty unavailable or invalid",a.BandStates[3]);
        Assert.Equal(128,a.OriginalPackedBandMask[1]);Assert.Null(a.UsableReflectance[0]);Assert.NotNull(a.Reflectance[0]);Assert.Equal(-9999,MemoryMarshal.Cast<byte,float>(a.OriginalReflectanceBytes)[2]);
        var keep=await decoder.ReadNativeAsync(p,new(0,0,1,1,8,1),new(ExcludeInterpolatedBands:false));Assert.NotNull(keep.Pixels[0].UsableReflectance[0]);
    }
    [Fact]public async Task CfScalingPreservesOriginalBitsAndDoesNotManufactureCalibration()
    {
        var decoder=new EmitProductDecoder(Reader());var p=await decoder.OpenAsync(Bundle("scaled"));var a=(await decoder.ReadNativeAsync(p,new(0,0,1,1,0,1))).Pixels[0];
        Assert.Equal(.125f,MemoryMarshal.Cast<byte,float>(a.OriginalReflectanceBytes)[0]);Assert.Equal((double).125f*2-.1,a.Reflectance[0]);Assert.Equal((double).005f*.5,a.Uncertainty[0]);Assert.Equal("Original CF scale_factor/add_offset, with CF defaults for absent members",p.Definition.Variables["RFL"].ScalingOrigin);
    }
    [Fact]public async Task ProducerUtcOffsetFormatIsAcceptedWithoutGuessingAnAbsentTimezone()
    {
        var p=await new EmitProductDecoder(Reader()).OpenAsync(Bundle("producer"));Assert.Equal(DateTimeOffset.Parse("2099-01-01T00:00:00Z"),p.Definition.StartUtc);Assert.Equal("2099-01-01T00:00:00+0000",p.Definition.Sources[0].OriginalAttributes["time_coverage_start"].Strings![0]);
    }
    [Fact]public async Task NegativeAndAboveOneReflectanceArePreservedWhileNonfiniteAndNegativeUncertaintyAreExplicit()
    {
        var decoder=new EmitProductDecoder(Reader());var product=await decoder.OpenAsync(Bundle("extremes"));var result=await decoder.ReadNativeAsync(product,new(0,0,1,1,0,4));var p=result.Pixels[0];
        Assert.Equal((double)(-.02f),p.UsableReflectance[0]);Assert.Equal((double)1.2f,p.Reflectance[1]);Assert.Equal((double)(-.003f),p.Uncertainty[1]);Assert.Null(p.UsableReflectance[1]);Assert.Equal("Uncertainty unavailable or invalid",p.BandStates[1]);
        Assert.True(float.IsNaN(MemoryMarshal.Cast<byte,float>(p.OriginalReflectanceBytes)[2]));Assert.True(float.IsPositiveInfinity(MemoryMarshal.Cast<byte,float>(p.OriginalReflectanceBytes)[3]));Assert.Null(p.Reflectance[2]);Assert.Null(p.UsableReflectance[3]);
    }
    [Fact]public async Task WorkAndMetadataBoundsRejectLargeReadsAndOversizedStringsWithoutRelaxingReaderSecurity()
    {
        var decoder=new EmitProductDecoder(Reader());var product=await decoder.OpenAsync(Bundle("limits"));
        Assert.Equal("Limit",(await Assert.ThrowsAsync<EmitProductException>(()=>decoder.ReadNativeAsync(product,new(0,0,20,20,0,285)))).Code);
        Assert.Equal("Limit",(await Assert.ThrowsAsync<EmitProductException>(()=>decoder.ReadNativeAsync(product,new(0,0,120,100,0,1)))).Code);
        Assert.Equal("Limit",(await Assert.ThrowsAsync<EmitProductException>(()=>decoder.ReadGeographicAsync(product,new(0,0,16,16,0,1)))).Code);
        var b=Bundle() with{Mask=Copy("MASK","oversized-string")};Assert.Equal("Limit",(await Assert.ThrowsAsync<ScientificHdfException>(()=>decoder.OpenAsync(b))).Code);
    }
    [Fact]public async Task ExternalFileCanaryCannotEnterThroughTheProductDecoder()
    {
        var b=Bundle();var path=Path.Combine(root,b.Reflectance.RelativePath);File.Copy(Path.Combine(AppContext.BaseDirectory,"HdfFixtures","previous-canary-reflectance.nc"),path,true);using(var stream=File.OpenRead(path))b=b with{Reflectance=b.Reflectance with{Bytes=stream.Length,Sha256=Convert.ToHexStringLower(SHA256.HashData(stream))}};
        Assert.Equal("ExternalStorage",(await Assert.ThrowsAsync<ScientificHdfException>(()=>new EmitProductDecoder(Reader()).OpenAsync(b))).Code);
    }
    [Fact]public async Task CompletePackedFillVectorCannotBecomeClearedQuality()
    {
        var decoder=new EmitProductDecoder(Reader());var product=await decoder.OpenAsync(Bundle() with{Mask=Copy("MASK","missing-packed")});var p=(await decoder.ReadNativeAsync(product,new(0,0,1,1,0,5))).Pixels[0];
        Assert.Equal("Quality unknown",p.State);Assert.All(p.UsableReflectance,v=>Assert.Null(v));Assert.All(p.OriginalPackedBandMask,b=>Assert.Equal(241,b));Assert.NotNull(p.Reflectance[0]);
    }
    [Fact]public async Task GeographicWindowUsesOneBasedGltNativeCoordinatesAndOnlyActualRequestedQaSupport()
    {
        var decoder=new EmitProductDecoder(Reader());var p=await decoder.OpenAsync(Bundle());var g=await decoder.ReadGeographicAsync(p,new(0,0,2,4,0,5));
        Assert.Equal(8,g.Cells.Length);var first=g.Cells[0];Assert.Equal(0,first.SourceColumn);Assert.Equal(0,first.SourceRow);Assert.Equal(37.995,first.GridLatitude,10);Assert.Equal(-119.995,first.GridLongitude,10);Assert.Equal(38,first.Spectrum!.Latitude);Assert.Equal(-120,first.Spectrum.Longitude);
        Assert.Equal("Not geolocated",g.Cells[2].State);Assert.Null(g.Cells[2].Spectrum);Assert.Equal("Invalid GLT reference",g.Cells[3].State);Assert.Null(g.Cells[3].SourceColumn);Assert.Equal("Invalid GLT reference",g.Cells[6].State);
        Assert.Equal(3,g.Cells.Count(c=>c.Spectrum?.State=="Usable under selected QA policy"));Assert.Contains("not the scene outline",g.Support);
    }
    [Theory][InlineData("spectral","Companion")][InlineData("time","Companion")][InlineData("processing","Companion")][InlineData("labels","UnsupportedLayout")][InlineData("version","Identity")][InlineData("missing-units","Metadata")]
    public async Task UnsupportedOrIncompatibleOriginalMetadataFailsWithoutSubstitution(string variant,string code)
    {
        var bundle=Bundle();bundle=variant is "spectral" or "time" or "processing"?bundle with{Uncertainty=Copy("RFLUNCERT",variant)}:variant=="missing-units"?bundle with{Reflectance=Copy("RFL",variant)}:bundle with{Mask=Copy("MASK",variant)};
        var error=await Assert.ThrowsAsync<EmitProductException>(()=>new EmitProductDecoder(Reader()).OpenAsync(bundle));Assert.Equal(code,error.Code);
    }
    [Fact]public async Task OldElevenBandMaskIsRejectedRatherThanApplyingTheWrongQualityPolicy()
    {
        var bundle=Bundle() with{Mask=Copy("MASK","old-mask")};await Assert.ThrowsAsync<ScientificHdfException>(()=>new EmitProductDecoder(Reader()).OpenAsync(bundle));
    }
    [Fact]public async Task NativeAndGridCompanionSpatialCorrespondenceIsVerifiedForEveryReturnedWindow()
    {
        var decoder=new EmitProductDecoder(Reader());var bundle=Bundle() with{Uncertainty=Copy("RFLUNCERT","shifted")};var p=await decoder.OpenAsync(bundle);
        Assert.Equal("Companion",(await Assert.ThrowsAsync<EmitProductException>(()=>decoder.ReadNativeAsync(p,new(0,0,1,1,0,1)))).Code);
        bundle=Bundle() with{Mask=Copy("MASK","glt")};p=await decoder.OpenAsync(bundle);Assert.Equal("Companion",(await Assert.ThrowsAsync<EmitProductException>(()=>decoder.ReadGeographicAsync(p,new(0,0,1,1,0,1)))).Code);
    }
    [Fact]public async Task UnsupportedFilenameVersionsAndAcquisitionIdentityCannotBeSilentlySelected()
    {
        var b=Bundle();var decoder=new EmitProductDecoder(Reader());
        foreach(var relative in new[]{b.Reflectance.RelativePath.Replace("RFL_002","RFL_001"),b.Reflectance.RelativePath.Replace("RFL_002","RAD_002"),b.Reflectance.RelativePath.Replace("RFL_002","RFL_003")})Assert.Equal("UnsupportedVersion",(await Assert.ThrowsAsync<EmitProductException>(()=>decoder.OpenAsync(b with{Reflectance=b.Reflectance with{RelativePath=relative}}))).Code);
        Assert.Equal("Companion",(await Assert.ThrowsAsync<EmitProductException>(()=>decoder.OpenAsync(b with{Mask=b.Mask with{RelativePath=b.Mask.RelativePath.Replace("20990101T000000","20990101T000001")}}))).Code);
    }
    [Fact]public async Task BoundsCancellationChangedOriginalsAndOfflineReopeningPreserveScientificBytes()
    {
        var decoder=new EmitProductDecoder(Reader());var b=Bundle();var p=await decoder.OpenAsync(b);var names=Directory.GetFiles(root,"*",SearchOption.AllDirectories);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(()=>decoder.ReadNativeAsync(p,new(0,0,1,1,284,2)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(()=>decoder.ReadGeographicAsync(p,new(-1,0,1,1,0,1)));
        using(var cancel=new CancellationTokenSource()){cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>decoder.ReadNativeAsync(p,new(0,0,1,1,0,1),token:cancel.Token));}
        var first=await decoder.ReadNativeAsync(p,new(2,3,1,1,0,5));var restarted=new EmitProductDecoder(Reader());var again=await restarted.ReadNativeAsync(await restarted.OpenAsync(b),new(2,3,1,1,0,5));Assert.Equal(first.Pixels[0].OriginalReflectanceBytes,again.Pixels[0].OriginalReflectanceBytes);Assert.Equal(first.Pixels[0].UsableReflectance.ToArray(),again.Pixels[0].UsableReflectance.ToArray());
        Assert.Equal(names,Directory.GetFiles(root,"*",SearchOption.AllDirectories));Assert.Empty(Directory.GetFiles(root,"*.db",SearchOption.AllDirectories));
        var path=Path.Combine(root,b.Reflectance.RelativePath);var data=File.ReadAllBytes(path);data[^1]^=1;File.WriteAllBytes(path,data);Assert.Equal("Integrity",(await Assert.ThrowsAsync<ScientificHdfException>(()=>decoder.ReadNativeAsync(p,new(0,0,1,1,0,1)))).Code);
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
