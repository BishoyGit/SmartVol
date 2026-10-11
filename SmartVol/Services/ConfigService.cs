using System.IO;
using System.Text.Json;

namespace SmartVol.Services;

public sealed class AppConfig
{
    public List<string> HiddenDevices { get; set; } = new();
    public double Step { get; set; } = 0.05;
    public int OverlayDurationMs { get; set; } = 1300;
    public double OverlayOpacity { get; set; } = 0.92;
    public bool RunAtStartup { get; set; } = false;
    public bool HotkeyEitherSideModifiers { get; set; } = false;
    public Dictionary<string, HotkeyBinding> Hotkeys { get; set; } = HotkeyBindings.DefaultConfig();

    public void Normalize()
    {
        Step = Math.Clamp(Step, 0.01, 0.25);
        OverlayDurationMs = Math.Clamp(OverlayDurationMs, 400, 5000);
        OverlayOpacity = Math.Clamp(OverlayOpacity, 0.4, 1.0);
        HiddenDevices = HiddenDevices
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Backward compatible: files saved before custom hotkeys existed
        // simply miss this section and keep the classic defaults.
        var defaults = HotkeyBindings.DefaultConfig();
        Hotkeys ??= new Dictionary<string, HotkeyBinding>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, fallback) in defaults)
        {
            if (!Hotkeys.TryGetValue(name, out var binding) ||
                binding == null || binding.Key == 0 ||
                HotkeyBindings.IsModifierVk(binding.Key))
            {
                Hotkeys[name] = fallback;
            }
            else
            {
                binding.Modifiers = binding.Modifiers
                    .Where(HotkeyBindings.IsModifierVk)
                    .Distinct()
                    .OrderBy(HotkeyBindings.ModifierSortOrder)
                    .ToList();
            }
        }
    }
}

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string[] _paths;

    public ConfigService()
    {
        var paths = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "settings.json"),
        };
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
            paths.Add(Path.Combine(appData, "SmartVol", "settings.json"));
        _paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public AppConfig Load()
    {
        foreach (string path in _paths)
        {
            try
            {
                if (!File.Exists(path))
                    continue;
                var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), _jsonOptions);
                if (config == null)
                    continue;
                config.Normalize();
                return config;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Failed to load config from {path}", ex);
            }
        }

        return new AppConfig();
    }

    public bool Save(AppConfig config)
    {
        config.Normalize();
        string json;
        try
        {
            json = JsonSerializer.Serialize(config, _jsonOptions);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to serialize config", ex);
            return false;
        }

        bool saved = false;
        foreach (string path in _paths)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json);
                saved = true;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Failed to save config to {path}", ex);
            }
        }

        if (saved)
            AppLogger.Info("Saved SmartVol settings");
        return saved;
    }
}
