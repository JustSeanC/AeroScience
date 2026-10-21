using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace AeroScience.Engine;

public sealed record PairRequest(string? Code);
public sealed record AccessSession(string Token, string Csrf, DateTimeOffset ExpiresUtc);

public sealed class AccessControl(string? cookieName = null)
{
    private readonly ConcurrentDictionary<string, AccessSession> sessions = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset Start, int Count)> attempts = new();
    private readonly object attemptGate = new();
    public string PairingCode { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
    public const string CookieName = "AeroScience.Session";
    public string SessionCookieName { get; } = cookieName ?? ApplicationProfile.Current.SessionCookieName;

    public bool AllowAttempt(string address)
    {
        lock (attemptGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (attempts.Count > 1024)
                foreach (var item in attempts.Where(x => now - x.Value.Start >= TimeSpan.FromMinutes(1))) attempts.TryRemove(item.Key, out _);
            if (!attempts.TryGetValue(address, out var attempt) || now - attempt.Start >= TimeSpan.FromMinutes(1)) attempt = (now, 0);
            if (attempt.Count >= 5 || attempts.Count >= 1024 && !attempts.ContainsKey(address)) return false;
            attempts[address] = (attempt.Start, attempt.Count + 1);
            return true;
        }
    }

    public AccessSession? Pair(bool loopback, string? code)
    {
        if (!loopback && (code is null || !Equal(code.Replace(" ", "").ToUpperInvariant(), PairingCode))) return null;
        foreach (var item in sessions.Where(x => x.Value.ExpiresUtc <= DateTimeOffset.UtcNow)) sessions.TryRemove(item.Key, out _);
        if (sessions.Count >= 128) return null;
        var session = new AccessSession(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), DateTimeOffset.UtcNow.AddHours(12));
        sessions[session.Token] = session;
        return session;
    }

    public AccessSession? Get(HttpRequest request) => request.Cookies.TryGetValue(SessionCookieName, out var token)
        && sessions.TryGetValue(token, out var session) && session.ExpiresUtc > DateTimeOffset.UtcNow ? session : null;
    public static bool Equal(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    public static bool IsLoopback(HttpContext context) => context.Connection.RemoteIpAddress is { } address
        ? IPAddress.IsLoopback(address) : context.RequestServices.GetRequiredService<IHostEnvironment>().IsEnvironment("Testing");

    public static bool ValidHost(HttpRequest request, EngineOptions options)
    {
        var host = request.Host.Host.Trim('[', ']');
        return (request.Host.Port ?? 80) == options.Port && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host is "127.0.0.1" or "::1" || options.EnableLan && host == options.LanAddress);
    }

    public static bool ValidOrigin(HttpRequest request) => Uri.TryCreate(request.Headers.Origin.ToString(), UriKind.Absolute, out var origin)
        && origin.Scheme == request.Scheme && origin.Authority.Equals(request.Host.Value, StringComparison.OrdinalIgnoreCase)
        && origin.AbsolutePath == "/" && string.IsNullOrEmpty(origin.Query);
}
