using System.Net;

namespace AeroScience.Engine;

public sealed class EngineOptions
{
    public string Telemetry { get; set; } = "SimConnect";
    public bool RequireExistingDatabase { get; set; }
    public string? ScienceLibraryDirectory { get; set; }
    public int Port { get; set; } = 5080;
    public int SamplingIntervalMs { get; set; } = 1000;
    public bool EnableLan { get; set; }
    public bool EnableToolbarBridge { get; set; } = true;
    public string? LanAddress { get; set; }
    public string DataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AeroScience");

    public void Validate()
    {
        if (Telemetry != "SimConnect") throw new InvalidOperationException("Production AeroScience uses live SimConnect telemetry only.");
        if (Port is < 1024 or > 65535) throw new InvalidOperationException("AeroScience:Port must be 1024–65535.");
        if (SamplingIntervalMs is < 250 or > 60000) throw new InvalidOperationException("Sampling interval must be 250–60000 ms.");
        if (string.IsNullOrWhiteSpace(DataDirectory)) throw new InvalidOperationException("A data directory is required.");
        if (EnableLan && (!IPAddress.TryParse(LanAddress, out var address) || !IsPrivateV4(address)))
            throw new InvalidOperationException("LAN access requires AeroScience:LanAddress set to this PC's private IPv4 address (10.x, 172.16–31.x, or 192.168.x).");
    }

    public static bool IsPrivateV4(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168));
    }
}
