using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

// Bounded aviation originals, not numeric science. No reprojection or neatline claim here.
public static class AviationDownloads
{
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i =>
    { var value = (uint)i; for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) != 0 ? 0xEDB88320u : 0); return value; }).ToArray();
    public const long MaximumTransferBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumExpandedBytes = 8L * 1024 * 1024 * 1024;
    public static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, WireJson.Options))).ToLowerInvariant();
    public static string SelectionHash(AviationCatalogItem item, long transfer, long expanded) => Hash(new { version = 1, item, transfer, expanded });
    public static void Validate(AviationDownloadJob job)
    {
        if (job.Version != 1 || job.Preview.Version != 1) throw new NotSupportedException("Unsupported aviation download version.");
        var p = job.Preview; AviationCatalog.Validate(p.Item);
        if (job.Id == Guid.Empty || p.Id != job.Id || job.Revision < 0 || p.MaximumTransferBytes is < 1048576 or > MaximumTransferBytes ||
            p.MaximumExpandedBytes is < 1048576 or > MaximumExpandedBytes || job.DownloadedBytes < 0 || job.DownloadedBytes > p.MaximumTransferBytes ||
            job.ChargedBytes < job.DownloadedBytes || job.ChargedBytes > p.MaximumTransferBytes || job.AttemptBytes < 0 || job.AttemptBytes > job.DownloadedBytes || p.ExpectedBytes != p.Item.OriginalBytes ||
            p.SelectionHash != SelectionHash(p.Item, p.MaximumTransferBytes, p.MaximumExpandedBytes) ||
            p.CreatedUtc.Offset != TimeSpan.Zero || p.ExpiresUtc <= p.CreatedUtc || job.UpdatedUtc.Offset != TimeSpan.Zero ||
            job.State is not ("Awaiting Approval" or "Queued" or "Preparing" or "Downloading" or "Paused by User" or "Verifying" or "Completed" or "Failed" or "Cancelled" or "Interrupted" or "Waiting for Network" or "Waiting for Flight Priority"))
            throw new InvalidDataException("Invalid aviation download intent or budget.");
        if (job.State != "Awaiting Approval" && !job.Approved) throw new InvalidDataException("Aviation download requires stored approval.");
        if (job.Files is { Length: > 101 }) throw new InvalidDataException("Too many retained aviation files.");
        foreach (var file in job.Files ?? [])
            if (Path.GetFileName(file.Name) != file.Name || !System.Text.RegularExpressions.Regex.IsMatch(file.Name, @"^[A-Za-z0-9_ .-]{1,180}$") ||
                file.Bytes < 0 || !System.Text.RegularExpressions.Regex.IsMatch(file.Sha256, "^[a-f0-9]{64}$")) throw new InvalidDataException("Invalid retained aviation identity.");
    }
    public static async Task<string> FileHash(string file, CancellationToken token = default)
    {
        StorageManager.ValidateLocalPath(file);
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
    }
    public static async Task<AviationRetainedFile[]> VerifyArchive(string zip, string expanded, long maximumBytes, CancellationToken token, Func<bool>? canContinue = null)
    {
        StorageManager.ValidateLocalPath(zip); StorageManager.ValidateLocalPath(expanded);
        Directory.CreateDirectory(expanded); var files = new List<AviationRetainedFile>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count is < 1 or > 100) throw new InvalidDataException("FAA archive must contain 1–100 bounded entries.");
        long total = 0; var tiffs = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested(); var name = entry.FullName;
            if (name.StartsWith('/') || name.Contains('\\') || name.Contains(':') || name.Split('/').Any(p => p is "." or "..") ||
                ((entry.ExternalAttributes >> 16) & 0xF000) is 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("FAA archive contains an unsafe path or link.");
            if (name.EndsWith('/') && entry.Length == 0) continue;
            var fileName = Path.GetFileName(name);
            if (!System.Text.RegularExpressions.Regex.IsMatch(fileName, @"^[A-Za-z0-9_ .-]{1,180}$") || !names.Add(fileName) ||
                entry.Length < 0 || entry.Length > maximumBytes - total || entry.Length > Math.Max(1048576, entry.CompressedLength * 200))
                throw new InvalidDataException("FAA archive exceeds approved bounds or has conflicting file names.");
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            if (extension is not (".tif" or ".tiff" or ".tfw" or ".prj" or ".xml" or ".txt" or ".htm" or ".html"))
                throw new InvalidDataException("Unsupported FAA archive companion. Nested archives and executables are not extracted; original ZIP is preserved.");
            total = checked(total + entry.Length); var path = Path.Combine(expanded, fileName);
            await using (var input = entry.Open())
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536]; long count = 0; uint crc = 0xFFFFFFFF;
                int n; while ((n = await input.ReadAsync(buffer, token)) != 0)
                {
                    if (canContinue?.Invoke() == false) throw new OperationCanceledException("Aviation extraction paused by its stored control or flight-priority policy.");
                    if (n > entry.Length - count) throw new InvalidDataException("FAA expanded length exceeds its verified directory.");
                    count += n;
                    for (var i = 0; i < n; i++) crc = (crc >> 8) ^ CrcTable[(crc ^ buffer[i]) & 255];
                    await output.WriteAsync(buffer.AsMemory(0, n), token);
                }
                if (count != entry.Length || ~crc != entry.Crc32) throw new InvalidDataException("Incomplete or corrupt FAA archive entry.");
                output.Flush(true);
            }
            if (extension is ".tif" or ".tiff")
            {
                await using var input = File.OpenRead(path); var signature = new byte[4]; await input.ReadExactlyAsync(signature, token);
                if (!signature.SequenceEqual(new byte[] { 73, 73, 42, 0 }) && !signature.SequenceEqual(new byte[] { 77, 77, 0, 42 }) &&
                    !signature.SequenceEqual(new byte[] { 73, 73, 43, 0 }) && !signature.SequenceEqual(new byte[] { 77, 77, 0, 43 }))
                    throw new InvalidDataException("FAA original is not a TIFF raster. No rendering or scientific support was inferred.");
                tiffs++;
            }
            files.Add(new(fileName, entry.Length, await FileHash(path, token)));
        }
        if (tiffs == 0) throw new InvalidDataException("No original TIFF chart in this distribution.");
        return files.ToArray();
    }
    public static async Task<bool> VerifyRetained(string directory, AviationDownloadJob job, CancellationToken token = default)
    {
        if (job.State != "Completed" || job.Files is not { Length: > 0 } || job.OriginalSha256 is null) return false;
        StorageManager.ValidateLocalPath(directory);
        if (await FileHash(Path.Combine(directory, "original.zip"), token) != job.OriginalSha256) throw new IOException("Retained FAA original changed. Restore it; no replacement was downloaded.");
        foreach (var file in job.Files)
        {
            var path = Path.Combine(directory, "files", file.Name); StorageManager.ValidateLocalPath(path);
            if (new FileInfo(path).Length != file.Bytes || await FileHash(path, token) != file.Sha256) throw new IOException("Retained FAA content changed. Original evidence was preserved.");
        }
        return true;
    }
}
