using System;
using System.IO;
using System.Text.Json;

namespace AiUsageWidget;

internal enum MonitorSource { Claude, Codex, Copilot }

internal sealed record SourceSettings(bool Claude = true, bool Codex = true, bool Copilot = true)
{
    public bool AnyEnabled => Claude || Codex || Copilot;

    public bool IsEnabled(MonitorSource source) => source switch
    {
        MonitorSource.Claude => Claude,
        MonitorSource.Codex => Codex,
        MonitorSource.Copilot => Copilot,
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };

    public SourceSettings WithEnabled(MonitorSource source, bool enabled) => source switch
    {
        MonitorSource.Claude => this with { Claude = enabled },
        MonitorSource.Codex => this with { Codex = enabled },
        MonitorSource.Copilot => this with { Copilot = enabled },
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };

    public static SourceSettings Load(string file)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
                return new(ReadEnabled(root, "claude"), ReadEnabled(root, "codex"), ReadEnabled(root, "copilot"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return new();
    }

    private static bool ReadEnabled(JsonElement root, string key) =>
        !root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.False;

    public bool TrySave(string file)
    {
        var directory = Path.GetDirectoryName(file)!;
        var temporaryFile = Path.Combine(directory, Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporaryFile, JsonSerializer.Serialize(new { claude = Claude, codex = Codex, copilot = Copilot }));
            File.Move(temporaryFile, file, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporaryFile); }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }
}
