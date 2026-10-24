using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;

namespace AeroScience.Tests;

// Local read-only scientific validation tool, never an application import endpoint.
public static class EmitProductProbe
{
    public sealed record Input(string LibraryDirectory,string[] RelativeOriginals,EmitNativeWindow NativeWindow,EmitGridWindow GeographicWindow,EmitQualityPolicy? QualityPolicy=null);
    public static async Task Run(string[] args)
    {
        if(args.Length!=2)throw new ArgumentException("Use --emit-product-probe request.json NEW-output-directory. No network access, import or database creation.");
        var inputFile=new FileInfo(args[0]);if(inputFile.Length>65536)throw new ArgumentException("Use a bounded local probe request.");
        var input=JsonSerializer.Deserialize<Input>(File.ReadAllText(inputFile.FullName),WireJson.Options)??throw new ArgumentException("Missing probe request.");
        var root=Path.GetFullPath(input.LibraryDirectory);var library=StorageManager.LibraryId(root);var output=Path.GetFullPath(args[1]);
        static bool Contains(string parent,string child)=>parent.Equals(child,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal)||child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal);
        StorageManager.ValidateLocalPath(output);
        if(Directory.Exists(output)||File.Exists(output)||Contains(root,output)||Contains(output,root))throw new ArgumentException("Preserve original files and prior evidence; choose a new output directory outside the Library.");
        if(input.RelativeOriginals.Length!=3)throw new ArgumentException("Select exactly three approved original files.");
        var originals=input.RelativeOriginals.Select(relative=>{var path=StorageManager.ResolveDataset(root,library,relative);if(new FileInfo(path).Length>64L*1024*1024*1024)throw new ArgumentException("Original exceeds the reader's per-file processing bound.");using var stream=File.OpenRead(path);return new HdfOriginal(root,library,relative,stream.Length,Convert.ToHexStringLower(SHA256.HashData(stream)));}).ToArray();
        var decoder=new EmitProductDecoder(new ScientificHdfReader());var product=await decoder.OpenAsync(EmitProductDecoder.ResolveOriginals(originals));
        var native=await decoder.ReadNativeAsync(product,input.NativeWindow,input.QualityPolicy);var geographic=await decoder.ReadGeographicAsync(product,input.GeographicWindow,input.QualityPolicy);
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output,"emit-definition.json"),JsonSerializer.Serialize(product.Definition,WireJson.Options));
        await File.WriteAllTextAsync(Path.Combine(output,"emit-native.json"),JsonSerializer.Serialize(native,WireJson.Options));
        await File.WriteAllTextAsync(Path.Combine(output,"emit-geographic.json"),JsonSerializer.Serialize(geographic,WireJson.Options));
        Console.WriteLine("EMIT local-original product/window verification completed. No data was downloaded/imported and no mission was created. Independent numerical comparison and original authenticity/authorization remain separate; reports may contain scientific samples and must stay local unless explicitly approved.");
    }
}
