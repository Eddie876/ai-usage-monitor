using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AiUsageWidget;

internal record Limit(string Label, double Used, DateTimeOffset? Reset);
internal record Usage(string Provider, string? Plan, IReadOnlyList<Limit> Limits, DateTimeOffset? Observed, string? Note, bool Stale = false);

internal static class UsageSources
{
    public static Usage Claude(string? configRoot = null, string? localRoot = null)
    {
        var root = configRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = localRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dirs = new List<string> {
            Path.Combine(root, "Claude"),
            Path.Combine(local, "Claude-Data", "claude_pzs8sxrjxfjjc")
        };
        try
        {
            var packages = Path.Combine(local, "Packages");
            if (Directory.Exists(packages))
                dirs.AddRange(Directory.EnumerateDirectories(packages, "Claude_*").Select(p => Path.Combine(p, "LocalCache", "Roaming", "Claude")));
            var file = dirs.Select(d => Path.Combine(d, "plan-usage-history.json"))
                .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (file is null) return Missing("Claude", "找不到桌面用量紀錄。公司帳號的登入整合尚未完成。");
            if (new FileInfo(file).Length > 4 * 1024 * 1024) return Missing("Claude", "桌面用量紀錄過大，無法讀取。");
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            var samples = doc.RootElement.GetProperty("samples").EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out _) && x.TryGetProperty("u", out _))
                .OrderByDescending(x => x.GetProperty("t").GetInt64());
            var sample = samples.FirstOrDefault();
            if (sample.ValueKind != JsonValueKind.Object) return Missing("Claude", "桌面用量紀錄沒有可用資料。");
            var observed = DateTimeOffset.FromUnixTimeMilliseconds(sample.GetProperty("t").GetInt64());
            var u = sample.GetProperty("u");
            var limits = new List<Limit>();
            AddNumber(u, "fh", "目前時段", limits); AddNumber(u, "sd", "本週", limits);
            return new("Claude", null, limits, observed, "桌面紀錄未提供重置時間，組織帳號尚未核對。", DateTimeOffset.UtcNow - observed > TimeSpan.FromMinutes(30));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentOutOfRangeException or KeyNotFoundException)
        { return Missing("Claude", "無法讀取桌面用量紀錄。"); }
    }
    private static void AddNumber(JsonElement obj, string key, string label, List<Limit> limits)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) && n >= 0 && n <= 100)
            limits.Add(new(label, n, null));
    }

    public static async Task<Usage> Codex(CancellationToken cancellation)
    {
        try
        {
            var bin = await FindCodex(cancellation);
            if (bin is null) return Missing("Codex", "找不到 Codex Desktop 或 Codex CLI。");
            using var p = new Process { StartInfo = new ProcessStartInfo(bin, "app-server") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            if (!p.Start()) return Missing("Codex", "無法啟動 Codex 用量查詢。");
            _ = p.StandardError.ReadToEndAsync(cancellation);
            try
            {
                await p.StandardInput.WriteLineAsync("{\"method\":\"initialize\",\"id\":0,\"params\":{\"clientInfo\":{\"name\":\"ai-usage-widget\",\"version\":\"0.1.0\"}}}");
                await p.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}");
                await p.StandardInput.WriteLineAsync("{\"method\":\"account/rateLimits/read\",\"id\":1}");
                await p.StandardInput.FlushAsync(cancellation);
                while (await p.StandardOutput.ReadLineAsync(cancellation) is { } line)
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out int number) || number != 1) continue;
                    if (!root.TryGetProperty("result", out var result)) return Missing("Codex", "Codex 未回傳額度，請確認桌面版已登入。");
                    JsonElement data;
                    if (result.TryGetProperty("rateLimitsByLimitId", out var buckets) && buckets.ValueKind == JsonValueKind.Object && buckets.TryGetProperty("codex", out var selected)) data = selected;
                    else if (result.TryGetProperty("rateLimits", out var fallback) && fallback.ValueKind == JsonValueKind.Object && (!fallback.TryGetProperty("limitId", out var kind) || kind.ValueKind == JsonValueKind.Null || kind.GetString() == "codex")) data = fallback;
                    else return Missing("Codex", "Codex 未回傳額度資料。");
                    var limits = new List<Limit>();
                    foreach (var key in new[] { "primary", "secondary" })
                    {
                        if (!data.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object || !w.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var pct) || !double.IsFinite(pct) || pct < 0 || pct > 100) continue;
                        long? secs = w.TryGetProperty("resetsAt", out var reset) && reset.ValueKind == JsonValueKind.Number && reset.TryGetInt64(out var timestamp) ? timestamp : null;
                        DateTimeOffset? resetAt = null;
                        try { if (secs.HasValue) resetAt = DateTimeOffset.FromUnixTimeSeconds(secs.Value); } catch (ArgumentOutOfRangeException) { }
                        var label = w.TryGetProperty("windowDurationMins", out var duration) && duration.ValueKind == JsonValueKind.Number && duration.TryGetInt32(out var minutes) && minutes >= 10080 ? "本週" : "目前時段";
                        limits.Add(new(label, pct, resetAt));
                    }
                    string? plan = data.TryGetProperty("planType", out var pl) && pl.ValueKind == JsonValueKind.String ? pl.GetString() : null;
                    return new("Codex", plan, limits, DateTimeOffset.UtcNow, limits.Count == 0 ? "帳號沒有回傳可顯示的額度。" : "來自 Codex 官方 app-server；帳號尚未核對。 ");
                }
                return Missing("Codex", "Codex 查詢未回應，請確認桌面版已登入。");
            }
            finally { if (!p.HasExited) p.Kill(true); }
        }
        catch (OperationCanceledException) { return Missing("Codex", "Codex 查詢逾時，請稍後重試。"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception)
        { return Missing("Codex", "無法啟動或讀取 Codex 用量查詢。"); }
    }
    private static async Task<string?> FindCodex(CancellationToken ct)
    {
        var script = "$p=Get-AppxPackage -Name OpenAI.Codex -ErrorAction SilentlyContinue | Sort-Object Version -Descending | Select-Object -First 1; if($p){[Console]::Out.Write($p.InstallLocation)}";
        using var p = new Process { StartInfo = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        p.StartInfo.ArgumentList.Add("-NoProfile"); p.StartInfo.ArgumentList.Add("-NonInteractive"); p.StartInfo.ArgumentList.Add("-Command"); p.StartInfo.ArgumentList.Add(script);
        if (p.Start())
        {
            var location = (await p.StandardOutput.ReadToEndAsync(ct)).Trim();
            await p.WaitForExitAsync(ct);
            var source = Path.Combine(location, "app", "resources", "codex.exe");
            if (location.Length > 0 && File.Exists(source))
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiUsageWidget", "codex-cache", Path.GetFileName(location));
                Directory.CreateDirectory(dir);
                var target = Path.Combine(dir, "codex.exe");
                var same = false;
                if (File.Exists(target))
                {
                    using var sourceFile = File.OpenRead(source);
                    using var targetFile = File.OpenRead(target);
                    same = CryptographicOperations.FixedTimeEquals(SHA256.HashData(sourceFile), SHA256.HashData(targetFile));
                }
                if (!same) File.Copy(source, target, true);
                return target;
            }
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[] { Path.Combine(home, ".local", "bin", "codex.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "codex", "codex.exe") };
        return candidates.FirstOrDefault(File.Exists);
    }
    public static Usage Copilot(string? localRoot = null)
    {
        var file = Path.Combine(localRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiUsageWidget", "copilot-usage.json");
        try
        {
            if (!File.Exists(file)) return Missing("Copilot", "請安裝 VS Code 連接擴充套件，並執行「AI 額度：連接 VS Code 的 GitHub 帳號」。");
            if (new FileInfo(file).Length > 64 * 1024) return Missing("Copilot", "VS Code 的用量資料過大，無法讀取。");
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1) return Missing("Copilot", "VS Code 用量資料格式無效。");
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String) return Missing("Copilot", error.GetString() ?? "Copilot 連線失敗。");
            if (!root.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(account.GetString()) || !root.TryGetProperty("observedAt", out var observedJson) || observedJson.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(observedJson.GetString(), out var observed) || observed > DateTimeOffset.UtcNow.AddMinutes(1)) return Missing("Copilot", "無法確認 VS Code 帳號或更新時間。");
            var name = account.GetString()!;
            if (name.Length > 200) return Missing("Copilot", "VS Code 帳號資料無效。");
            var stale = DateTimeOffset.UtcNow - observed > TimeSpan.FromMinutes(5);
            var note = $"VS Code GitHub：{name}；請確認是使用 Copilot 的帳號。";
            if (root.TryGetProperty("unlimited", out var unlimited) && unlimited.ValueKind == JsonValueKind.True) return new("Copilot", null, Array.Empty<Limit>(), observed, note + " 此額度無上限。", stale);
            if (!root.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var pct) || !double.IsFinite(pct) || pct < 0 || pct > 100) return Missing("Copilot", "GitHub 未提供可用的 Copilot 用量。");
            DateTimeOffset? reset = null;
            if (root.TryGetProperty("resetAt", out var resetValue) && resetValue.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(resetValue.GetString(), out var resetDate)) reset = resetDate;
            var rawPlan = root.TryGetProperty("plan", out var planValue) && planValue.ValueKind == JsonValueKind.String ? planValue.GetString() : null;
            return new("Copilot", CopilotPlanLabel(rawPlan), new[] { new Limit("本月", pct, reset) }, observed, note, stale);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        { return Missing("Copilot", "無法讀取 VS Code 的 Copilot 用量資料。"); }
    }
    internal static string? CopilotPlanLabel(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "individual" => "Pro",
        "free" => "Free",
        "individual_pro" or "individual_pro_plus" => "Pro+",
        "business" => "Business",
        "enterprise" => "Enterprise",
        "individual_edu" => "Student",
        _ => null
    };
    private static Usage Missing(string provider, string note) => new(provider, null, Array.Empty<Limit>(), null, note);
}
