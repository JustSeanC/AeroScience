using AeroScience.Engine;
using AeroScience.Missions;
using Xunit;
namespace AeroScience.Tests;

public sealed class OperationFailureTests
{
    [Fact]public void RejectedNasaRedirectIsAProviderFailureAndNeverAnInventedStorageOutage()
    {
        var original=new Uri("https://data.ornldaac.earthdata.nasa.gov/protected/aviris/AVIRIS-NG_L2_Reflectance/data/ang20140621t190550_corr_v1g_img.bin");
        var error=Assert.Throws<IOException>(()=>ScientificHttp.ValidateNasaRedirect(original,new("https://untrusted.example/collect-token")));
        var result=OperationFailure.Describe(error);Assert.Equal(502,result.Status);Assert.Equal("provider_redirect_rejected",result.Code);Assert.Equal("provider",result.Category);Assert.DoesNotContain("Storage unavailable",result.Error);Assert.Contains("No credential was forwarded",result.Error);Assert.DoesNotContain("untrusted.example",result.Error);
    }
    [Fact]public void CapacityPathAuthenticationAndUnknownIoRemainDistinct()
    {
        Assert.Equal("storage_capacity_insufficient",OperationFailure.Describe(new StorageLimitException("Full physical disk")).Code);
        Assert.Equal("selected_resource_unavailable",OperationFailure.Describe(new DirectoryNotFoundException("Reconnect the configured Library drive")).Code);
        Assert.Equal("provider_authorization_required",OperationFailure.Describe(ScientificFailure.Create(ScientificFailureKind.Authorization,"Connect Earthdata")).Code);
        var unknown=OperationFailure.Describe(new IOException("Scientific format cannot be read"));Assert.Equal("operation",unknown.Category);Assert.DoesNotContain("Storage unavailable",unknown.Error);
        var network=OperationFailure.Describe(new HttpRequestException("https://provider.example/?token=private"));Assert.Equal("network",network.Category);Assert.DoesNotContain("private",network.Error);
    }
}
