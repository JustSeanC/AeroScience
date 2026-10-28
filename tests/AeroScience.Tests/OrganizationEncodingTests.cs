using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Organization;
using AeroScience.Shared;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;
namespace AeroScience.Tests;

public sealed partial class OrganizationServerTests
{
    [PostgresFact] public async Task NonUtf8OrganizationDatabaseRefusesStartupBeforeMigrationAndPreservesExistingBytes()
    {
        var name="aeroscience_encoding_"+Guid.NewGuid().ToString("N");var settings=new NpgsqlConnectionStringBuilder(connection);var original="{\"syntheticRetainedRevision\":\"\\u6d4b\"}";
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();await using(var create=new NpgsqlCommand($"CREATE DATABASE \"{name}\" ENCODING 'WIN1252' LC_COLLATE 'C' LC_CTYPE 'C' TEMPLATE template0",admin))await create.ExecuteNonQueryAsync();settings.Database=name;
        try{
            await using var retained=new NpgsqlConnection(settings.ConnectionString);await retained.OpenAsync();await using(var table=new NpgsqlCommand("CREATE TABLE retained_fixture (original text NOT NULL)",retained))await table.ExecuteNonQueryAsync();await using(var put=new NpgsqlCommand("INSERT INTO retained_fixture VALUES (@original)",retained)){put.Parameters.AddWithValue("original",original);await put.ExecuteNonQueryAsync();}
            await using var server=OrganizationApplication.Build([],b=>{b.Environment.EnvironmentName="Testing";b.WebHost.UseTestServer();b.Configuration.AddInMemoryCollection(new Dictionary<string,string?>{["Organization:LocalDevelopment"]="true",["Organization:PublicOrigin"]=Origin,["Organization:ConnectionString"]=settings.ConnectionString,["Organization:BootstrapSecretFile"]=Path.Combine(directory,"bootstrap-secret")});});
            var error=await Assert.ThrowsAsync<OrganizationDatabaseEncodingException>(()=>OrganizationApplication.Initialize(server));Assert.Contains("UTF8",error.Message);await using(var read=new NpgsqlCommand("SELECT original FROM retained_fixture",retained))Assert.Equal(original,await read.ExecuteScalarAsync());await using(var schema=new NpgsqlCommand("SELECT count(*) FROM pg_namespace WHERE nspname='organization'",retained))Assert.Equal(0L,await schema.ExecuteScalarAsync());
        }finally{NpgsqlConnection.ClearAllPools();await using var drop=new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)",admin);await drop.ExecuteNonQueryAsync();}
    }
}

public sealed partial class OrganizationConnectionIntegrationTests
{
    [PostgresFact] public async Task Utf8PublishedProgressPreservesMultilingualNamesQuestionsAndSearchAcrossRestart()
    {
        await using var f=await SubmissionFixture.Create();var original=await RealOpportunity(f.Server,ObservationResearch.Template);var campaign=(await f.Server.Post($"/api/organization/programs/{original.ProgramId}/campaigns",new OrganizationCampaignRequest(1,Guid.NewGuid(),0,"研究キャンペーン","Résumé — 原本","気温は高度とともにどう変わる？",OrganizationFocus.ScientificResearch,new(ObservationResearch.Template,1)))).Deserialize<OrganizationCampaignView>(WireJson.Options)!;campaign=(await f.Server.Post($"/api/organization/campaigns/{campaign.Id}/publish",new OrganizationPublishRequest(1,Guid.NewGuid(),campaign.Revision))).Deserialize<OrganizationCampaignView>(WireJson.Options)!;
        var parent=original with{CampaignId=campaign.Id,CampaignRevision=campaign.Revision};var published=await ProgressCard(f,parent,"研究カード — 測定");var view=await f.Desktop.CampaignProgress(f.Read,campaign.Id,default);Assert.True(view.VerifiedNow);Assert.Equal(campaign.Objective,view.Value!.Objective);var cards=await f.Desktop.CampaignCards(f.Read,campaign.Id,0,"研究","name","",default);Assert.Equal(published.Name,Assert.Single(cards.Value!.Items).Name);await f.Restart();var restored=await ProgressGet<CampaignProgressView>(f,"/campaigns/"+campaign.Id);Assert.Equal(campaign.Name,restored.Name);Assert.Equal(campaign.Objective,restored.Objective);Assert.Equal(published.Name,Assert.Single((await ProgressGet<OrganizationPage<CampaignCardProgress>>(f,"/campaigns/"+campaign.Id+"/cards?search="+Uri.EscapeDataString("研究"))).Items).Name);
    }
}
