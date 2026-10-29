using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AeroScience.Organization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AeroScience.Tests;
public sealed class OrganizationDeploymentConfigurationTests
{
    sealed class HostingEnvironment:IHostEnvironment
    {
        public string EnvironmentName{get;set;}="Production";public string ApplicationName{get;set;}="Deployment tests";public string ContentRootPath{get;set;}="";public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider{get;set;}=new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
    static Dictionary<string,string?> Values()=>new(){
        ["Organization:PublicOrigin"]="https://research.example.org",["Organization:ConnectionString"]="Host=database.example.org;Database=research;Username=research;SSL Mode=VerifyFull",
        ["Organization:ManagedDeployment"]="true",["Organization:StateDirectory"]=Path.Combine(Path.GetTempPath(),"protected-configuration"),["Organization:PersistentStorageConfirmed"]="true",
        ["Organization:BootstrapSecret"]=new string('b',48),["Organization:KeyCertificatePassword"]=new string('k',48),["Organization:DatabaseExposure"]="private-network"
    };
    static OrganizationSettings Read(Dictionary<string,string?> settings)=>OrganizationSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),new HostingEnvironment());
    [Theory]
    [InlineData("Organization:PublicOrigin","http://research.example.org")]
    [InlineData("Organization:PublicOrigin","https://research.example.org/path")]
    [InlineData("Organization:ConnectionString","Host=database;Database=research;Username=test;SSL Mode=Require")]
    [InlineData("Organization:PersistentStorageConfirmed","false")]
    [InlineData("Organization:StateDirectory","relative")]
    [InlineData("Organization:BootstrapSecret","sample")]
    [InlineData("Organization:KeyCertificatePassword","sample")]
    [InlineData("Organization:DatabaseExposure","public")]
    [InlineData("Organization:BootstrapLifetimeMinutes","61")]
    [InlineData("Organization:BootstrapLifetimeMinutes","4")]
    [InlineData("ASPNETCORE_FORWARDEDHEADERS_ENABLED","true")]
    [InlineData("Organization:TrustedProxies:0","*")]
    [InlineData("Organization:HostingProvider","imaginary")]
    public void UnsafeManagedConfigurationCannotStart(string key,string value){var settings=Values();settings[key]=value;Assert.Throws<InvalidOperationException>(()=>Read(settings));}
    [Fact]public void OwnerCodeAndCertificateProtectionAreIndependentAndNeverDefaultSecrets(){var settings=Values();Assert.True(Read(settings).ManagedDeployment);settings["Organization:KeyCertificatePassword"]=settings["Organization:BootstrapSecret"];Assert.Throws<InvalidOperationException>(()=>Read(settings));}
    [Fact]public void OwnerSecretExpiresWithoutChangingPasswordOrLockoutRules(){using var credential=new BootstrapCredential();credential.Initialize(Encoding.UTF8.GetBytes(new string('s',48)),DateTimeOffset.UtcNow.AddSeconds(-1));Assert.False(credential.Matches(new string('s',48)));}
}
public sealed partial class OrganizationServerTests
{
    OrganizationSettings ManagedFixture(string ownerCode)
    {
        var path=Path.Combine(directory,"deployment-fixture.pfx");
        if(!File.Exists(path)){using var key=RSA.Create(2048);var request=new CertificateRequest("CN=Synthetic deployment test",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);using var certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddDays(1));File.WriteAllBytes(path,certificate.Export(X509ContentType.Pfx,"Fixture-Key-Password!"));OrganizationConfigurationTests.ProtectCredential(path);}
        return app.Services.GetRequiredService<OrganizationSettings>() with{ManagedDeployment=true,KeyCertificateFile=path,KeyCertificatePassword="Fixture-Key-Password!",BootstrapSecret=ownerCode};
    }
    [PostgresFact]public async Task ManagedOwnerWindowSurvivesRestartCannotBeRenewedByTheSameCodeAndConsumesOnce()
    {
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();var settings=ManagedFixture(new string('o',48));using var credential=new BootstrapCredential();await OrganizationDeployment.Initialize(db,settings,credential,default);var expires=(await db.DeploymentState.SingleAsync()).BootstrapExpiresUtc;
        await OrganizationDeployment.Initialize(db,settings,credential,default);Assert.Equal(expires,(await db.DeploymentState.SingleAsync()).BootstrapExpiresUtc);Assert.True(credential.Matches(settings.BootstrapSecret));
        await using(var tx=await db.Database.BeginTransactionAsync()){await OrganizationDeployment.ConsumeBootstrap(db,settings,settings.BootstrapSecret!,default);await db.SaveChangesAsync();await tx.CommitAsync();}
        await Assert.ThrowsAsync<OrganizationForbidden>(()=>OrganizationDeployment.ConsumeBootstrap(db,settings,settings.BootstrapSecret!,default));
        await OrganizationDeployment.Initialize(db,settings,credential,default);Assert.NotNull((await db.DeploymentState.SingleAsync()).BootstrapConsumedUtc);
    }
    [PostgresFact]public async Task ExpiredOwnerCodeRequiresExplicitOwnerRotationAndChangedPersistentCertificateIsRefused()
    {
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();var settings=ManagedFixture(new string('o',48));using var credential=new BootstrapCredential();await OrganizationDeployment.Initialize(db,settings,credential,default);var state=await db.DeploymentState.SingleAsync();state.BootstrapExpiresUtc=DateTimeOffset.UtcNow.AddMinutes(-1);await db.SaveChangesAsync();await OrganizationDeployment.Initialize(db,settings,credential,default);Assert.False(credential.Matches(settings.BootstrapSecret));await Assert.ThrowsAsync<OrganizationForbidden>(()=>OrganizationDeployment.ConsumeBootstrap(db,settings,settings.BootstrapSecret!,default));
        var rotated=settings with{BootstrapSecret=new string('n',48)};await OrganizationDeployment.Initialize(db,rotated,credential,default);Assert.True(credential.Matches(rotated.BootstrapSecret));Assert.False(credential.Matches(settings.BootstrapSecret));
        state.CertificateSha256=new string('0',64);await db.SaveChangesAsync();await Assert.ThrowsAsync<InvalidOperationException>(()=>OrganizationDeployment.Initialize(db,rotated,credential,default));
    }
    [PostgresFact]public void MissingPersistentCertificateCannotBeRegeneratedOverAnExistingOrganizationSchema()
    {
        var state=Path.Combine(directory,"missing-key-state");Directory.CreateDirectory(state);if(!OperatingSystem.IsWindows())File.SetUnixFileMode(state,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        var settings=app.Services.GetRequiredService<OrganizationSettings>() with{ManagedDeployment=true,StateDirectory=state,KeyCertificateFile=Path.Combine(state,"data-protection.pfx"),KeyCertificatePassword="Fixture-Not-For-Production!"};
        Assert.Throws<InvalidOperationException>(()=>OrganizationDeployment.PrepareCertificate(settings));Assert.False(File.Exists(settings.KeyCertificateFile));
    }
    [PostgresFact]public async Task AdministratorHealthIsRealBoundedAndDoesNotExposePrivateConfiguration()
    {
        Assert.Equal(HttpStatusCode.Unauthorized,(await browser.Client.GetAsync("/api/organization/server-health")).StatusCode);await Bootstrap();await Login();var response=await browser.Client.GetAsync("/api/organization/server-health");response.EnsureSuccessStatusCode();var health=(await response.Content.ReadFromJsonAsync<OrganizationServerHealth>())!;
        Assert.False(health.ProductionReady);Assert.Equal(Origin,health.PublicOrigin);Assert.Contains(health.Checks,c=>c.Name=="Database & schema"&&c.State=="healthy");Assert.Contains(health.Checks,c=>c.Name=="Received evidence"&&c.State=="healthy");Assert.Contains(health.Checks,c=>c.Name=="Backups & recovery"&&c.State=="release-gate");var json=JsonSerializer.Serialize(health);Assert.DoesNotContain(connection,json);Assert.DoesNotContain(directory,json);Assert.DoesNotContain(secret,json);Assert.DoesNotContain(Password,json);
        var account=await JoinPilot("health-pilot",InvitationSecret(await Invite()));Assert.Equal(HttpStatusCode.Forbidden,(await account.Client.GetAsync("/api/organization/server-health")).StatusCode);account.Dispose();
    }
}
