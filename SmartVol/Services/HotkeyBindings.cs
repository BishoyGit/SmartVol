using System.Windows.Input;

namespace SmartVol.Services;

/// <summary>
/// A user-configurable hotkey: required modifier keys (virtual-key codes)
/// plus one main key. Example: Right Ctrl + Up is Modifiers=[0xA3], Key=0x26.
/// </summary>
public sealed class HotkeyBinding
{
    public List<int> Modifiers { get; set; } = new();
    public int Key { get; set; }

    public HotkeyBinding Clone() =>
        new() { Modifiers = new List<int>(Modifiers), Key = Key };
}

/// <summary>
/// Helpers for hotkey bindings: modifier key sets, defaults (identical to the
/// original hard-coded Right Ctrl + Arrows behavior), config mapping and
/// human-readable display text for the settings UI.
/// </summary>
internal static class HotkeyBindings
{
    public const string AppUp = "AppUp";
    public const string AppDown = "AppDown";
    public const string SystemUp = "SystemUp";
    public const string SystemDown = "SystemDown";

    public const int VkLshift = 0xA0;
    public const int VkRshift = 0xA1;
    public const int VkLcontrol = 0xA2;
    public const int VkRcontrol = 0xA3;
    public const int VkLmenu = 0xA4;
    public const int VkRmenu = 0xA5;
    public const int VkLwin = 0x5B;
    public const int VkRwin = 0x5C;
    public const int VkUp = 0x26;
    public const int VkDown = 0x28;
    public const int VkLeft = 0x25;
    public const int VkRight = 0x27;

    public static readonly int[] AllModifierVks =
    [
        VkLcontrol, VkRcontrol, VkLshift, VkRshift,
        VkLmenu, VkRmenu, VkLwin, VkRwin,
    ];

    private static readonly Dictionary<string, VolumeAction> ActionByName = new(StringComparer.OrdinalIgnoreCase)
    {
        [AppUp] = VolumeAction.AppUp,
        [AppDown] = VolumeAction.AppDown,
        [SystemUp] = VolumeAction.SystemUp,
        [SystemDown] = VolumeAction.SystemDown,
    };

    public static bool IsModifierVk(int vk) => AllModifierVks.Contains(vk);

    public static Dictionary<string, HotkeyBinding> DefaultConfig() => new(StringComparer.OrdinalIgnoreCase)
    {
        [AppUp] = new HotkeyBinding { Modifiers = [VkRcontrol], Key = VkUp },
        [AppDown] = new HotkeyBinding { Modifiers = [VkRcontrol], Key = VkDown },
        [SystemUp] = new HotkeyBinding { Modifiers = [VkRcontrol], Key = VkRight },
        [SystemDown] = new HotkeyBinding { Modifiers = [VkRcontrol], Key = VkLeft },
    };

    public static string DisplayActionName(string actionName) => actionName switch
    {
        AppUp => "App volume up",
        AppDown => "App volume down",
        SystemUp => "System volume up",
        SystemDown => "System volume down",
        _ => actionName,
    };

    /// <summary>Converts stored bindings into validated match rules.</summary>
    public static List<HotkeyRule> FromConfig(AppConfig config)
    {
        var rules = new List<HotkeyRule>();
        if (config.Hotkeys == null)
            return rules;

        foreach (var (name, binding) in config.Hotkeys)
        {
            if (!ActionByName.TryGetValue(name, out var action) || binding == null)
                continue;
            if (binding.Key == 0 || IsModifierVk(binding.Key))
                continue;
            rules.Add(new HotkeyRule(
                action,
                binding.Key,
                binding.Modifiers.Where(IsModifierVk).Distinct().ToArray()));
        }

        return rules;
    }

    public static bool SameBinding(HotkeyBinding a, HotkeyBinding b)
    {
        if (a == null || b == null || a.Key != b.Key)
            return false;
        var modsA = a.Modifiers.Where(IsModifierVk).Distinct().OrderBy(v => v);
        var modsB = b.Modifiers.Where(IsModifierVk).Distinct().OrderBy(v => v);
        return modsA.SequenceEqual(modsB);
    }

    public static int ModifierSortOrder(int vk) => vk switch
    {
        VkLcontrol => 0,
        VkRcontrol => 1,
        VkLshift => 2,
        VkRshift => 3,
        VkLmenu => 4,
        VkRmenu => 5,
        VkLwin => 6,
        VkRwin => 7,
        _ => 9,
    };

    /// <summary>Left/right variants of one modifier share a family.</summary>
    public static int ModifierFamily(int vk) => vk switch
    {
        VkLcontrol or VkRcontrol => 0,
        VkLshift or VkRshift => 1,
        VkLmenu or VkRmenu => 2,
        VkLwin or VkRwin => 3,
        _ => 9,
    };

    public static string ModifierFamilyName(int vk) => ModifierFamily(vk) switch
    {
        0 => "Ctrl",
        1 => "Shift",
        2 => "Alt",
        3 => "Win",
        _ => KeyName(vk),
    };

    /// <summary>"Right Ctrl + ↑", "Ctrl + Shift + A", "F9" ...</summary>
    public static string ToDisplayString(HotkeyBinding? binding, bool eitherSide = false)
    {
        if (binding == null || binding.Key == 0)
            return "Not set";
        var parts = binding.Modifiers
            .Where(IsModifierVk)
            .Distinct()
            .OrderBy(ModifierSortOrder)
            .Select(vk => eitherSide ? ModifierFamilyName(vk) : KeyName(vk))
            .Distinct()
            .ToList();
        parts.Add(KeyName(binding.Key));
        return string.Join(" + ", parts);
    }

    public static string KeyName(int vk) => vk switch
    {
        VkLeft => "←",
        VkUp => "↑",
        VkRight => "→",
        VkDown => "↓",
        VkLcontrol => "Left Ctrl",
        VkRcontrol => "Right Ctrl",
        VkLshift => "Left Shift",
        VkRshift => "Right Shift",
        VkLmenu => "Left Alt",
        VkRmenu => "Right Alt",
        VkLwin => "Left Win",
        VkRwin => "Right Win",
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x1B => "Esc",
        0x20 => "Space",
        0x21 => "Page Up",
        0x22 => "Page Down",
        0x23 => "End",
        0x24 => "Home",
        0x2D => "Insert",
        0x2E => "Delete",
        0x6A => "Num *",
        0x6B => "Num +",
        0x6D => "Num -",
        0x6E => "Num .",
        0x6F => "Num /",
        0x90 => "Num Lock",
        0x91 => "Scroll Lock",
        0xAD => "Mute",
        0xAE => "Vol Down",
        0xAF => "Vol Up",
        _ => WpfKeyName(vk),
    };

    private static string WpfKeyName(int vk)
    {
        try
        {
            var key = KeyInterop.KeyFromVirtualKey(vk);
            return key == Key.None ? $"Key 0x{vk:X2}" : key.ToString();
        }
        catch
        {
            return $"Key 0x{vk:X2}";
        }
    }
}
