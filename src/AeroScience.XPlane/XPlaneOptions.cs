using System.Net;
using System.Net.Sockets;

namespace AeroScience.XPlane;

public sealed record XPlaneOptions
{
    public string Address { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 8086;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ConfirmationInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaximumConfirmationAge { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaximumSnapshotSpan { get; init; } = TimeSpan.FromMilliseconds(500);
    public int ConfirmationConcurrency { get; init; } = 4;
    public int MaximumMessageBytes { get; init; } = 65536;
    public int DiscoveryPageSize { get; init; } = 64;
    public int MaximumDiscoveryPages { get; init; } = 4;
    public double DiscontinuityToleranceMetres { get; init; } = 1000;

    public void Validate()
    {
        if (!IPAddress.TryParse(Address, out var ip) || !IPAddress.IsLoopback(ip)
            || ip.IsIPv4MappedToIPv6 || ip.ToString() != Address
            || ip.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            throw new ArgumentException("Use a canonical explicit loopback IP address, not a hostname or URL.");
        if (Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(Port));
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromSeconds(30)
            || ShutdownTimeout <= TimeSpan.Zero || ShutdownTimeout > TimeSpan.FromSeconds(3))
            throw new ArgumentException("Connection and shutdown deadlines must be bounded.");
        if (MaximumConfirmationAge <= TimeSpan.Zero || MaximumConfirmationAge > TimeSpan.FromSeconds(2)
            || MaximumSnapshotSpan <= TimeSpan.Zero || MaximumSnapshotSpan > MaximumConfirmationAge
            || ConfirmationInterval < TimeSpan.FromMilliseconds(10) || ConfirmationInterval > MaximumConfirmationAge / 2)
            throw new ArgumentException("Use a confirmation interval within the conservative two-second freshness budget.");
        if (ConfirmationConcurrency is < 1 or > 8 || MaximumMessageBytes is < 512 or > 262144
            || DiscoveryPageSize is < 1 or > 128 || MaximumDiscoveryPages is < 1 or > 8
            || !double.IsFinite(DiscontinuityToleranceMetres) || DiscontinuityToleranceMetres is < 1 or > 100000)
            throw new ArgumentException("Protocol and diagnostic limits must remain bounded.");
    }

    internal Uri Endpoint(string path, bool socket = false) => new UriBuilder(socket ? "ws" : "http", Address, Port, path).Uri;
    internal static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];
}
