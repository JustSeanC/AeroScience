using System.IO.Compression;
using AeroScience.Shared;

namespace AeroScience.Missions;

public static partial class AviationDownloads
{
    // FAA's actual AIXM 5.0 distribution has TWO explicitly named inner ZIPs.
    // This is not recursive arbitrary extraction. Schemas/Office documents remain inert in originals.
    private static async Task<AviationRetainedFile[]> VerifySaaArchive(string zip, string expanded, long maximumBytes, CancellationToken token, Func<bool>? canContinue)
    {
        StorageManager.ValidateLocalPath(zip); StorageManager.ValidateLocalPath(expanded);
        if (File.GetAttributes(zip).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Linked FAA archive is unsupported.");
        Directory.CreateDirectory(expanded); var retained = new List<AviationRetainedFile>(); long expandedBytes = 0; var entries = 0;
        async Task VerifyLevel(string path, int level)
        {
            using var archive = ZipFile.OpenRead(path);
            var limit = level == 0 ? 8 : level == 1 ? 128 : 2000;
            if (archive.Entries.Count is < 1 || archive.Entries.Count > limit) throw new InvalidDataException("FAA SAA archive entry bound exceeded.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); string? child = null; var xmls = 0;
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested(); var name = entry.FullName;
                if (++entries > 2200 || name.Length > 300 || name.StartsWith('/') || name.Contains('\\') || name.Contains(':') || name.Split('/').Any(p => p is "." or "..") ||
                    name.Any(char.IsControl) || !names.Add(name) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Unsafe SAA archive member/path/link.");
                if (name.EndsWith('/') && entry.Length == 0) continue;
                if (entry.Length < 0 || entry.Length > maximumBytes - expandedBytes || entry.Length > Math.Max(1048576, entry.CompressedLength * 200))
                    throw new InvalidDataException("SAA expanded/ratio allowance exceeded.");
                expandedBytes += entry.Length;
                var nested = level == 0 && name == "SAA-AIXM_5_Schema/SaaSubscriberFile.zip" || level == 1 && name == "Saa_Sub_File.zip";
                var extension = Path.GetExtension(name).ToLowerInvariant();
                if (nested ? entry.Length > 16777216 : level == 2 ? extension != ".xml" || entry.Length > 1048576 : extension is not (".xsd" or ".xml" or ".xls" or ".doc" or ".xsdconfig" or ".txt") || entry.Length > 8388608)
                    throw new InvalidDataException("Unsupported SAA nested distribution or oversized companion.");
                string? destination = nested ? Path.Combine(expanded, Path.GetFileName(name)) : null;
                if (nested && child is not null) throw new InvalidDataException("Conflicting SAA nested archives.");
                await using (var input = entry.Open())
                await using (var output = destination is null ? Stream.Null : new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    var bytes = new byte[65536]; long count = 0; uint crc = 0xFFFFFFFF; int n;
                    while ((n = await input.ReadAsync(bytes, token)) != 0)
                    {
                        if (canContinue?.Invoke() == false) throw new OperationCanceledException("FAA SAA verification yielded to its stored control/flight-priority policy.");
                        if (n > entry.Length - count) throw new InvalidDataException("SAA original expanded beyond its declared length.");
                        count += n; for (var i = 0; i < n; i++) crc = (crc >> 8) ^ CrcTable[(crc ^ bytes[i]) & 255];
                        await output.WriteAsync(bytes.AsMemory(0, n), token);
                    }
                    if (count != entry.Length || ~crc != entry.Crc32) throw new InvalidDataException("Incomplete/corrupt FAA SAA original member.");
                    if (output is FileStream file) file.Flush(true);
                }
                if (destination is not null) { child = destination; retained.Add(new(Path.GetFileName(destination), entry.Length, await FileHash(destination, token))); }
                if (level == 2) xmls++;
            }
            if (level < 2)
            {
                if (child is null) throw new InvalidDataException("Required official SAA nested companion is missing.");
                await VerifyLevel(child, level + 1);
            }
            else if (xmls == 0) throw new InvalidDataException("No actual SAA data XML was found.");
        }
        await VerifyLevel(zip, 0); return retained.ToArray();
    }
}
