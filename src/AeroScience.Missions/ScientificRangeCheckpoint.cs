using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Shared;
namespace AeroScience.Missions;

// Owned partial COG blocks, not a published source. A fresh exact 206 establishes
// identity before any saved block is used. Tokens/URLs never enter the checkpoint.
internal sealed class ScientificRangeCheckpoint
{
    private sealed record Index(string Etag,long Length,Dictionary<long,string> Blocks);
    private readonly string folder;private readonly string workFolder;private readonly string relative;private readonly string workPath;private Index? index;
    internal ScientificRangeCheckpoint(ScienceStore science,string relative)
    {
        this.relative=relative;workFolder=Path.Combine(science.DirectoryPath,"Metadata","Work");
        folder=StorageManager.ResolveDataset(science.DirectoryPath,science.LibraryId,relative);Directory.CreateDirectory(folder);
        var ledger=Path.Combine(folder,"ledger.json");
        if(!File.Exists(ledger)){var owned=science.BeginManagedWrite("Recoverable approved Library numeric ranges",[relative+"/index.json",relative+"/ledger.json"]);PreferenceStore.WriteAtomic(ledger,new{work=Path.GetFileName(owned)});}
        using var record=JsonDocument.Parse(File.ReadAllText(ledger));var work=record.RootElement.GetProperty("work").GetString()!;
        if(!Guid.TryParseExact(Path.GetFileNameWithoutExtension(work),"N",out _)||Path.GetFileName(work)!=work)throw new IOException("Invalid partial range ownership record.");
        workPath=Path.Combine(workFolder,work);
    }
    internal void Establish(string? etag,long length)
    {
        if(etag is null||etag.StartsWith("W/",StringComparison.Ordinal)){index=null;return;}
        var path=Path.Combine(folder,"index.json");
        try{index=File.Exists(path)?JsonSerializer.Deserialize<Index>(File.ReadAllText(path),WireJson.Options):null;}catch(JsonException){index=null;}
        if(index?.Blocks is null||index.Blocks.Count>2048)index=null;
        if(index is not null&&(index.Etag!=etag||index.Length!=length))throw new IOException("Approved scientific asset identity changed. Create a fresh preview; incompatible partial bytes were not appended or published.");
        index??=new(etag,length,[]);
    }
    internal byte[]? Read(long block)
    {
        if(index is null||!index.Blocks.TryGetValue(block,out var hash))return null;
        var file=Path.Combine(folder,block+".bin");if(!File.Exists(file))return null;StorageManager.ValidateLocalPath(file);
        var expected=Math.Min(65536,index.Length-block*65536);if(new FileInfo(file).Length!=expected)return null;
        var bytes=File.ReadAllBytes(file);return Digest(bytes)==hash?bytes:null;
    }
    internal void Save(long block,byte[] bytes)
    {
        if(index is null)return;
        if(!index.Blocks.ContainsKey(block))
        {
            var work=JsonSerializer.Deserialize<ManagedScientificWork>(File.ReadAllText(workPath),WireJson.Options)!;
            var name=relative+"/"+block+".bin";if(!work.Paths.Contains(name)){if(work.Paths.Length>=2050)throw new IOException("Partial range checkpoint exceeds bounded blocks.");PreferenceStore.WriteAtomic(workPath,work with{Paths=work.Paths.Append(name).ToArray()});}
        }
        var file=Path.Combine(folder,block+".bin");StorageManager.ValidateLocalPath(file);using(var output=new FileStream(file,FileMode.Create,FileAccess.Write,FileShare.None,65536,FileOptions.WriteThrough)){output.Write(bytes);output.Flush(true);}
        index.Blocks[block]=Digest(bytes);PreferenceStore.WriteAtomic(Path.Combine(folder,"index.json"),index);
    }
    internal void Complete()
    {
        // Only this checkpoint's explicitly owned files; no scientific originals or backups.
        var ledgerFile=Path.Combine(folder,"ledger.json");string? work=null;
        if(File.Exists(ledgerFile))using(var json=JsonDocument.Parse(File.ReadAllText(ledgerFile)))work=json.RootElement.GetProperty("work").GetString();
        foreach(var f in Directory.EnumerateFiles(folder))if(Path.GetFileName(f) is "index.json" or "ledger.json"||int.TryParse(Path.GetFileNameWithoutExtension(f),out _)&&Path.GetExtension(f)==".bin")File.Delete(f);
        Directory.Delete(folder);
        if(work is not null&&Guid.TryParseExact(Path.GetFileNameWithoutExtension(work),"N",out _))File.Delete(Path.Combine(workFolder,work));
    }
    private static string Digest(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
