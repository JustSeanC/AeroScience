using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Xunit;

namespace AeroScience.Tests;
[Collection(ScientificIoCollection.Name)]
public sealed class ScientificSourceIndexTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-source-index-"+Guid.NewGuid().ToString("N"));
    private readonly ScienceStore science;
    public ScientificSourceIndexTests(){var paths=new StorageManager(new PreferenceStore(root)).Initialize();science=new(paths.ScienceLibraryDirectory);science.Initialize();}
    [Fact]public async Task RealImportsBeyondFiveHundredRemainPagedSearchableDeduplicatedAndReusableAfterRestart()
    {
        var path=RemoteFixtureFiles.Create(Path.Combine(root,"user-original"),RemoteKind.Terrain);
        var manifest=ScienceStore.ReadSourceManifest(path);ScientificSource? first=null;
        for(var i=0;i<505;i++)
        {
            var item=await science.ImportSourceAsync(manifest with{SceneId="synthetic-region-"+i,Name="Synthetic retained terrain "+i},Path.GetDirectoryName(path)!);
            first??=item;
        }
        Assert.Equal(505,science.Sources().Length);Assert.Equal(first!.ManifestSha256,science.Source(first.Id)!.ManifestSha256);
        var matched=science.SourcePage(new(Scene:"synthetic-region-0"),limit:1);Assert.Equal(first.Id,Assert.Single(matched.Items).Id);
        var seen=new HashSet<Guid>();long after=0;long? through=null;
        do{var page=science.SourcePage(after:after,limit:37,through:through);through=page.Through;Assert.Equal(505,page.Total);Assert.InRange(page.Items.Length,1,37);foreach(var source in page.Items)Assert.True(seen.Add(source.Id));if(page.Next is not {} next)break;after=next;}while(true);
        Assert.Equal(505,seen.Count);
        var collections=science.SourceCollections(limit:50);Assert.Equal(505,collections.Total);Assert.Equal(50,collections.Items.Length);Assert.NotNull(collections.Next);Assert.Equal(505,science.SourceCollections(new(Search:"Synthetic retained terrain")).Total);
        var repeated=await science.ImportSourceAsync(first.Manifest,Path.GetDirectoryName(path)!,reuseExisting:true);Assert.Equal(first.Id,repeated.Id);Assert.Equal(505,science.CountSources());
        science.Dispose();using var reopened=new ScienceStore(science.DirectoryPath);reopened.Initialize();
        Assert.Equal(first.Id,reopened.SourcePage(new(Scene:"synthetic-region-0"),limit:1).Items.Single().Id);Assert.Equal(505,reopened.Sources().Length);
        Assert.Contains(reopened.Availability(new(40,-75),RemoteKind.Terrain),a=>a.Source.Id==first.Id&&a.FilesAvailable);
        using var db=new SqliteConnection("Data Source="+science.DatabasePath+";Mode=ReadOnly;Pooling=False");db.Open();using var command=db.CreateCommand();command.CommandText="PRAGMA integrity_check";Assert.Equal("ok",command.ExecuteScalar());
        var missions=new MissionStore(Path.Combine(new PreferenceStore(root).Load().MissionDirectory,"missions.db"));missions.Initialize();var coordinator=new MissionCoordinator(missions,1000);
        var deletion=reopened.PreviewSourceDeletion(new(All:true),missions);Assert.Equal(505,deletion.TotalSources);Assert.Equal(20,deletion.Sources.Length);Assert.NotNull(deletion.Next);Assert.Equal(0,deletion.BlockedSources);
        var result=reopened.DeleteExternalSources(new(deletion.Id,deletion.SelectionHash),missions,coordinator.EnterMaintenance);Assert.True(result.State=="Completed",result.Warning);Assert.Equal(505,result.RemovedSources);Assert.Equal(0,reopened.CountSources());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(reopened.DirectoryPath,"Sources"),"*.tif",SearchOption.AllDirectories));
    }
    [Fact]public async Task TenThousandCatalogRegionsUseSpatialIndexInsteadOfHydratingTheWholeLibrary()
    {
        var source=await science.ImportSourceAsync(RemoteFixtureFiles.Create(Path.Combine(root,"user-original"),RemoteKind.Terrain));
        science.Dispose();
        // Synthetic catalog-scale fixture, not genuine retained files/scientific acceptance.
        // Only the real target owns local files; unrelated records must never be opened.
        using(var db=new SqliteConnection("Data Source="+science.DatabasePath+";Pooling=False"))
        {
            db.Open();using var tx=db.BeginTransaction();using var command=db.CreateCommand();command.Transaction=tx;
            command.CommandText="DROP TABLE science_delete_files; DROP TABLE science_delete_missions; DROP TABLE science_delete_reviews; DROP INDEX remote_products_source; DROP TABLE source_delete_items; DROP TABLE source_delete_ledgers; DROP TABLE source_delete_previews; DROP TABLE source_references; DROP TABLE source_search; DROP TABLE source_bounds; PRAGMA user_version=3";command.ExecuteNonQuery();
            command.CommandText="INSERT INTO remote_sources VALUES($id,$sha,$json)";command.Parameters.Add("$id",SqliteType.Text);command.Parameters.Add("$sha",SqliteType.Text);command.Parameters.Add("$json",SqliteType.Text);
            var grid=source.Grids.Values.First() with{OriginX=0,OriginY=10};
            for(var i=0;i<10000;i++)
            {
                var id=Guid.NewGuid();var item=source with{Id=id,Manifest=source.Manifest with{SceneId="synthetic-unrelated-"+i},RelativeDirectory="Sources/"+id.ToString("N"),Grids=source.Grids.ToDictionary(p=>p.Key,_=>grid),ManifestSha256=i.ToString("x64")};
                command.Parameters["$id"].Value=id.ToString();command.Parameters["$sha"].Value=item.ManifestSha256;command.Parameters["$json"].Value=JsonSerializer.Serialize(item,WireJson.Options);command.ExecuteNonQuery();
            }
            tx.Commit();
        }
        using var reopened=new ScienceStore(science.DirectoryPath);reopened.Initialize();Assert.Equal(10001,reopened.CountSources());
        var page=reopened.SourcePage(new(RemoteKind.Terrain,new(40,-75)),limit:10);Assert.Equal(source.Id,Assert.Single(page.Items).Id);Assert.Equal(1,page.Total);
        Assert.Equal(source.Id,Assert.Single(reopened.Availability(new(40,-75),RemoteKind.Terrain)).Source.Id);
        var next=reopened.SourcePage(limit:23);Assert.Equal(10001,next.Total);Assert.Equal(23,next.Items.Length);Assert.NotNull(next.Next);
        using var verify=new SqliteConnection("Data Source="+science.DatabasePath+";Mode=ReadOnly;Pooling=False");verify.Open();using var query=verify.CreateCommand();
        query.CommandText="EXPLAIN QUERY PLAN SELECT seq FROM source_bounds WHERE west<=-75 AND east>=-75 AND south<=40 AND north>=40";
        using var rows=query.ExecuteReader();var plan=new List<string>();while(rows.Read())plan.Add(rows.GetString(3));Assert.Contains(plan,line=>line.Contains("VIRTUAL TABLE INDEX",StringComparison.Ordinal));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(science.DirectoryPath,"Metadata","Backups")));
    }
    [Fact]public async Task MigrationFailureRollsBackWithoutChangingOriginalSourceMetadata()
    {
        var source=await science.ImportSourceAsync(RemoteFixtureFiles.Create(Path.Combine(root,"user-original"),RemoteKind.Terrain));science.Dispose();
        using(var db=new SqliteConnection("Data Source="+science.DatabasePath+";Pooling=False")){db.Open();using var c=db.CreateCommand();c.CommandText="DROP TABLE science_delete_files; DROP TABLE science_delete_missions; DROP TABLE science_delete_reviews; DROP INDEX remote_products_source; DROP TABLE source_delete_items; DROP TABLE source_delete_ledgers; DROP TABLE source_delete_previews; DROP TABLE source_references; DROP TABLE source_search; DROP TABLE source_bounds; CREATE TABLE source_search(sentinel TEXT); PRAGMA user_version=3";c.ExecuteNonQuery();}
        using var reopened=new ScienceStore(science.DirectoryPath);Assert.Throws<SqliteException>(()=>reopened.Initialize());
        using var verify=new SqliteConnection("Data Source="+science.DatabasePath+";Mode=ReadOnly;Pooling=False");verify.Open();using var command=verify.CreateCommand();command.CommandText="PRAGMA user_version";Assert.Equal(3L,command.ExecuteScalar());
        command.CommandText="SELECT payload_json FROM remote_sources WHERE id=$id";command.Parameters.AddWithValue("$id",source.Id.ToString());Assert.Equal(JsonSerializer.Serialize(source,WireJson.Options),(string)command.ExecuteScalar()!);
        foreach(var file in source.Files)Assert.True(File.Exists(Path.Combine(science.DirectoryPath,source.RelativeDirectory,file.RelativePath)));
    }
    public void Dispose(){science.Dispose();SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
}
