using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Troubleshootr.Collectors.Plannr;

/// <summary>
/// The link Plannr shows under Settings → Sync & devices → Troubleshootr:
/// https://&lt;server&gt;/#troubleshootr=&lt;space&gt;.&lt;secret&gt;.&lt;public key&gt; (base64url). It only lets this tool add reports
/// to that Plannr's inbox and read its open ticket numbers; Plannr's other data stays sealed with a key it never gives out.
/// </summary>
public sealed class PlannrLink
{
    public string Server { get; }
    public string Space { get; }
    public byte[] Secret { get; }
    public byte[] PublicKey { get; }

    private PlannrLink(string server, string space, byte[] secret, byte[] publicKey) => (Server, Space, Secret, PublicKey) = (server, space, secret, publicKey);

    public static PlannrLink? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // https only; plain http just for a sync service running on this computer (testing).
        var m = System.Text.RegularExpressions.Regex.Match(text!.Trim(), @"^(https://[^\s#]+?|http://(?:127\.0\.0\.1|localhost)(?::\d+)?)/*#troubleshootr=([A-Za-z0-9_-]{8,128})\.([A-Za-z0-9_-]{43})\.([A-Za-z0-9_-]{87})$");
        if (!m.Success) return null;
        try
        {
            var secret = DropCrypto.FromB64Url(m.Groups[3].Value);
            var pub = DropCrypto.FromB64Url(m.Groups[4].Value);
            if (secret.Length != 32 || pub.Length != 65 || pub[0] != 4) return null;
            DropCrypto.CheckPublicKey(pub);  // a damaged key isn't a point on the curve: reject it here, not when sending
            return new PlannrLink(m.Groups[1].Value, m.Groups[2].Value, secret, pub);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or PlatformNotSupportedException) { return null; }
    }

    /// <summary>What the inbox checks (the server keeps only its SHA-256).</summary>
    public string Token => DropCrypto.Hex(DropCrypto.Sha256(Encoding.UTF8.GetBytes("plannr-drop-token"), Secret));
}

/// <summary>
/// The same bytes as Plannr's shared/drop-crypto.ts. .NET Framework has no AES-GCM, so both sides use AES-256-CBC with an
/// HMAC-SHA256 over everything before it; the report key comes from ECDH P-256 with a fresh key per report.
/// </summary>
public static class DropCrypto
{
    public static byte[] SealReport(byte[] publicKey, byte[] plain)
    {
        using var peer = Ecdh.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = publicKey.Skip(1).Take(32).ToArray(), Y = publicKey.Skip(33).Take(32).ToArray() },
        });
        using var eph = Ecdh.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256 }, generate: true);
        var q = eph.Export().Q;
        // SHA-256(label ‖ Z) where Z is the ECDH x-coordinate: what Web Crypto's deriveBits + SHA-256 gives on Plannr's side.
        var enc = eph.DeriveFromHash(peer, Encoding.UTF8.GetBytes("plannr-drop-enc"));
        var mac = eph.DeriveFromHash(peer, Encoding.UTF8.GetBytes("plannr-drop-mac"));
        var prefix = new byte[66];
        prefix[0] = 1;
        prefix[1] = 4;
        Buffer.BlockCopy(Pad32(q.X!), 0, prefix, 2, 32);
        Buffer.BlockCopy(Pad32(q.Y!), 0, prefix, 34, 32);
        return Seal(prefix, enc, mac, plain);
    }

    /// <summary>Throws CryptographicException (or PlatformNotSupportedException) unless this is a valid P-256 public key.</summary>
    public static void CheckPublicKey(byte[] publicKey)
    {
        using var _ = Ecdh.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = publicKey.Skip(1).Take(32).ToArray(), Y = publicKey.Skip(33).Take(32).ToArray() },
        });
    }

    public static byte[] SealInfo(byte[] secret, byte[] plain) =>
        Seal([2], Sha256(Encoding.UTF8.GetBytes("plannr-drop-info-enc"), secret), Sha256(Encoding.UTF8.GetBytes("plannr-drop-info-mac"), secret), plain);

    public static byte[] OpenInfo(byte[] secret, byte[] sealed_)
    {
        if (sealed_.Length < 1 + 16 + 16 + 32 || sealed_[0] != 2) throw new CryptographicException("not a Plannr ticket list");
        return Open(sealed_, 1, Sha256(Encoding.UTF8.GetBytes("plannr-drop-info-enc"), secret), Sha256(Encoding.UTF8.GetBytes("plannr-drop-info-mac"), secret));
    }

    private static byte[] Seal(byte[] prefix, byte[] encKey, byte[] macKey, byte[] plain)
    {
        using var aes = Aes.Create();
        aes.Key = encKey;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.GenerateIV();
        byte[] ct;
        using (var e = aes.CreateEncryptor()) ct = e.TransformFinalBlock(plain, 0, plain.Length);
        var body = prefix.Concat(aes.IV).Concat(ct).ToArray();
        using var hmac = new HMACSHA256(macKey);
        return body.Concat(hmac.ComputeHash(body)).ToArray();
    }

    private static byte[] Open(byte[] sealed_, int prefixLength, byte[] encKey, byte[] macKey)
    {
        var body = sealed_.Take(sealed_.Length - 32).ToArray();
        using (var hmac = new HMACSHA256(macKey))
        {
            var expected = hmac.ComputeHash(body);
            var diff = 0;
            for (var i = 0; i < 32; i++) diff |= expected[i] ^ sealed_[sealed_.Length - 32 + i];
            if (diff != 0) throw new CryptographicException("the ticket list doesn't match this link");
        }
        using var aes = Aes.Create();
        aes.Key = encKey;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.IV = body.Skip(prefixLength).Take(16).ToArray();
        using var d = aes.CreateDecryptor();
        return d.TransformFinalBlock(body, prefixLength + 16, body.Length - prefixLength - 16);
    }

    public static byte[] Gzip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    public static byte[] Gunzip(byte[] data)
    {
        using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }

    public static byte[] Sha256(params byte[][] parts)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(parts.SelectMany(p => p).ToArray());
    }

    public static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));

    public static byte[] FromB64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
    }

    private static byte[] Pad32(byte[] b) => b.Length >= 32 ? b : new byte[32 - b.Length].Concat(b).ToArray();

    /// <summary>
    /// ECDiffieHellman through reflection: netstandard2.0 (what this library compiles against) leaves it out, but both
    /// runtimes the app and tests run on have it, in the same assembly as ECDsa (System.Core on .NET Framework 4.7.2).
    /// </summary>
    private sealed class Ecdh : IDisposable
    {
        private static readonly Type Type = typeof(ECDsa).Assembly.GetType("System.Security.Cryptography.ECDiffieHellman")
            ?? throw new PlatformNotSupportedException("ECDH isn't available on this Windows");
        private readonly object _key;

        private Ecdh(object key) => _key = key;

        /// <summary>A key from public parameters, or (generate) a new key pair on the curve.</summary>
        public static Ecdh Create(ECParameters p, bool generate = false)
        {
            var factory = Type.GetMethod("Create", generate ? [typeof(ECCurve)] : [typeof(ECParameters)])
                ?? throw new PlatformNotSupportedException("ECDiffieHellman.Create is missing");
            return new Ecdh(Invoke(factory, null, generate ? p.Curve : p));
        }

        public ECParameters Export() => (ECParameters)Invoke(Type.GetMethod("ExportParameters", [typeof(bool)])!, _key, false);

        public byte[] DeriveFromHash(Ecdh other, byte[] prepend)
        {
            var publicKey = Type.GetProperty("PublicKey")!.GetValue(other._key);
            var derive = Type.GetMethods().First(m => m.Name == "DeriveKeyFromHash" && m.GetParameters().Length == 4);
            return (byte[])Invoke(derive, _key, publicKey!, HashAlgorithmName.SHA256, prepend, null!);
        }

        private static object Invoke(System.Reflection.MethodInfo m, object? target, params object[] args)
        {
            try { return m.Invoke(target, args)!; }
            catch (System.Reflection.TargetInvocationException e) when (e.InnerException is { } inner) { throw inner; }
        }

        public void Dispose() => (_key as IDisposable)?.Dispose();
    }
}

/// <summary>An open ticket as Plannr publishes it for the "add to ticket" list.</summary>
public sealed record PlannrTicket(int Number, string Customer, string Device, string Status, string? Shown = null)
{
    /// <summary>The number as Plannr shows it (its own prefix, e.g. T-0007); older Plannr versions don't send it.</summary>
    public string Name => string.IsNullOrWhiteSpace(Shown) ? $"#{Number}" : Shown!;

    public string Label => $"{Name}  ·  {(string.IsNullOrWhiteSpace(Customer) ? "no customer" : Customer)}{(string.IsNullOrWhiteSpace(Device) ? "" : $"  ·  {Device}")}";
}

/// <summary>Sends reports to a Plannr inbox and reads its open tickets, over the owner's own Plannr sync service.</summary>
public sealed class PlannrDrop : IDisposable
{
    private readonly PlannrLink _link;
    private readonly HttpClient _http;

    public PlannrDrop(PlannrLink link, HttpMessageHandler? handler = null)
    {
        _link = link;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.Add("Authorization", "Bearer " + link.Token);
    }

    private string Url(string action) => $"{_link.Server}/api/s/{_link.Space}/{action}";

    public async Task<List<PlannrTicket>> OpenTicketsAsync(CancellationToken ct)
    {
        var json = await SendAsync(new HttpRequestMessage(HttpMethod.Get, Url("dropinfo")), ct).ConfigureAwait(false);
        var info = JsonNode.Parse(json)?["info"]?.GetValue<string>();
        if (string.IsNullOrEmpty(info)) return [];
        var list = JsonNode.Parse(Encoding.UTF8.GetString(DropCrypto.Gunzip(DropCrypto.OpenInfo(_link.Secret, DropCrypto.FromB64Url(info!)))));
        return (list?["tickets"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(t => new PlannrTicket(t["number"]?.GetValue<int>() ?? 0, t["customer"]?.GetValue<string>() ?? "", t["device"]?.GetValue<string>() ?? "", t["status"]?.GetValue<string>() ?? "", t["label"]?.GetValue<string>()))
            .Where(t => t.Number > 0)
            .OrderByDescending(t => t.Number)
            .ToList();
    }

    /// <summary>Seals the payload to Plannr's public key and posts it to the inbox.</summary>
    public async Task SendReportAsync(JsonObject payload, CancellationToken ct)
    {
        var sealed_ = await Task.Run(() => DropCrypto.SealReport(_link.PublicKey, DropCrypto.Gzip(Encoding.UTF8.GetBytes(payload.ToJsonString()))), ct).ConfigureAwait(false);
        var request = new HttpRequestMessage(HttpMethod.Post, Url("inbox")) { Content = new ByteArrayContent(sealed_) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        await SendAsync(request, ct).ConfigureAwait(false);
    }

    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        {
            HttpResponseMessage response;
            try { response = await _http.SendAsync(request, ct).ConfigureAwait(false); }
            catch (HttpRequestException e) { throw new PlannrException("Couldn't reach the Plannr sync service. Check the internet connection.", e); }
            catch (TaskCanceledException e) when (!ct.IsCancellationRequested) { throw new PlannrException("The Plannr sync service didn't answer in time.", e); }
            using (response)
            {
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return text;
                string? error = null;
                try { error = JsonNode.Parse(text)?["error"]?.GetValue<string>(); }
                catch (JsonException) { }
                throw new PlannrException((int)response.StatusCode switch
                {
                    401 => "Plannr didn't accept this link (it was disconnected or reconnected). Copy the link again from Plannr.",
                    413 => "The report is too big to send.",
                    _ => error ?? $"The Plannr sync service answered {(int)response.StatusCode}.",
                });
            }
        }
    }

    public void Dispose() => _http.Dispose();
}

public sealed class PlannrException(string message, Exception? inner = null) : Exception(message, inner);
