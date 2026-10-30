using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Organization;
using AeroScience.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
namespace AeroScience.Tests;

public sealed class PairedUpdateFactAttribute:FactAttribute
{
    public PairedUpdateFactAttribute(){if(string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("AEROSCIENCE_UPDATE_FIXTURE"))||string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("AEROSCIENCE_RECOVERY_RESTIC"))||string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("AEROSCIENCE_ORG_TEST_DATABASE")))Skip="Isolated old/new update binaries and real PostgreSQL/native recovery tools are not configured. No simulated update acceptance.";}
}
public sealed partial class OrganizationConnectionIntegrationTests
{
    sealed class UpdateServerProcess: IAsyncDisposable
    {
        readonly Process process;public readonly HttpClient Client;readonly string origin;
        UpdateServerProcess(Process process,string origin,CookieContainer cookies){this.process=process;this.origin=origin;Client=new(new HttpClientHandler{UseCookies=true,CookieContainer=cookies,AllowAutoRedirect=false,UseProxy=false}){BaseAddress=new(origin),Timeout=TimeSpan.FromSeconds(5)};}
        public static ProcessStartInfo StartInfo(string binary,Dictionary<string,string> environment,string? argument=null)
        {
            var start=new ProcessStartInfo("dotnet"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add(binary);if(argument is not null)start.ArgumentList.Add(argument);foreach(var pair in environment)start.Environment[pair.Key]=pair.Value;return start;
        }
        public static async Task<UpdateServerProcess> Start(string binary,string origin,Dictionary<string,string> environment,CookieContainer cookies)
        {
            var p=Process.Start(StartInfo(binary,environment))!;p.OutputDataReceived+=(_,_)=>{};p.ErrorDataReceived+=(_,_)=>{};p.BeginOutputReadLine();p.BeginErrorReadLine();var server=new UpdateServerProcess(p,origin,cookies);
            try{for(var attempt=0;attempt<100;attempt++){if(p.HasExited)throw new InvalidOperationException("The actual isolated update server refused startup.");try{if((await server.Client.GetAsync("/api/health")).IsSuccessStatusCode)return server;}catch(HttpRequestException){}await Task.Delay(50);}throw new InvalidOperationException("The actual isolated update server did not become healthy within its existing startup bound.");}
            catch{await server.DisposeAsync();throw;}
        }
        public async Task<JsonElement> Post(string path,object value)
        {
            var session=await Client.GetFromJsonAsync<JsonElement>("/api/session");using var request=new HttpRequestMessage(HttpMethod.Post,path){Content=JsonContent.Create(value,options:WireJson.Options)};request.Headers.Add("Origin",origin);request.Headers.Add("X-AeroScience-Organization-CSRF",session.GetProperty("csrf").GetString());using var response=await Client.SendAsync(request);Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        public async ValueTask DisposeAsync(){Client.Dispose();if(!process.HasExited)process.Kill(entireProcessTree:true);await process.WaitForExitAsync();process.Dispose();}
    }
    static async Task<int> ApplyUpdateBinary(string binary,Dictionary<string,string> environment,Guid operation)
    {
        var values=new Dictionary<string,string>(environment){["Organization__Operations__ApplyOperationId"]=operation.ToString("D")};using var process=Process.Start(UpdateServerProcess.StartInfo(binary,values,"--apply-prepared-update"))!;var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();using var bound=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try{await process.WaitForExitAsync(bound.Token);await Task.WhenAll(output,errors);return process.ExitCode;}finally{if(!process.HasExited)process.Kill(true);}
    }
    [PairedUpdateFact]public async Task ActualOldAndNewProcessesRefuseStaleCheckpointRollbackFailedUpgradeAndApplyExactApprovedPair()
    {
        var fixtures=System.Environment.GetEnvironmentVariable("AEROSCIENCE_UPDATE_FIXTURE")!;var oldBinary=Path.Combine(fixtures,"old","AeroScience.Organization.dll");var newBinary=Path.Combine(fixtures,"new","AeroScience.Organization.dll");using var metadata=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixtures,"fixture.json")));var targetSource=metadata.RootElement.GetProperty("newSource").GetString()!;
        await using var f=await SubmissionFixture.Create(protectedIdentity:true);var card=await f.Card(ObservationResearch.Template);var attempt=await f.Record(card.Prepared,2,notes:true);var approved=await f.ReviewApprove(f.Draft(attempt));using var delivery=f.Delivery();Assert.Equal(SubmissionDeliveryState.ServerReceived,(await delivery.Send(approved.Id,default)).State);
        var enroll=await f.Server.Post("/api/account-security/enroll",new AccountEnrollment("ResearchOnly!2026Flight"));var codes=await f.Server.Post("/api/account-security/enable",new AccountCode(OrganizationServerTests.AuthenticatorCode(enroll.GetProperty("setupKey").GetString()!,DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30+1)));await f.Server.Post("/api/account-security/confirm-recovery",new AccountRecoveryConfirmation(codes.GetProperty("recoveryCodes")[0].GetString()!));
        var connection=f.Server.App.Services.GetRequiredService<OrganizationSettings>().ConnectionString;var origin=f.Server.Origin;string[] sourceSchema;
        var oldMigrations=Directory.GetFiles(Path.Combine(fixtures,"old-source","src","AeroScience.Organization","Migrations"),"*.cs").Select(Path.GetFileNameWithoutExtension).Where(n=>n is not null&&n.Length>15&&char.IsAsciiDigit(n[0])&&!n.EndsWith(".Designer",StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        await f.Server.App.StopAsync();using(var scope=f.Server.App.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();await db.GetService<IMigrator>().MigrateAsync(oldMigrations[^1]);sourceSchema=(await db.Database.GetAppliedMigrationsAsync()).ToArray();}await f.Server.DisposeAsync();f.Server=null!;
        var primary=Path.Combine(Path.GetTempPath(),"aeroscience-update-primary-"+Guid.NewGuid().ToString("N"));var secondary=Path.Combine(Path.GetTempPath(),"aeroscience-update-secondary-"+Guid.NewGuid().ToString("N"));var passwordFile=Path.Combine(f.Root,"update-primary.key");var separateFile=Path.Combine(f.Root,"update-independent.key");var manifestFile=Path.Combine(f.Root,"signed-update.json");var publicFile=Path.Combine(f.Root,"publisher.pub");using var publisher=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(passwordFile,Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));File.WriteAllText(separateFile,Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));File.WriteAllText(publicFile,publisher.ExportSubjectPublicKeyInfoPem());
        var distribution=new ServerDistribution(1,"0.7.1",targetSource,"ghcr.io/justseanc/aeroscience-organization@sha256:"+new string('b',64),"0.7.0",OrganizationSchemaUpgrade.SchemaHash(sourceSchema),true);File.WriteAllText(manifestFile,JsonSerializer.Serialize(new SignedServerDistribution(distribution,Convert.ToBase64String(publisher.SignData(JsonSerializer.SerializeToUtf8Bytes(distribution,WireJson.Options),HashAlgorithmName.SHA256))),WireJson.Options));foreach(var path in new[]{passwordFile,separateFile,publicFile,manifestFile})OrganizationConfigurationTests.ProtectCredential(path);
        var environment=new Dictionary<string,string>{["ASPNETCORE_ENVIRONMENT"]="Testing",["Organization__LocalDevelopment"]="true",["Organization__RequireAdministratorMfa"]="true",["Organization__PublicOrigin"]=origin,["Organization__ConnectionString"]=connection,["Organization__KeyCertificateFile"]=Path.Combine(f.Root,"recovery-identity.pfx"),["Organization__KeyCertificatePassword"]="P26-Only!Identity-Certificate-Key",["Organization__Operations__Enabled"]="true",["Organization__Operations__Repository"]=primary,["Organization__Operations__PasswordFile"]=passwordFile,["Organization__Operations__Independent__Repository"]=secondary,["Organization__Operations__Independent__PasswordFile"]=separateFile,["Organization__Operations__WorkDirectory"]=Path.Combine(f.Root,"update-work"),["Organization__Operations__ResticExecutable"]=System.Environment.GetEnvironmentVariable("AEROSCIENCE_RECOVERY_RESTIC")!,["Organization__Operations__PgDumpExecutable"]=System.Environment.GetEnvironmentVariable("AEROSCIENCE_RECOVERY_PG_DUMP")!,["Organization__Operations__PgRestoreExecutable"]=System.Environment.GetEnvironmentVariable("AEROSCIENCE_RECOVERY_PG_RESTORE")!,["Organization__Operations__RecoveryConnectionString"]=System.Environment.GetEnvironmentVariable("AEROSCIENCE_ORG_TEST_DATABASE")!,["Organization__Operations__DistributionFile"]=manifestFile,["Organization__Operations__DistributionPublicKeyFile"]=publicFile,["Logging__LogLevel__Default"]="Warning"};
        UpdateServerProcess? server=null;var cookies=new CookieContainer();
        async Task<Guid> Prepare(){var job=await server!.Post("/api/organization/operations/commands",new ServerOperationCommand(Guid.NewGuid(),ServerOperationKind.PrepareUpdate,null,"ResearchOnly!2026Flight",true));var id=job.GetProperty("id").GetGuid();for(var retry=0;retry<150;retry++){var view=await server.Client.GetFromJsonAsync<ServerOperationsView>("/api/organization/operations",WireJson.Options);var result=view!.Operations.Single(x=>x.Id==id);if(result.State==ServerOperationState.Completed)return id;Assert.NotEqual(ServerOperationState.Failed,result.State);await Task.Delay(150);}throw new InvalidOperationException("The real paired update checkpoint did not complete within the bounded fixture interval.");}
        try{
            server=await UpdateServerProcess.Start(oldBinary,origin,environment,cookies);await server.Post("/api/sign-in",new OrganizationLoginRequest("administrator","ResearchOnly!2026Flight"));await server.Post("/api/sign-in/second-factor",new AccountCode(codes.GetProperty("recoveryCodes")[1].GetString()!,true));await server.Post("/api/organization/operations/policy",new ServerOperationsPolicyCommand(Guid.NewGuid(),0,false,24,14,8,true,"ResearchOnly!2026Flight"));
            var stale=await Prepare();await server.Post("/api/organization/programs",new OrganizationProgramRequest(1,Guid.NewGuid(),0,"Legitimate change after checkpoint","This must invalidate the prepared update."));await server.DisposeAsync();server=null;Assert.NotEqual(0,await ApplyUpdateBinary(newBinary,environment,stale));
            server=await UpdateServerProcess.Start(oldBinary,origin,environment,cookies);var exact=await Prepare();await server.DisposeAsync();server=null;
            using var db=new OrganizationDatabase(new DbContextOptionsBuilder<OrganizationDatabase>().UseNpgsql(connection).Options);await using var raw=new NpgsqlConnection(connection);await raw.OpenAsync();var original=await OrganizationRecoveryEngine.Digests(raw,null,default);
            await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION public.refuse_process_upgrade() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'deliberate isolated process upgrade failure'; END $$; CREATE TRIGGER refuse_process_upgrade BEFORE INSERT ON public.\"__EFMigrationsHistory\" FOR EACH ROW EXECUTE FUNCTION public.refuse_process_upgrade()");
            try{Assert.NotEqual(0,await ApplyUpdateBinary(newBinary,environment,exact));Assert.Equal(sourceSchema,(await db.Database.GetAppliedMigrationsAsync()).ToArray());Assert.Equal(original,await OrganizationRecoveryEngine.Digests(raw,null,default));Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('organization.\"OwnerSetups\"') IS NOT NULL AS \"Value\"").SingleAsync());}
            finally{await db.Database.ExecuteSqlRawAsync("DROP TRIGGER refuse_process_upgrade ON public.\"__EFMigrationsHistory\"; DROP FUNCTION public.refuse_process_upgrade()");}
            // A failed transactional migration really permits the original binary to run.
            server=await UpdateServerProcess.Start(oldBinary,origin,environment,cookies);Assert.Equal(1,await db.Submissions.CountAsync());await server.DisposeAsync();server=null;
            Assert.Equal(0,await ApplyUpdateBinary(newBinary,environment,exact));Assert.Equal(db.Database.GetMigrations().ToArray(),(await db.Database.GetAppliedMigrationsAsync()).ToArray());Assert.Equal(0,await ApplyUpdateBinary(newBinary,environment,exact));Assert.Equal(1,await db.Audit.CountAsync(a=>a.Action=="server-schema-update-applied"&&a.ResourceId==exact));
            server=await UpdateServerProcess.Start(newBinary,origin,environment,cookies);var health=await server.Client.GetFromJsonAsync<OrganizationServerHealth>("/api/organization/server-health");Assert.Equal("0.7.1+"+targetSource,health!.Version);Assert.Equal(1,await db.Submissions.CountAsync());Assert.True((await db.Users.SingleAsync(u=>u.TwoFactorEnabled)).TwoFactorEnabled);
        }finally{if(server is not null)await server.DisposeAsync();if(Directory.Exists(primary))Directory.Delete(primary,true);if(Directory.Exists(secondary))Directory.Delete(secondary,true);}
    }
}
