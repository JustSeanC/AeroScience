using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

public sealed record ManagedCache(Guid Id, Guid ProductId, Guid MissionId, Guid DatasetId, string RelativePath,
    long Bytes, string Sha256, string ProductSha256, DateTimeOffset CreatedUtc, DateTimeOffset LastUsedUtc,
    string Recipe = "remote-preview-v1");
public sealed record CleanupItem(ManagedCache Cache, string Category, string MissionName, string Status, string Reason, double AgeDays);
public sealed record CleanupPreview(Guid Id, DateTimeOffset CreatedUtc, CleanupItem[] Items, long EligibleFiles, long EstimatedBytes,
    DateTimeOffset? OldestUtc, DateTimeOffset? NewestUtc, long CurrentUsage, string Policy = "Regenerable caches only; originals, completed products, backups, settings and unrelated files retained");
public sealed record CleanupResult(Guid Id, DateTimeOffset CompletedUtc, int Removed, int Skipped, int Failed,
    long ActualBytesRemoved, long? DiskSpaceReclaimed, string[] Messages);

public sealed partial class ScienceStore
{
    public StorageManagement? Management { get; set; }
    public Func<Guid,bool>? MissionIsProtected { get; set; }
    private void EnsureCacheTable(Microsoft.Data.Sqlite.SqliteConnection db)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "CREATE TABLE IF NOT EXISTS remote_cache(id TEXT PRIMARY KEY,product_id TEXT NOT NULL REFERENCES remote_products(id) ON DELETE CASCADE,relative_path TEXT NOT NULL UNIQUE,payload_json TEXT NOT NULL)"; cmd.ExecuteNonQuery();
    }
    public ManagedCache[] CacheEntries()
    {
        if (AvailabilityError() is { } error) throw new IOException(error);
        if (!File.Exists(DatabasePath)) return [];
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT name FROM sqlite_master WHERE name='remote_cache'";if(cmd.ExecuteScalar() is null)return [];
        cmd.CommandText="SELECT payload_json FROM remote_cache ORDER BY rowid LIMIT 10000";using var rows=cmd.ExecuteReader();var found=new List<ManagedCache>();
        while(rows.Read())found.Add(ReadCatalog<ManagedCache>(rows.GetString(0)));return found.ToArray();
    }
    public void RegisterCache(ManagedCache cache)
    {
        Initialize();
        using var db=Open(write:true);using var cmd=db.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM remote_cache";if(Convert.ToInt64(cmd.ExecuteScalar())>=10000)throw new IOException("Cache registry is full; previews remain available without caching.");cmd.CommandText="INSERT INTO remote_cache VALUES($id,$product,$path,$json)";
        cmd.Parameters.AddWithValue("$id",cache.Id.ToString());cmd.Parameters.AddWithValue("$product",cache.ProductId.ToString());cmd.Parameters.AddWithValue("$path",cache.RelativePath);cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(cache,WireJson.Options));cmd.ExecuteNonQuery();
    }
    public void ForgetCache(Guid id)
    {Initialize();using var db=Open(write:true);using var cmd=db.CreateCommand();cmd.CommandText="DELETE FROM remote_cache WHERE id=$id";cmd.Parameters.AddWithValue("$id",id.ToString());cmd.ExecuteNonQuery();}
    public bool DatasetProtected(Guid id)
    {if(!File.Exists(DatabasePath))return false;using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT value FROM metadata WHERE key=$key";cmd.Parameters.AddWithValue("$key","protect:dataset:"+id);return cmd.ExecuteScalar() as string=="true";}
    public void ProtectDataset(Guid id,bool protect)
    {if(!ContainsDataset(id))throw new ArgumentException("Dataset not found.");Initialize();using var db=Open(write:true);using var cmd=db.CreateCommand();cmd.CommandText="INSERT OR REPLACE INTO metadata VALUES($key,$value)";cmd.Parameters.AddWithValue("$key","protect:dataset:"+id);cmd.Parameters.AddWithValue("$value",protect?"true":"false");cmd.ExecuteNonQuery();}
    public long ProtectedDatasetCount() => DatasetsForProtection().LongCount(d=>DatasetProtected(d.Id)||MissionIsProtected?.Invoke(d.MissionId)==true);
    private IEnumerable<ScienceDataset> DatasetsForProtection()
    {for(var offset=0;;offset+=100){var rows=Datasets(limit:100,offset:offset);foreach(var row in rows)yield return row;if(rows.Count<100)yield break;}}
}

public sealed class ScientificCache(ScienceStore science, MissionStore missions, StorageManagement management, Action<string>? deleteFile = null, Action<string>? log = null) : IAsyncDisposable
{
    private readonly object gate=new();
    private readonly Dictionary<Guid,CleanupPreview> previews=[];
    private readonly HashSet<Guid> inUse=[];
    private int automaticRunning;
    private readonly object lifecycle=new();
    private Task automaticTask=Task.CompletedTask;
    private bool disposed;
    private int automaticCursor;
    private string PathFor(ManagedCache entry)
    {
        if(entry.RelativePath!=$"Cache/Thumbnails/{entry.Id:N}.png"||entry.Recipe!="remote-preview-v1")throw new IOException("Unrecognized cache path/recipe; file excluded.");
        return StorageManager.ResolveDataset(science.DirectoryPath,science.LibraryId,entry.RelativePath);
    }
    private CleanupItem Eligibility(ManagedCache entry)
    {
        var status="Excluded";var reason="Unverified dependency; retained.";var name=missions.Detail(entry.MissionId)?.Mission.Name??entry.MissionId.ToString();
        try
        {
            var file=PathFor(entry);
            if(inUse.Contains(entry.Id)){status="In Use";reason="Preview is being generated/read.";}
            else if(missions.IsProtected(entry.MissionId)||science.DatasetProtected(entry.DatasetId)){status="Protected";reason="Associated mission or dataset is protected.";}
            else
            {
                var product=science.Product(entry.ProductId);var original=StorageManager.ResolveDataset(science.DirectoryPath,product.LibraryId,product.RelativePath);
                if(product.Sha256!=entry.ProductSha256||!File.Exists(original)||new FileInfo(original).Length!=product.Bytes)throw new IOException("Original complete product is missing/changed; cannot establish regeneration.");
                if(!File.Exists(file)||new FileInfo(file).Length!=entry.Bytes||Hash(file)!=entry.Sha256)throw new IOException("Cache file missing/changed; unrelated bytes are preserved.");
                status="Eligible";reason="Registered remote-preview-v1 thumbnail can be regenerated from its retained complete scientific product.";
            }
        }
        catch(Exception e) when(e is IOException or ArgumentException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException){reason=e.Message;}
        return new(entry,"Scientific thumbnail",name,status,reason,Math.Max(0,(DateTimeOffset.UtcNow-entry.CreatedUtc).TotalDays));
    }
    private static string Hash(string path){using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();}
    public CleanupPreview Preview(bool automatic=false)
    {
        lock(gate)
        {
            var entries=science.CacheEntries();IEnumerable<ManagedCache> candidates=entries;
            if(automatic&&entries.Length>0){automaticCursor%=entries.Length;candidates=entries.Skip(automaticCursor).Take(20);automaticCursor+=20;}
            var rows=candidates.Select(Eligibility).ToArray();var eligible=rows.Where(r=>r.Status=="Eligible").ToArray();
            var value=new CleanupPreview(Guid.NewGuid(),DateTimeOffset.UtcNow,rows,eligible.LongLength,eligible.Sum(r=>r.Cache.Bytes),eligible.Select(r=>(DateTimeOffset?)r.Cache.CreatedUtc).Min(),eligible.Select(r=>(DateTimeOffset?)r.Cache.CreatedUtc).Max(),management.Snapshot().Science.UsedBytes);
            if(previews.Count>=5)previews.Remove(previews.OrderBy(v=>v.Value.CreatedUtc).First().Key);previews[value.Id]=value;return value;
        }
    }
    public object Page(Guid id,int offset=0,string sort="created",string filter="",string status="")
    {
        lock(gate)
        {
            if(!previews.TryGetValue(id,out var preview))throw new ArgumentException("Refresh the cleanup preview.");
            var rows=preview.Items.Where(r=>(status.Length==0||r.Status==status)&&(filter.Length==0||($"{r.Cache.RelativePath} {r.Category} {r.MissionName} {r.Cache.MissionId} {r.Cache.DatasetId} {r.Cache.ProductId}").Contains(filter,StringComparison.OrdinalIgnoreCase)));
            rows=sort switch{"size"=>rows.OrderByDescending(r=>r.Cache.Bytes),"age"=>rows.OrderByDescending(r=>r.AgeDays),"mission"=>rows.OrderBy(r=>r.MissionName),"dataset"=>rows.OrderBy(r=>r.Cache.DatasetId),"category"=>rows.OrderBy(r=>r.Category),_=>rows.OrderBy(r=>r.Cache.CreatedUtc)};
            return new{preview.Id,preview.CreatedUtc,preview.EligibleFiles,preview.EstimatedBytes,preview.OldestUtc,preview.NewestUtc,preview.CurrentUsage,expectedUsageAfter=Math.Max(0,preview.CurrentUsage-preview.EstimatedBytes),preview.Policy,total=rows.Count(),items=rows.Skip(Math.Max(0,offset)).Take(100).ToArray()};
        }
    }
    public CleanupResult Clean(Guid previewId,bool confirmed,bool automatic=false)
    {
        if(!confirmed)throw new InvalidOperationException("Cleanup cancelled; no files were removed.");
        lock(gate)
        {
            if(!previews.TryGetValue(previewId,out var preview)||DateTimeOffset.UtcNow-preview.CreatedUtc>TimeSpan.FromMinutes(5))throw new InvalidOperationException("Cleanup preview is stale. Refresh Preview before confirming.");
            if(science.AvailabilityError() is {} unavailable)throw new IOException(unavailable);
            var currentUsage=management.Snapshot().Science.UsedBytes;
            if(Math.Abs(currentUsage-preview.CurrentUsage)>Math.Max(1048576,preview.CurrentUsage*.1))throw new InvalidOperationException("Storage changed materially since this preview. Refresh Preview before confirming.");
            var removed=0;var skipped=0;var failed=0;long bytes=0;var notes=new List<string>();var disk=new LocalStorageCapacity();long? before=null;
            try{before=disk.FreeBytes(science.DirectoryPath);}catch(IOException){}
            foreach(var row in preview.Items.Where(r=>r.Status=="Eligible").Take(automatic?20:int.MaxValue))
            {
                var actual=Eligibility(row.Cache);if(actual.Status!="Eligible"){skipped++;notes.Add($"Skipped {row.Cache.RelativePath}: {actual.Reason}");continue;}
                try
                {
                    // Verify the retained scientific input, not merely its filename,
                    // immediately before deleting this positively registered cache.
                    _=science.ReadProduct(row.Cache.ProductId);
                    var file=PathFor(row.Cache);var size=new FileInfo(file).Length;(deleteFile??File.Delete)(file);if(File.Exists(file))throw new IOException("The file still exists after the removal attempt.");removed++;bytes+=size;management.AddedScienceFile(-size,true);notes.Add("Removed regenerable cache "+row.Cache.RelativePath);
                    try{science.ForgetCache(row.Cache.Id);}catch(Exception e) when(e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException){notes.Add("File removed, but its index entry needs review: "+e.Message);}
                }
                catch(Exception e) when(e is IOException or UnauthorizedAccessException or ArgumentException or Microsoft.Data.Sqlite.SqliteException){failed++;notes.Add($"Retained {row.Cache.RelativePath}: {e.Message}");}
            }
            long? recovered=null;try{if(before is not null)recovered=disk.FreeBytes(science.DirectoryPath)-before;}catch(IOException){}
            var result=new CleanupResult(Guid.NewGuid(),DateTimeOffset.UtcNow,removed,skipped,failed,bytes,recovered,notes.ToArray());
            try{PreferenceStore.WriteAtomic(System.IO.Path.Combine(science.DirectoryPath,"Metadata","CleanupHistory",result.Id.ToString("N")+".json"),result);}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException){result=result with{Messages=result.Messages.Append("Cleanup completed but the library history log could not be saved: "+e.Message).ToArray()};}
            log?.Invoke(JsonSerializer.Serialize(result,WireJson.Options));
            previews.Remove(previewId);return result;
        }
    }
    public CleanupResult[] History()
    {
        var folder=StorageManager.ResolveDataset(science.DirectoryPath,science.LibraryId,"Metadata/CleanupHistory");if(!Directory.Exists(folder))return [];
        var results=new List<CleanupResult>();
        foreach(var file in Directory.EnumerateFiles(folder,"*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(30))
        {
            try { StorageManager.ValidateLocalPath(file);if(new FileInfo(file).Length>4194304)continue;var result=JsonSerializer.Deserialize<CleanupResult>(File.ReadAllText(file));if(result is not null&&System.IO.Path.GetFileName(file)==result.Id.ToString("N")+".json")results.Add(result); }
            catch(Exception error) when(error is IOException or JsonException or UnauthorizedAccessException){ }
        }
        return results.ToArray();
    }
    public bool ScheduleAutomatic()
    {
        lock(lifecycle)
        {
            if(disposed||!management.Preferences.AutomaticCacheCleanup||Interlocked.Exchange(ref automaticRunning,1)!=0)return false;
            automaticTask=Task.Run(()=>{try{var preview=Preview(true);if(preview.EligibleFiles>0)Clean(preview.Id,true,true);}catch(Exception e) when(e is IOException or InvalidOperationException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException){log?.Invoke("Automatic cache cleanup deferred: "+e.Message);}finally{Volatile.Write(ref automaticRunning,0);}});return true;
        }
    }
    public async ValueTask DisposeAsync(){Task pending;lock(lifecycle){disposed=true;pending=automaticTask;}await pending;}
    public byte[] PreviewPng(Guid id)
    {
        lock(gate)
        {
            var existing=science.CacheEntries().FirstOrDefault(c=>c.ProductId==id);
            if(existing is not null)
            {
                inUse.Add(existing.Id);try{var file=PathFor(existing);if(File.Exists(file)&&Hash(file)==existing.Sha256)return File.ReadAllBytes(file);}finally{inUse.Remove(existing.Id);}
            }
            var data=science.ReadProduct(id);var png=ScientificPreview.Png(data);var cacheId=Guid.NewGuid();var relative=$"Cache/Thumbnails/{cacheId:N}.png";
            try
            {
                using var reservation=management.ReserveScience(png.Length+8192);var ledger=science.BeginManagedWrite("Regenerable cache generation",[relative]);var path=StorageManager.ResolveDataset(science.DirectoryPath,science.LibraryId,relative);Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(png);file.Flush(true);}
                var p=data.Product;science.RegisterCache(new(cacheId,p.Id,p.MissionId,p.DatasetId,relative,png.Length,Hash(path),p.Sha256,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow));management.AddedScienceFile(png.Length,true);File.Delete(ledger);
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException){/* Viewing remains available when caching is unavailable. Work ledger retains partial bytes for review. */}
            return png;
        }
    }
}

public static class ScientificPreview
{
    public static byte[] Png(RemoteProductData data)
    {
        if(data.Source.Manifest.Kind==RemoteKind.Hyperspectral)throw new ArgumentException("Hyperspectral products use the wavelength chart and spectral CSV; no fake scalar thumbnail is generated.");
        var p=data.Product;var values=data.Derived.GetValueOrDefault("temperature")??data.Derived.GetValueOrDefault("ndvi")??data.Derived.GetValueOrDefault("elevation");
        var valid=values?.OfType<double>().ToArray()??[];var min=valid.Length>0?valid.Min():0;var max=valid.Length>0?valid.Max():1;
        using var raw=new MemoryStream();
        for(var y=0;y<p.Height;y++)
        {
            raw.WriteByte(0);
            for(var x=0;x<p.Width;x++)
            {
                var i=y*p.Width+x;byte r=0,g=0,b=0,a=0;
                if(values is not null&&values[i] is {} v){var t=Math.Clamp((v-min)/(max-min==0?1:max-min),0,1);r=(byte)(255*t);g=(byte)(210*(1-Math.Abs(t-.5)*2));b=(byte)(255*(1-t));a=255;}
                else if(data.Source.Manifest.Kind==RemoteKind.Multispectral)
                {
                    var rgb=new[]{"B04","B03","B02"}.Select(id=>data.Bands.Single(band=>band.Id==id).Values[i]).ToArray();if(rgb.All(v=>v is not null)){r=(byte)(Math.Clamp(rgb[0]!.Value/.3,0,1)*255);g=(byte)(Math.Clamp(rgb[1]!.Value/.3,0,1)*255);b=(byte)(Math.Clamp(rgb[2]!.Value/.3,0,1)*255);a=255;}
                }
                raw.Write([r,g,b,a]);
            }
        }
        using var output=new MemoryStream();output.Write([137,80,78,71,13,10,26,10]);
        void Chunk(string name,byte[] bytes)
        {
            var header=new byte[4];System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header,bytes.Length);output.Write(header);var type=Encoding.ASCII.GetBytes(name);output.Write(type);output.Write(bytes);
            uint crc=0xffffffff;foreach(var value in type.Concat(bytes)){crc^=value;for(var bit=0;bit<8;bit++)crc=(crc&1)!=0?(crc>>1)^0xedb88320:crc>>1;}System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header,crc^0xffffffff);output.Write(header);
        }
        var ihdr=new byte[13];System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr,p.Width);System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4),p.Height);ihdr[8]=8;ihdr[9]=6;Chunk("IHDR",ihdr);
        using var compressed=new MemoryStream();using(var zip=new ZLibStream(compressed,CompressionLevel.Fastest,true))zip.Write(raw.ToArray());Chunk("IDAT",compressed.ToArray());Chunk("IEND",[]);return output.ToArray();
    }
}
