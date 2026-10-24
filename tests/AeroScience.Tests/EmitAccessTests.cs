using System.Net;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;
namespace AeroScience.Tests;
public sealed class EmitAccessTests
{
    private const string Root="https://data.lpdaac.earthdatacloud.nasa.gov/lp-prod-protected/";
    private const string Scene="20220810T034103";
    private const string File="EMITL2ARFL.002/EMIT_L2A_RFL_002_"+Scene+"/EMIT_L2A_RFL_002_"+Scene+".nc";
    [Theory][InlineData("RFL")][InlineData("RFLUNCERT")][InlineData("MASK")]
    public void OnlyExactOriginalFamilyVersionAcquisitionAndUnsignedOfficialUrlsAreAccepted(string role)
    {
        var product=role=="MASK"?"MASK":"RFL";var version=role=="MASK"?"003":"002";
        var url=Root+$"EMITL2A{product}.{version}/EMIT_L2A_{product}_{version}_{Scene}/EMIT_L2A_{role}_{version}_{Scene}.nc";
        ScientificHttp.ValidateNasaOriginal(new(url));
        foreach(var bad in new[]{url+"?token=secret",url.Replace("_"+Scene+".nc","_20220811T034103.nc"),url.Replace(".nc",".png"),url.Replace("earthdatacloud.nasa.gov","earthdatacloud.nasa.gov.evil.example"),url.Replace("EMITL2A"+product+"."+version,"EMITL2A"+product+".001")})Assert.Throws<IOException>(()=>ScientificHttp.ValidateNasaOriginal(new(bad)));
    }
    private sealed class Credential:IEarthdataCredentials{public bool Available=>true;public string? ReadToken()=>"synthetic_earthdata_not_a_real_token";public void Clear(){} }
    private sealed class RedirectHandler(string url):HttpMessageHandler
    {public int Calls;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){Calls++;Assert.Equal("data.lpdaac.earthdatacloud.nasa.gov",request.RequestUri!.Host);Assert.Equal("synthetic_earthdata_not_a_real_token",request.Headers.Authorization?.Parameter);var r=new HttpResponseMessage(HttpStatusCode.Found);r.Headers.Location=new(url);return Task.FromResult(r);}}
    [Theory][InlineData("https://urs.earthdata.nasa.gov/oauth/authorize",ScientificFailureKind.Authorization)][InlineData("https://unverified.example/asset?signature=private",ScientificFailureKind.ProviderRedirect)]
    public async Task UnverifiedRedirectIsNeverFollowedAndNoCredentialIsForwarded(string destination,ScientificFailureKind kind)
    {
        using var handler=new RedirectHandler(destination);using var http=new ScientificHttp(handler,cacheEnabled:false);var error=await Assert.ThrowsAsync<IOException>(()=>http.SendNasa(new(Root+File),HttpMethod.Get,new Credential(),null,null,null,default));Assert.Equal(kind,ScientificFailure.Kind(error));Assert.Equal(1,handler.Calls);Assert.DoesNotContain("signature=private",error.Message);Assert.DoesNotContain("synthetic_earthdata",error.Message);
    }
    [Fact]public void LegacySourceManifestsSerializeWithoutAddingNullSwathOrChangingHistoricalHashInput()
    {
        var manifest=new ScientificSourceManifest(1,"Legacy",RemoteKind.Terrain,"USGS","public","source","scene",null,"NAVD88",[new("elevation","a.tif","geotiff",Unit:"m")]);
        Assert.DoesNotContain("swath",System.Text.Json.JsonSerializer.Serialize(manifest,WireJson.Options));
    }
}
