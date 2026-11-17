using System.IO;
using System.Text.Json;
using System.Windows.Media;
using RouteDeck.Core.Models;
using RouteDeck.Core.Serialization;

namespace RouteDeck.App.Services;

/// <summary>Per-user UI preferences in %LocalAppData%\RouteDeck\ui.json.</summary>
public sealed class UiSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RouteDeck", "ui.json");

    public HashSet<string> DismissedFindings { get; set; } = [];

    public bool ShowSystemRoutes { get; set; }

    public static UiSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath), RouteDeckJson.Options) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, RouteDeckJson.Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preferences are best effort.
        }
    }
}

/// <summary>Stable per-adapter colors, derived from the interface GUID so they survive restarts.</summary>
public static class AdapterPalette
{
    private static readonly Brush[] Colors =
    [
        Brush("#378ADD"), Brush("#1D9E75"), Brush("#D85A30"), Brush("#7F77DD"),
        Brush("#D4537E"), Brush("#BA7517"), Brush("#639922"), Brush("#0F6E56"),
    ];

    public static Brush Disconnected { get; } = Brush("#888780");

    public static Brush For(NetAdapter? adapter)
    {
        if (adapter is null) return Disconnected;
        byte[] bytes = adapter.Guid == Guid.Empty ? BitConverter.GetBytes(adapter.Luid) : adapter.Guid.ToByteArray();
        int hash = bytes.Aggregate(17, (h, b) => h * 31 + b);
        return Colors[(hash & 0x7FFFFFFF) % Colors.Length];
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
