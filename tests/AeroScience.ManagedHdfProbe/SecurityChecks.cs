using PureHDF;
using System.Security.Cryptography;
using System.Text.Json;

// Adversarial source-policy checks. No production importer or genuine NASA claim.
internal static class SecurityChecks
{
    public static object Run(string folder)
    {
        var root = Path.Combine(folder, "security-fixtures");
        using var provenance = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "provenance.json")));
        foreach (var entry in provenance.RootElement.GetProperty("files").EnumerateArray())
        {
            var name = entry.GetProperty("file").GetString()!;
            if (Path.GetFileName(name) != name) throw new IOException("Unsafe security fixture name.");
            using var input = File.OpenRead(Path.Combine(root, name));
            if (input.Length != entry.GetProperty("bytes").GetInt64() || Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant() != entry.GetProperty("sha256").GetString()) throw new IOException("Adversarial fixture hash changed.");
        }
        var checks = 0;
        void Reject(string name, string expected)
        {
            try
            {
                using var file = H5File.OpenRead(Path.Combine(root, name));
                _ = file.Dataset("unsafe");
            }
            catch (NotSupportedException error) when (error.Message == expected) { checks++; return; }
            throw new InvalidOperationException("Pre-resolution policy did not reject " + name);
        }
        foreach (var name in new[] { "external-traversal.nc", "external-absolute.nc", "external-windows.nc", "external-unc.nc", "external-url.nc" }) Reject(name, "AeroScience rejects external HDF links before resolution.");
        Reject("soft-cycle.nc", "AeroScience rejects symbolic HDF links before resolution.");
        Reject("external-raw.nc", "AeroScience rejects external HDF raw storage before resolution.");
        Reject("virtual.nc", "AeroScience rejects HDF virtual storage before resolution.");
        try
        {
            using var reader = new EmitWindowReader(Path.Combine(root, "previous-canary-reflectance.nc"), Path.Combine(root, "previous-canary-uncertainty.nc"), Path.Combine(root, "previous-canary-mask.nc"));
            _ = reader.ReadSpectrum(1, 0);
            throw new InvalidOperationException("Previously failing outside-file canary was accepted.");
        }
        catch (NotSupportedException error) when (error.Message == "AeroScience rejects external HDF links before resolution.") { checks++; }
        // Use the same positive-control original with cut-off metadata. A short input
        // must fail; a different filesystem error cannot masquerade as link rejection.
        var temporary = Path.Combine(Path.GetTempPath(), "aeroscience-hdf-short-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var original = File.ReadAllBytes(Path.Combine(folder, "synthetic-netcdf4.nc"));
            foreach (var length in new[] { 9, 15, 31, 63 })
            {
                var path = Path.Combine(temporary, length + ".nc"); File.WriteAllBytes(path, original[..length]);
                var rejected = false;
                try { using var file = H5File.OpenRead(path); _ = file.Dataset("reflectance"); }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { rejected = true; }
                if (!rejected) throw new InvalidDataException("Truncated scientific metadata was accepted.");
                // The failure must release its input handle immediately; do not rely
                // on GC/finalizers or suppress a Windows locked-file failure.
                using(var exclusive=File.Open(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){}
                checks++;
            }
        }
        finally { Directory.Delete(temporary, true); }
        var assembly = typeof(H5File).Assembly.GetName().Name;
        if (assembly != "AeroScience.Hdf.SafeRead") throw new InvalidOperationException("Unpatched dependency substituted for restricted source.");
        return new { evidenceClass = "Synthetic adversarial fixtures / actual restricted reader process", checksPassed = checks, assembly, externalCanaryObserved = false,
            productionImporterApproved = false, pending = "Memory/decompression process isolation and genuine NASA-format integration are separate gates." };
    }
}
