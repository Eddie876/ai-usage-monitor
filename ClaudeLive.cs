using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AiUsageWidget;

// Reads the exact Claude Desktop store for the current Windows user. The token is
// used only for the two fixed Anthropic endpoints, and is never persisted or logged.
internal static class ClaudeLive
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(20) };
    private static DateTimeOffset retryAfter = DateTimeOffset.MinValue;
    public static string LastFailure { get; private set; } = "尚未檢查即時額度。";
    private const int MaxFile = 4 * 1024 * 1024;
    internal record TokenEntry(string Account, string Organization, string Token, long ExpiresAt);

    internal static string? FindDataDirectory(string roaming, string local)
    {
        var dirs = new List<string> { Path.Combine(local, "Claude-Data", "claude_pzs8sxrjxfjjc"), Path.Combine(roaming, "Claude") };
        var packages = Path.Combine(local, "Packages");
        if (Directory.Exists(packages))
            dirs.AddRange(Directory.EnumerateDirectories(packages, "Claude_*").Select(p => Path.Combine(p, "LocalCache", "Roaming", "Claude")));
        return dirs.Where(dir => new[] { "config.json", "Local State", "plan-usage-history.json" }.All(name => File.Exists(Path.Combine(dir, name))))
            .OrderByDescending(dir => File.GetLastWriteTimeUtc(Path.Combine(dir, "plan-usage-history.json"))).FirstOrDefault();
    }

    internal static string DiagnosticStatus()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = FindDataDirectory(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), local);
        var files = new[] { "config.json", "Local State", "plan-usage-history.json" };
        return $"{(string.IsNullOrEmpty(LastFailure) ? "Claude 即時額度已連線。" : LastFailure)}\n程式：{Environment.ProcessPath}\n帳號：{Environment.UserDomainName}\\{Environment.UserName}\nLocalApplicationData：{local}\nClaude 資料目錄：{dir ?? "找不到完整資料目錄"}\n" +
            string.Join("\n", files.Select(name => $"{name}：{(dir is not null && File.Exists(Path.Combine(dir, name)) ? "存在" : "不存在或無法存取")}"));
    }

    public static async Task<Usage?> Fetch(CancellationToken cancellation)
    {
        if (DateTimeOffset.UtcNow < retryAfter) return Unavailable("Claude 查詢暫時受到限流，稍後再試。");
        byte[]? plain = null; byte[]? key = null;
        try
        {
            var dir = FindDataDirectory(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            if (dir is null) return Unavailable("找不到完整的 Claude Desktop 登入及用量紀錄。");
            var configPath = Path.Combine(dir, "config.json");
            var statePath = Path.Combine(dir, "Local State");
            var historyPath = Path.Combine(dir, "plan-usage-history.json");
            if (!File.Exists(configPath) || !File.Exists(statePath) || !File.Exists(historyPath)) return Unavailable("找不到 Claude Desktop 登入或用量紀錄。");
            var configBytes = ReadBounded(configPath); var configDigest = SHA256.HashData(configBytes);
            using var config = JsonDocument.Parse(configBytes);
            var account = GetString(config.RootElement, "lastKnownAccountUuid");
            if (!Guid.TryParse(account, out _)) return Unavailable("無法辨識 Claude Desktop 的目前帳號。");
            var org = LatestOrganization(ReadBounded(historyPath));
            if (!Guid.TryParse(org, out _)) return Unavailable("桌面用量紀錄沒有可核對的組織。");
            var cipherText = GetString(config.RootElement, "oauth:tokenCacheV2");
            if (string.IsNullOrWhiteSpace(cipherText)) return Unavailable("Claude Desktop 沒有可用的 OAuth 登入紀錄。");
            var encrypted = Convert.FromBase64String(cipherText);
            using var state = JsonDocument.Parse(ReadBounded(statePath));
            var wrappedString = GetString(state.RootElement.GetProperty("os_crypt"), "encrypted_key");
            if (wrappedString is null) return Unavailable("Claude Desktop 沒有可用的 Windows 保護金鑰。");
            var wrapped = Convert.FromBase64String(wrappedString);
            if (wrapped.Length < 6 || !wrapped.AsSpan(0, 5).SequenceEqual("DPAPI"u8)) return Unavailable("Claude Desktop 的登入保護格式不支援。");
            key = Unprotect(wrapped.AsSpan(5).ToArray());
            plain = DecryptTokenCache(encrypted, key);
            var entry = SelectToken(plain, account!, org!, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (entry is null) return Unavailable("找不到符合目前帳號、組織及用量權限的有效登入資料。");
            using var profile = await GetJson("https://api.anthropic.com/api/oauth/profile", entry.Token, cancellation);
            if (profile is null) return null;
            if (!ProfileMatches(profile.RootElement, account!, org!)) return Unavailable("Claude 回報的帳號或組織與桌面紀錄不符。");
            using var usage = await GetJson("https://api.anthropic.com/api/oauth/usage", entry.Token, cancellation);
            if (usage is null) return null;
            var limits = ParseWindows(usage.RootElement);
            if (limits.Count == 0) return Unavailable("Claude 未回報可顯示的額度資料。");
            // Fail closed if the local account, selected organization or token changed while querying.
            if (!CryptographicOperations.FixedTimeEquals(configDigest, SHA256.HashData(ReadBounded(configPath))) ||
                !string.Equals(org, LatestOrganization(ReadBounded(historyPath)), StringComparison.OrdinalIgnoreCase)) return Unavailable("查詢期間 Claude 帳號或組織發生變更。");
            LastFailure = "";
            return new Usage("Claude", null, limits, DateTimeOffset.UtcNow, "來源：Claude Desktop 帳號用量；組織已與桌面紀錄核對。 ");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException or CryptographicException or InvalidOperationException or KeyNotFoundException or ArgumentException or HttpRequestException or OperationCanceledException or ExternalException)
        { return Unavailable("Claude 登入資料無法讀取或即時查詢失敗。"); }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }
    private static Usage? Unavailable(string reason) { LastFailure = reason; return null; }
    private static byte[] ReadBounded(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > MaxFile) throw new IOException("File too large");
        using var buffer = new MemoryStream();
        var chunk = new byte[8192]; int read;
        while ((read = file.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaxFile) throw new IOException("File too large");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
    internal static string? LatestOrganization(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        if (!doc.RootElement.TryGetProperty("samples", out var samples) || samples.ValueKind != JsonValueKind.Array) return null;
        long latest = long.MinValue; string? org = null;
        foreach (var sample in samples.EnumerateArray())
        {
            if (sample.ValueKind != JsonValueKind.Object || !sample.TryGetProperty("t", out var time) || time.ValueKind != JsonValueKind.Number || !time.TryGetInt64(out var t) || t < latest) continue;
            latest = t; org = GetString(sample, "org");
        }
        return org;
    }
    internal static TokenEntry? SelectToken(byte[] bytes, string account, string org, long nowMs)
    {
        using var doc = JsonDocument.Parse(bytes);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        var candidates = new List<TokenEntry>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var name = prop.Name; var boundAccount = account;
            if (name.StartsWith("acct:", StringComparison.OrdinalIgnoreCase))
            {
                var split = name.IndexOf('|'); if (split < 0) continue;
                boundAccount = name.Substring(5, split - 5); name = name[(split + 1)..];
            }
            var parts = name.Split(':');
            // Desktop releases have used both organization:client and client:organization.
            // Accept the expected organization in either position, then verify it again
            // against the service's profile response before displaying any data.
            if (parts.Length < 3 || !string.Equals(boundAccount, account, StringComparison.OrdinalIgnoreCase) ||
                !(string.Equals(parts[0], org, StringComparison.OrdinalIgnoreCase) || string.Equals(parts[1], org, StringComparison.OrdinalIgnoreCase)) ||
                !name.Contains("https://api.anthropic.com", StringComparison.OrdinalIgnoreCase) ||
                !name.Contains("user:profile", StringComparison.OrdinalIgnoreCase) || !name.Contains("user:inference", StringComparison.OrdinalIgnoreCase)) continue;
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            var token = GetString(prop.Value, "token");
            if (string.IsNullOrWhiteSpace(token) || !prop.Value.TryGetProperty("expiresAt", out var expiry) || expiry.ValueKind != JsonValueKind.Number || !expiry.TryGetInt64(out var until) || until <= nowMs) continue;
            candidates.Add(new(account, org, token, until));
        }
        return candidates.OrderByDescending(x => x.ExpiresAt).FirstOrDefault();
    }
    internal static List<Limit> ParseWindows(JsonElement root)
    {
        var limits = new List<Limit>();
        foreach (var (key, label) in new[] { ("five_hour", "目前時段"), ("seven_day", "本週") })
        {
            if (!root.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object || !window.TryGetProperty("utilization", out var n) || n.ValueKind != JsonValueKind.Number || !n.TryGetDouble(out var used) || !double.IsFinite(used) || used < 0 || used > 100) continue;
            DateTimeOffset? reset = DateTimeOffset.TryParse(GetString(window, "resets_at"), out var date) ? date : null;
            limits.Add(new(label, used, reset));
        }
        return limits;
    }
    internal static bool ProfileMatches(JsonElement root, string account, string org)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        var accountId = root.TryGetProperty("account", out var a) && a.ValueKind == JsonValueKind.Object ? GetString(a, "uuid") : null;
        var orgId = root.TryGetProperty("organization", out var o) && o.ValueKind == JsonValueKind.Object ? GetString(o, "uuid") : null;
        accountId ??= GetString(root, "accountUuid") ?? GetString(root, "account_uuid");
        orgId ??= GetString(root, "organizationUuid") ?? GetString(root, "organization_uuid");
        return string.Equals(accountId, account, StringComparison.OrdinalIgnoreCase) && string.Equals(orgId, org, StringComparison.OrdinalIgnoreCase);
    }
    private static string? GetString(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
    private static async Task<JsonDocument?> GetJson(string url, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        request.Headers.UserAgent.ParseAdd("claude-code/2.1.80");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retry = response.Headers.RetryAfter;
            retryAfter = retry?.Date ?? (DateTimeOffset.UtcNow + (retry?.Delta ?? TimeSpan.FromMinutes(4)));
            if (retryAfter < DateTimeOffset.UtcNow.AddMinutes(1)) retryAfter = DateTimeOffset.UtcNow.AddMinutes(1);
        }
        if (!response.IsSuccessStatusCode) { LastFailure = $"Claude 即時查詢失敗（HTTP {(int)response.StatusCode}）。"; return null; }
        if (response.Content.Headers.ContentLength > 1024 * 1024) { LastFailure = "Claude 回應過大。"; return null; }
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var dest = new MemoryStream(); var block = new byte[8192]; int n;
        while ((n = await source.ReadAsync(block, ct)) > 0) { if (dest.Length + n > 1024 * 1024) { LastFailure = "Claude 回應過大。"; return null; } dest.Write(block, 0, n); }
        return JsonDocument.Parse(dest.ToArray());
    }
    internal static byte[] DecryptTokenCache(byte[] input, byte[] key)
    {
        if (input.Length < 31 || key.Length != 32 || !(input.AsSpan(0, 3).SequenceEqual("v10"u8) || input.AsSpan(0, 3).SequenceEqual("v11"u8))) throw new CryptographicException();
        var data = new byte[input.Length - 31];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(input.AsSpan(3, 12), input.AsSpan(15, data.Length), input.AsSpan(input.Length - 16), data);
        return data;
    }
    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr ptr);
    private static byte[] Unprotect(byte[] input)
    {
        var blob = new DataBlob { Length = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        try
        {
            Marshal.Copy(input, 0, blob.Data, input.Length);
            if (!CryptUnprotectData(ref blob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out var output)) throw new CryptographicException();
            try { if (output.Length != 32) throw new CryptographicException(); var bytes = new byte[output.Length]; Marshal.Copy(output.Data, bytes, 0, bytes.Length); return bytes; }
            finally { if (output.Data != IntPtr.Zero) { for (int i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0); LocalFree(output.Data); } }
        }
        finally { Marshal.FreeHGlobal(blob.Data); CryptographicOperations.ZeroMemory(input); }
    }
}
