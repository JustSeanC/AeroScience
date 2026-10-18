using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;

namespace AeroScience.Missions;

public sealed partial class ScienceStore
{
    private static T ReadCatalog<T>(string json) where T:class
    {
        try{return JsonSerializer.Deserialize<T>(json,WireJson.Options)??throw new IOException("Scientific catalogue metadata is missing; original files were preserved.");}
        catch(JsonException error){throw new IOException("Scientific catalogue metadata is invalid; review/recover the selected library. Original data were preserved.",error);}
    }
    public (long Count, long Bytes, string[] Paths) ManagedProductSummary()
    {
        if (!File.Exists(DatabasePath)) return (0, 0, []);
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT name FROM sqlite_master WHERE name='remote_products'";
        if (cmd.ExecuteScalar() is null) return (0, 0, []);
        cmd.CommandText = "SELECT COUNT(*),COALESCE(SUM(json_extract(payload_json,'$.bytes')),0) FROM remote_products";
        long count, bytes; using (var reader = cmd.ExecuteReader()) { reader.Read(); count = reader.GetInt64(0); bytes = reader.GetInt64(1); }
        cmd.CommandText = "SELECT json_extract(payload_json,'$.relativePath') FROM remote_products LIMIT 10";
        using var paths = cmd.ExecuteReader(); var list = new List<string>(); while (paths.Read()) list.Add(paths.GetString(0)); return (count, bytes, list.ToArray());
    }
    public IEnumerable<RemoteProduct> AllProducts(Guid? mission = null)
    {
        var offset = 0;
        while (true) { var page = Products(mission, 200, offset); foreach (var product in page) yield return product; if (page.Length < 200) yield break; offset += page.Length; }
    }
    public string CreateRecoveryBackup(string folder)
    {
        Initialize(); var summary = ManagedProductSummary();
        if (disk.FreeBytes(folder) < summary.Bytes + StorageManager.DatabaseBytes(DatabasePath) * 2 + StorageManager.MinimumFreeBytes) throw new IOException("Insufficient space for the scientific database and complete managed products. No data was cleared.");
        var destination = Path.Combine(folder, "AeroScience-science-recovery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(destination);
        var database = DataBackup.Create(DatabasePath, destination, true);
        foreach (var product in AllProducts())
        {
            var from = StorageManager.ResolveDataset(DirectoryPath, product.LibraryId, product.RelativePath);
            if (!File.Exists(from) || Digest(from) != product.Sha256) throw new IOException("A managed product is missing/changed; a complete verified backup could not be made. No data was cleared.");
            var to = Path.Combine(destination, product.RelativePath); Directory.CreateDirectory(Path.GetDirectoryName(to)!); File.Copy(from, to);
            if (Digest(to) != product.Sha256) throw new IOException("Product backup failed verification. No data was cleared.");
        }
        PreferenceStore.WriteAtomic(Path.Combine(destination, "recovery.json"), new { formatVersion = 1, libraryId = LibraryId, database = Path.GetFileName(database), sourceDirectory = DirectoryPath, products = summary.Count,
            note = "Original imported Sources are preserved by clear and are not duplicated here. Restore the verified database and listed Products into the same library identity; retain originals until verification." });
        return destination;
    }
    private static string Digest(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(); }
    private void InitializeRemoteTables(SqliteConnection db)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='remote_products'";
        if (Convert.ToInt64(cmd.ExecuteScalar()) != 0) { EnsureCacheTable(db); return; }
        // Additive extension: retain schema-1 observations and all historical JSON.
        // A verified SQLite backup precedes the first extension on each library.
        var backups = Path.Combine(DirectoryPath, "Metadata", "Backups"); Directory.CreateDirectory(backups);
        _ = DataBackup.Create(DatabasePath, backups, true);
        using var tx = db.BeginTransaction(); cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE remote_sources(id TEXT PRIMARY KEY,manifest_sha TEXT NOT NULL UNIQUE,payload_json TEXT NOT NULL);
            CREATE TABLE remote_products(id TEXT PRIMARY KEY,dataset_id TEXT NOT NULL REFERENCES datasets(id),
              mission_id TEXT NOT NULL,instrument_id TEXT NOT NULL,flight_utc TEXT NOT NULL,payload_json TEXT NOT NULL,
              UNIQUE(dataset_id,flight_utc));
            CREATE INDEX remote_products_mission ON remote_products(mission_id,flight_utc);
            """;
        cmd.ExecuteNonQuery(); tx.Commit(); EnsureCacheTable(db);
    }
    public static ScientificSourceManifest ReadSourceManifest(string manifestPath)
    {
        StorageManager.ValidateLocalPath(manifestPath);
        if (new FileInfo(manifestPath).Length > 1048576) throw new InvalidDataException("Source manifest must be under 1 MiB.");
        var source = JsonSerializer.Deserialize<ScientificSourceManifest>(File.ReadAllText(manifestPath), WireJson.Options) ?? throw new InvalidDataException("A source manifest is required.");
        if (source.FormatVersion != 1 || !Enum.IsDefined(source.Kind) || source.Bands is null || source.Bands.Length is < 1 or > 512 || source.Bands.Any(b=>b is null || string.IsNullOrWhiteSpace(b.File))
            || new[] { source.Name, source.Provider, source.License, source.Citation, source.SceneId, source.VerticalDatum }.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 2000 || s.Any(c => char.IsControl(c)))
            || source.ObservationUtc is { Offset: var zone } && zone != TimeSpan.Zero || source.ObservationUtc>DateTimeOffset.UtcNow.AddDays(1)
            || source.PublicationUtc is {Offset:var publicationZone} && publicationZone!=TimeSpan.Zero
            || source.Bands.Select(b => b.Id).Distinct(StringComparer.Ordinal).Count() != source.Bands.Length)
            throw new InvalidDataException("Supply version 1, one real scene/date, unique bands, provider, license, citation and explicit vertical datum (or not applicable).");
        source=source with{Bands=source.Bands.Select(b=>b with{File=b.File.Replace('\\','/'),HeaderFile=b.HeaderFile?.Replace('\\','/')}).ToArray()};
        foreach (var b in source.Bands)
            if (string.IsNullOrWhiteSpace(b.Id) || b.Id.Length > 60 || string.IsNullOrWhiteSpace(b.Unit) || b.Unit.Length > 60 || b.Channel < 0
                || !double.IsFinite(b.Scale) || b.Scale == 0 || !double.IsFinite(b.Offset) || b.NoData is { } n && !double.IsFinite(n)
                || b.SceneId is not null && b.SceneId != source.SceneId)
                throw new InvalidDataException("Each band needs finite scaling, an explicit unit and the same source scene/date.");
        var ids = source.Bands.Select(b => b.Id).ToHashSet();
        if (source.Kind == RemoteKind.Thermal && (!ids.Contains("ST_B10") || !ids.Contains("QA_PIXEL")
            || source.Bands.Single(b => b.Id == "ST_B10") is var thermal && (thermal.Unit != "K" || Math.Abs(thermal.Scale - .00341802) > 1e-10 || thermal.Offset != 149)))
            throw new InvalidDataException("Thermal imports require Landsat Collection 2 ST_B10 (0.00341802 × DN + 149 Kelvin) and QA_PIXEL.");
        if (source.Kind == RemoteKind.Multispectral && new[] { "B02", "B03", "B04", "B08", "SCL" }.Any(id => !ids.Contains(id)))
            throw new InvalidDataException("Multispectral imports require Sentinel-2 L2A B02/B03/B04/B08 and SCL from the same scene.");
        if (source.Kind == RemoteKind.Multispectral && source.Bands.Where(b => b.Id != "SCL").Any(b => b.Unit != "reflectance"))
            throw new InvalidDataException("Use reflectance units and the source product's actual quantification scale and additive offset.");
        if(source.Kind is RemoteKind.Thermal or RemoteKind.Multispectral && source.Bands.Where(b=>b.Id is "QA_PIXEL" or "QA_RADSAT" or "SCL").Any(b=>b.Unit!="1"||b.Scale!=1||b.Offset!=0||b.BadBand))
            throw new InvalidDataException("Quality masks require their original unscaled integer class/bit values, unit 1, scale 1, offset 0, and cannot be disabled as bad bands.");
        if (source.Kind == RemoteKind.Terrain && (source.Bands.Length != 1 || source.Bands[0].Unit != "m"))
            throw new InvalidDataException("Terrain imports require one elevation band in metres and an explicitly stated source vertical datum. DEM samples are not LiDAR returns.");
        return source;
    }
    public static (ScientificSourceManifest Manifest, long Bytes) InspectImport(string manifestPath)
    {
        var source = ReadSourceManifest(manifestPath); var root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        long bytes = 0;
        foreach (var name in source.Bands.SelectMany(b => new[] { b.File, b.HeaderFile }).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var file = ScientificRaster.SafeFile(root, name); var size = new FileInfo(file).Length;
            if (size is <= 0 or > 17179869184) throw new InvalidDataException("Each local source file must contain 1 byte to 16 GiB; split larger collections.");
            bytes = checked(bytes + size);
        }
        if (bytes > 68719476736) throw new InvalidDataException("Import supports up to 64 GiB per source collection; import bounded geographic subsets.");
        return (source, bytes);
    }
    public async Task<ScientificSource> ImportSourceAsync(string manifestPath, CancellationToken token = default, IProgress<string>? progress = null)
    {
        var inspected = InspectImport(manifestPath); var manifest = inspected.Manifest;
        using var reservation=Management?.ReserveScience(inspected.Bytes+1048576);
        if (Sources().Length >= 500) throw new InvalidOperationException("This library supports up to 500 source collections; use bounded regional collections.");
        if (AvailabilityError() is { } unavailable) throw new IOException(unavailable);
        if (disk.FreeBytes(DirectoryPath) < inspected.Bytes + StorageManager.MinimumFreeBytes) throw new IOException("Insufficient Science Library space for the original source files and reserve.");
        var root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!; var id = Guid.NewGuid();
        var relative = "Sources/" + id.ToString("N"); var destination = Path.Combine(DirectoryPath, relative); var temporary = destination + ".partial";
        var grids = new Dictionary<string, RasterGrid>(); var bands = new List<SourceBand>();
        foreach (var band in manifest.Bands)
        {
            token.ThrowIfCancellationRequested(); using var raster = new ScientificRaster(root, band);
            if (band.WavelengthNm is { } declared && raster.NativeWavelengthNm is { } native && Math.Abs(declared - native) > .01) throw new InvalidDataException("Wavelength metadata differs from the original ENVI header.");
            grids.Add(band.Id, raster.Grid); bands.Add(band with { WavelengthNm = raster.NativeWavelengthNm ?? band.WavelengthNm });
        }
        manifest = manifest with { Bands = bands.ToArray() };
        if (manifest.Kind == RemoteKind.Hyperspectral)
        {
            if (bands.Count < 16 || bands.All(b=>b.BadBand) || grids.Values.Distinct().Count()!=1 || bands.Select(b=>b.Unit).Distinct().Count()!=1 || bands.Any(b => b.Format != "envi" || b.WavelengthNm is not (> 0 and < 100000) || b.Unit is not ("reflectance" or "radiance W/(m2 sr um)" or "radiance W/(m2 sr nm)" or "radiance mW/(cm2 sr um)" or "radiance uW/(cm2 sr nm)"))
                || bands.Zip(bands.Skip(1)).Any(p => p.First.WavelengthNm >= p.Second.WavelengthNm))
                throw new InvalidDataException("Hyperspectral inputs require at least 16 ordered ENVI wavelengths and explicitly distinguished reflectance or radiance units.");
        }
        Initialize();
        var names = manifest.Bands.SelectMany(b => new[] { b.File, b.HeaderFile }).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Append("source.json").ToArray();
        var ledger = BeginManagedWrite("Source import", names.SelectMany(name => new[] { relative + ".partial/" + name, relative + "/" + name }));
        Directory.CreateDirectory(temporary);
        try
        {
            var files = new List<ManagedSourceFile>();
            foreach (var name in manifest.Bands.SelectMany(b => new[] { b.File, b.HeaderFile }).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested(); var fromPath = ScientificRaster.SafeFile(root, name); var target = Path.Combine(temporary, name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); progress?.Report("Copying " + Path.GetFileName(name));
                using (var from = File.OpenRead(fromPath))
                using (var to = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
                { await from.CopyToAsync(to, token); to.Flush(true); }
                if (Digest(fromPath) != Digest(target)) throw new IOException("Source changed during import; no source was published.");
                files.Add(new(name.Replace('\\', '/'), new FileInfo(target).Length, Digest(target)));
            }
            var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { manifest, files }, WireJson.Options))).ToLowerInvariant();
            var source = new ScientificSource(id, manifest, DateTimeOffset.UtcNow, grids, files.ToArray(), fingerprint, relative);
            PreferenceStore.WriteAtomic(Path.Combine(temporary, "source.json"), source);
            token.ThrowIfCancellationRequested(); Directory.Move(temporary, destination);
            lock (gate)
            {
                using var db = Open(write: true); using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO remote_sources VALUES($id,$sha,$json)";
                cmd.Parameters.AddWithValue("$id", id.ToString()); cmd.Parameters.AddWithValue("$sha", fingerprint); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(source, WireJson.Options)); cmd.ExecuteNonQuery();
            }
            Management?.AddedScienceFile(inspected.Bytes + new FileInfo(Path.Combine(destination, "source.json")).Length, source: true);
            File.Delete(ledger);
            return source;
        }
        catch
        {
            // Preserve interrupted/duplicate imports for explicit review; they
            // are never catalogued as available and never replace originals.
            try { Management?.RefreshScience(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException) { }
            throw;
        }
    }
    public ScientificSource[] Sources()
    {
        if (AvailabilityError() is { } unavailable) throw new IOException(unavailable);
        if (!File.Exists(DatabasePath)) return [];
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT name FROM sqlite_master WHERE name='remote_sources'";
        if (cmd.ExecuteScalar() is null) return [];
        cmd.CommandText = "SELECT payload_json FROM remote_sources ORDER BY rowid DESC LIMIT 500";
        using var read = cmd.ExecuteReader(); var list = new List<ScientificSource>();
        while (read.Read()) list.Add(ReadCatalog<ScientificSource>(read.GetString(0))); return list.ToArray();
    }
    public SourceAvailability[] Availability(Coordinates? point = null) => Sources().Select(source =>
    {
        try
        {
            var root = StorageManager.ResolveDataset(DirectoryPath, LibraryId, source.RelativeDirectory);
            foreach (var file in source.Files) if (new FileInfo(ScientificRaster.SafeFile(root, file.RelativePath)).Length != file.Bytes) throw new IOException("A source file is missing or changed; reimport a verified collection.");
            using var raster = new ScientificRaster(root, source.Manifest.Bands.First(b => !b.BadBand));
            return new SourceAvailability(source, true, point is null ? null : raster.Covers(point), null);
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        { return new SourceAvailability(source, false, false, e.Message); }
    }).ToArray();
    public RemoteProduct[] Products(Guid? mission = null, int limit = 50, int offset = 0, string? instrument = null)
    {
        if (AvailabilityError() is { } unavailable) throw new IOException(unavailable);
        if (!File.Exists(DatabasePath)) return [];
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT name FROM sqlite_master WHERE name='remote_products'";
        if (cmd.ExecuteScalar() is null) return [];
        cmd.CommandText = "SELECT payload_json FROM remote_products WHERE ($mission='' OR mission_id=$mission) AND ($instrument='' OR instrument_id=$instrument) ORDER BY flight_utc DESC LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$instrument",instrument??"");
        cmd.Parameters.AddWithValue("$mission", mission?.ToString() ?? ""); cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200)); cmd.Parameters.AddWithValue("$offset", Math.Max(offset, 0));
        using var read = cmd.ExecuteReader(); var products = new List<RemoteProduct>();
        while (read.Read()) products.Add(ReadCatalog<RemoteProduct>(read.GetString(0))); return products.ToArray();
    }
    public RemoteProduct Product(Guid id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT payload_json FROM remote_products WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id.ToString()); return ReadCatalog<RemoteProduct>(cmd.ExecuteScalar() as string ?? throw new ArgumentException("Scientific product not found."));
    }
    public RemoteProductData ReadProduct(Guid id)
    {
        var product = Product(id); var path = StorageManager.ResolveDataset(DirectoryPath, product.LibraryId, product.RelativePath);
        if (new FileInfo(path).Length != product.Bytes || Digest(path) != product.Sha256) throw new IOException("Scientific product is missing or failed its integrity check. The archive was preserved.");
        var data = JsonSerializer.Deserialize<RemoteProductData>(File.ReadAllText(path), WireJson.Options) ?? throw new IOException("Scientific product metadata is invalid.");
        return data with { Product = product };
    }
    public RemoteProduct Publish(RemoteProductData data, Action? ensureCurrent = null)
    {
        if (disk.FreeBytes(DirectoryPath) < StorageManager.MinimumFreeBytes + 33554432) throw new IOException("Insufficient space for a bounded science product and reserve; nothing was deleted.");
        var target = StorageManager.ResolveDataset(DirectoryPath, LibraryId, data.Product.RelativePath); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, WireJson.Options);
        using var reservation=Management?.ReserveScience(bytes.Length+65536);
        if (bytes.Length > 33554432) throw new IOException("Product exceeds the supported 32 MiB acquisition bound.");
        var temporary = target + ".partial";
        var ledger = BeginManagedWrite("Scientific acquisition", [data.Product.RelativePath, data.Product.RelativePath + ".partial"]);
        try
        {
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
        var product = data.Product with { Bytes = bytes.Length, Sha256 = Digest(temporary) };
        lock (gate)
        {
            using var db = Open(write: true); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            ensureCurrent?.Invoke();
            cmd.CommandText = "INSERT INTO remote_products VALUES($id,$dataset,$mission,$instrument,$utc,$json)";
            cmd.Parameters.AddWithValue("$id", product.Id.ToString()); cmd.Parameters.AddWithValue("$dataset", product.DatasetId.ToString()); cmd.Parameters.AddWithValue("$mission", product.MissionId.ToString()); cmd.Parameters.AddWithValue("$instrument", product.InstrumentId);
            cmd.Parameters.AddWithValue("$utc", product.FlightPacketUtc.ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(product, WireJson.Options)); cmd.ExecuteNonQuery();
            File.Move(temporary, target); tx.Commit();
        }
        Management?.AddedScienceFile(bytes.Length, product: true);
        File.Delete(ledger);
        return product;
        }
        catch
        {
            try { Management?.RefreshScience(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException) { }
            throw;
        }
    }
    public static RemoteStorageEstimate RemoteEstimate(InstrumentConfiguration config, double minutes, int bands) =>
        new((long)Math.Ceiling(minutes * 60000 / config.IntervalMs), 24, 24, bands, 8, 24L * 24 * bands * 8,
            checked((long)Math.Ceiling(minutes * 60000 / config.IntervalMs) * (24L * 24 * bands * 32 + 262144)), 33554432,
            "24×24 geographic samples, 8-byte scientific values; conservative JSON/index/coordinate overhead, no compression assumption. Source imports are additional and measured before copying.");
}
