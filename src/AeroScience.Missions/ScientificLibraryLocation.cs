using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

public sealed partial class ScienceStore
{
    // No arbitrary path from HTTP reaches a shell. Resolve the current store identity
    // and, optionally, its exact managed source ownership metadata again at execution.
    public string ScientificFolder(Guid expectedLibrary, Guid? sourceId = null)
    {
        if (AvailabilityError() is { } unavailable) throw new IOException(unavailable);
        if (LibraryId != expectedLibrary) throw new IOException("Scientific Library changed; refresh the storage location before opening it.");
        StorageManager.ValidateLocalPath(DirectoryPath);
        if (!Directory.Exists(DirectoryPath)) throw new IOException("The configured Scientific Library drive/folder is unavailable. Reconnect it; no replacement was created.");
        if (sourceId is null) return DirectoryPath;
        var source = Source(sourceId.Value) ?? throw new ArgumentException("The retained source is unavailable. Refresh the Library inventory.");
        if (source.RelativeDirectory != $"Sources/{source.Id:N}") throw new IOException("This source has no verified managed folder identity.");
        var folder = StorageManager.ResolveDataset(DirectoryPath, expectedLibrary, source.RelativeDirectory);
        StorageManager.ValidateLocalPath(folder);
        var metadata = ScientificRaster.SafeFile(folder, "source.json");
        if (new FileInfo(metadata).Length > 16777216) throw new IOException("Source ownership metadata exceeds the supported verification bound.");
        ScientificSource retained;
        try { retained = JsonSerializer.Deserialize<ScientificSource>(File.ReadAllText(metadata), WireJson.Options) ?? throw new IOException("Source ownership metadata is missing."); }
        catch(JsonException error){throw new IOException("Source ownership metadata is invalid. Restore/review it; nothing was opened or changed.",error);}
        if (ScientificCatalog.Hash(retained) != ScientificCatalog.Hash(source)) throw new IOException("Source folder identity differs from the retained catalog. Nothing was opened or changed.");
        return folder;
    }
}
