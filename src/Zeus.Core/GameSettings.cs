using System.Text.Json;

namespace Zeus.Core;

/// <summary>In-game toggles saved with /keep and restored on the next start.</summary>
public sealed record GameSettings(bool Reme = false, bool Qq = false, bool PingTag = false, bool AntiGravity = false, bool Path = false)
{
    public static GameSettings Load(string? path)
    {
        if (path == null || !File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<GameSettings>(File.ReadAllText(path)) ?? new(); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}
