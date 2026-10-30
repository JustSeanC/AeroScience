using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroScience.Organization;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AeroScience.Tests;

public sealed partial class OrganizationServerTests
{
    sealed class UnavailableHealthStorage(string table):DbCommandInterceptor
    {
        public int Failures, ReadsAfterFailure;
        public bool Armed;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken ct=default)
        {
            if(!Armed)return ValueTask.FromResult(result);
            if(Failures>0)ReadsAfterFailure++;
            if(command.CommandText.Contains($"FROM organization.\"{table}\"",StringComparison.Ordinal)){
                Failures++;
                throw new IOException("Private database host, credential and internal path must never reach the browser.");
            }
            return ValueTask.FromResult(result);
        }
    }

    [PostgresFact]public async Task HealthReportsAnUnavailableRecoveryReadWithoutReturningAnErrorPageOrPrivateException()
    {
        var fault=new UnavailableHealthStorage("ServerBackups");operationStatusInterceptor=fault;
        var account=await SecureAdministrator();await Value<JsonElement>(await browser.Send("/api/account-security/confirm-recovery",new AccountRecoveryConfirmation(account.Codes[0])));fault.Armed=true;
        using var response=await browser.Client.GetAsync("/api/organization/server-health");
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var health=(await response.Content.ReadFromJsonAsync<OrganizationServerHealth>())!;
        Assert.Equal("Deployment Blocked",health.DeploymentState);Assert.False(health.ProductionReady);
        Assert.Contains(health.Checks,c=>c.Name=="Storage check"&&c.State=="blocked");
        Assert.Equal(1,fault.Failures);Assert.Equal(0,fault.ReadsAfterFailure);
        var json=JsonSerializer.Serialize(health);Assert.DoesNotContain("Private database host",json);Assert.DoesNotContain(directory,json);Assert.DoesNotContain(connection,json);Assert.DoesNotContain(secret,json);
        fault.Armed=false;
        var recovered=(await browser.Client.GetFromJsonAsync<OrganizationServerHealth>("/api/organization/server-health"))!;
        Assert.Equal("Deployment Healthy",recovered.DeploymentState);Assert.Contains(recovered.Checks,c=>c.Name=="Backups & recovery"&&c.State=="release-gate");
    }

    [PostgresFact]public async Task HealthDoesNotQueryRecoveryOrAdministratorTablesAfterAnEarlierStorageFailure()
    {
        var fault=new UnavailableHealthStorage("DataProtectionKeys");operationStatusInterceptor=fault;
        var account=await SecureAdministrator();await Value<JsonElement>(await browser.Send("/api/account-security/confirm-recovery",new AccountRecoveryConfirmation(account.Codes[0])));fault.Armed=true;
        var health=(await browser.Client.GetFromJsonAsync<OrganizationServerHealth>("/api/organization/server-health"))!;
        Assert.Equal("Deployment Blocked",health.DeploymentState);Assert.False(health.ProductionReady);
        Assert.Contains(health.Checks,c=>c.Name=="Storage check"&&c.State=="blocked");
        Assert.Equal(1,fault.Failures);Assert.Equal(0,fault.ReadsAfterFailure);
        Assert.DoesNotContain(health.Checks,c=>c.Name=="Administrator security"&&c.State=="healthy");
    }

    [PostgresFact]public async Task HealthRespectsCallerCancellationInsteadOfReturningAnInventedHealthyResult()
    {
        using var scope=app.Services.CreateScope();using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>scope.ServiceProvider.GetRequiredService<OrganizationServerDiagnostics>().Check(cancelled.Token));
    }
}
