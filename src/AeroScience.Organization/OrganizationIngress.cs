using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using AeroScience.Shared;
using AeroScience.Missions;

namespace AeroScience.Organization;

// The public listener grants no network identity. It ignores ALL forwarding claims.
// Only this per-process gateway can authenticate a bounded request to the loopback app.
// User authentication, membership, MFA, Origin and antiforgery remain application checks.
public sealed class OrganizationIngress : IDisposable
{
    internal const string ProofHeader = "X-AeroScience-Ingress-Proof";
    static readonly string[] BoundHeaders = ["Cookie", "Authorization", "Origin", "X-AeroScience-Organization-CSRF", "Content-Type", "Accept"];
    static readonly HashSet<string> HopHeaders = new(StringComparer.OrdinalIgnoreCase) { "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host", "Content-Length" };
    readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    readonly Dictionary<string, long> used = new(StringComparer.Ordinal);
    readonly SemaphoreSlim limit = new(32, 32);
    readonly HttpClient client = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, MaxConnectionsPerServer = 32 }) { Timeout = TimeSpan.FromSeconds(30) };
    readonly OrganizationSettings settings;
    public OrganizationIngress(OrganizationSettings settings) { this.settings = settings; }

    static string Canonical(string method, string target, string host, byte[] body, Func<string, string> header) =>
        string.Join('\n', new[] { "AeroScience.local-ingress.v1", method, target, host, Convert.ToHexStringLower(SHA256.HashData(body)) }.Concat(BoundHeaders.Select(h => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(header(h)))))));
    internal string Sign(string canonical, long timestamp, string nonce) => timestamp.ToString(CultureInfo.InvariantCulture) + "." + nonce + "." + Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + "\n" + nonce + "\n" + canonical)));
    internal bool Verify(string proof, string canonical, long now)
    {
        var fields = proof.Split('.');
        if (fields.Length != 3 || !long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp) || timestamp < now - 30 || timestamp > now + 2 || fields[1].Length != 32 || fields[2].Length != 64 || fields.Skip(1).Any(f => f.Any(c => !char.IsAsciiHexDigitLower(c)))) return false;
        var expected = Sign(canonical, timestamp, fields[1]);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(proof))) return false;
        lock (used) {
            foreach (var expired in used.Where(v => v.Value < now - 30).Select(v => v.Key).ToArray()) used.Remove(expired);
            if (used.Count >= 4096 || !used.TryAdd(fields[1], timestamp)) return false;
        }
        return true;
    }
    static bool Forwarded(string header) => header.Equals("Forwarded", StringComparison.OrdinalIgnoreCase) || header.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) || header.StartsWith("X-Railway-", StringComparison.OrdinalIgnoreCase) || header.StartsWith("X-AeroScience-Ingress-", StringComparison.OrdinalIgnoreCase) || header.Equals("X-Real-IP", StringComparison.OrdinalIgnoreCase);
    static async Task<byte[]> ReadBody(HttpContext context)
    {
        var max = context.Request.Method == "POST" && context.Request.Path == OrganizationSubmissionApi.ReceivePath ? SubmissionEvidenceProtocol.MaximumWireBytes : 32768;
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature) feature.MaxRequestBodySize = max;
        if (context.Request.ContentLength > max) throw new BadHttpRequestException("Request too large.", 413);
        using var body = new MemoryStream();
        var buffer = new byte[8192]; int length;
        while ((length = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) != 0) { if (body.Length + length > max) throw new BadHttpRequestException("Request too large.", 413); await body.WriteAsync(buffer.AsMemory(0, length), context.RequestAborted); }
        return body.ToArray();
    }
    public async Task Invoke(HttpContext context, RequestDelegate next)
    {
        var backend = context.Connection.LocalPort == settings.IngressApplicationPort;
        if (!backend && context.Connection.LocalPort != settings.IngressPublicPort) { context.Response.StatusCode = 403; return; }
        if (!backend && !await limit.WaitAsync(0, context.RequestAborted)) { context.Response.StatusCode = 429; return; }
        byte[]? body = null;
        try {
            var probe = context.Request.Method == "GET" && context.Request.Path == "/healthz/ready" && (context.Request.Host.Host == "healthcheck.railway.app" || context.Request.Host.Host == "127.0.0.1" && context.Connection.RemoteIpAddress is {} ip && IPAddress.IsLoopback(ip));
            if (!backend && !probe && !string.Equals(context.Request.Host.Value, settings.Origin.Authority, StringComparison.OrdinalIgnoreCase)) { context.Response.StatusCode = 400; return; }
            if (context.Request.QueryString.Value?.Length > 8192) { context.Response.StatusCode = 414; return; }
            body = await ReadBody(context);
            var target = context.Request.PathBase.ToUriComponent() + context.Request.Path.ToUriComponent() + context.Request.QueryString.ToUriComponent();
            if (backend) {
                var canonical = Canonical(context.Request.Method, target, context.Request.Host.Value??"", body, h => context.Request.Headers[h].ToString());
                if (context.Connection.RemoteIpAddress is not {} remote || !IPAddress.IsLoopback(remote) || !Verify(context.Request.Headers[ProofHeader].ToString(), canonical, DateTimeOffset.UtcNow.ToUnixTimeSeconds())) { context.Response.StatusCode = 403; return; }
                foreach (var h in context.Request.Headers.Keys.Where(Forwarded).ToArray()) context.Request.Headers.Remove(h);
                // This scheme is a configured public transport contract, NEVER a forwarded user claim.
                context.Request.Scheme = "https"; context.Request.Body = new MemoryStream(body, writable: false);
                await next(context); return;
            }
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), new Uri($"http://127.0.0.1:{settings.IngressApplicationPort}" + target));
            request.Content = new ByteArrayContent(body);
            // Explicit application headers; arbitrary user proxy headers never cross the boundary.
            request.Headers.Host = probe ? "127.0.0.1" : settings.Origin.Authority;
            foreach (var header in BoundHeaders) if (context.Request.Headers.TryGetValue(header, out var values)) {
                if(header is not("Cookie" or "Accept")&&values.Count!=1){context.Response.StatusCode=400;return;}
                var value=string.Join(header=="Cookie"?"; ":",",values.ToArray());
                if (!request.Headers.TryAddWithoutValidation(header, value)) request.Content.Headers.TryAddWithoutValidation(header, value);
            }
            var proof = Sign(Canonical(context.Request.Method, request.RequestUri!.PathAndQuery, request.Headers.Host!, body, h => request.Headers.TryGetValues(h, out var values) || request.Content.Headers.TryGetValues(h, out values) ? string.Join(",", values) : ""), DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));
            request.Headers.Add(ProofHeader, proof);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers.Concat(response.Content.Headers)) if (!HopHeaders.Contains(header.Key) && !Forwarded(header.Key)) context.Response.Headers[header.Key] = header.Value.ToArray();
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        } catch (BadHttpRequestException ex) { context.Response.StatusCode = ex.StatusCode; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { if (context.Response.HasStarted) context.Abort(); else context.Response.StatusCode = 503; }
        finally { if (body is not null) CryptographicOperations.ZeroMemory(body); if (!backend) limit.Release(); }
    }
    public void Dispose() { client.Dispose(); limit.Dispose(); CryptographicOperations.ZeroMemory(key); }
}
