using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.Elements.InventoryElements;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared;
using ExileCore2.Shared.Enums;

namespace TabletHelper;

internal enum ItemLocation
{
    Inventory,
    Stash,
    QuadStash,
    SpecialStash,
    Merchant,
    OwnMerchant
}

internal sealed class VisibleItemRef
{
    public long Key { get; init; }
    public ItemLocation Location { get; init; }
    public ServerInventory.InventSlotItem? InventoryItem { get; init; }
    public NormalInventoryItem? StashItem { get; init; }
    public object? UiElementItem { get; init; }

    public Entity? Entity => InventoryItem?.Item ?? StashItem?.Item ?? TryGetEntity(UiElementItem);

    public RectangleF GetRect()
    {
        if (InventoryItem != null)
            return InventoryItem.GetClientRect();
        if (StashItem != null)
            return StashItem.GetClientRectCache;
        if (UiElementItem != null)
            return TryGetElementRect(UiElementItem);
        return default;
    }

    private static Entity? TryGetEntity(object? element)
    {
        if (element == null)
            return null;

        try
        {
            var property = element.GetType().GetProperty("Entity", BindingFlags.Public | BindingFlags.Instance);
            return property?.GetValue(element) as Entity;
        }
        catch
        {
            return null;
        }
    }

    private static RectangleF TryGetElementRect(object element)
    {
        try
        {
            var property = element.GetType().GetProperty("GetClientRectCache", BindingFlags.Public | BindingFlags.Instance);
            if (property?.GetValue(element) is RectangleF rect)
                return rect;
        }
        catch
        {
            // Fall through to method fallback.
        }

        try
        {
            var method = element.GetType().GetMethod("GetClientRect", BindingFlags.Public | BindingFlags.Instance, Type.DefaultBinder, Type.EmptyTypes, null);
            if (method?.Invoke(element, Array.Empty<object>()) is RectangleF rect)
                return rect;
        }
        catch
        {
            // Ignore invalid UI element reads.
        }

        return default;
    }
}

internal sealed class TabletItem
{
    public long Key { get; }
    public string Metadata { get; }
    public string TabletTypeKey { get; private set; } = TabletTypeKeys.Unknown;
    public string TabletTypeName { get; private set; } = "Unknown Tablet";
    public ItemLocation Location { get; private set; }
    public RectangleF Rect { get; private set; }
    public int UsesLeft { get; private set; }
    public int ExplicitModCount { get; private set; }
    public ItemRarity Rarity { get; private set; }
    public bool Identified { get; private set; }
    public List<TabletModInfo> Mods { get; } = new List<TabletModInfo>();
    public HashSet<string> InternalMatchKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private VisibleItemRef _lastVisibleRef;

    public TabletItem(VisibleItemRef itemRef, bool includeDebugData = false)
    {
        Key = itemRef.Key;
        _lastVisibleRef = itemRef;
        Location = itemRef.Location;
        Rect = itemRef.GetRect();
        Metadata = itemRef.Entity?.Metadata ?? string.Empty;

        var mods = itemRef.Entity?.GetComponent<Mods>();
        Parse(mods, includeDebugData);
    }

    public void UpdateRect(VisibleItemRef itemRef)
    {
        _lastVisibleRef = itemRef;
        Rect = itemRef.GetRect();
        Location = itemRef.Location;
    }

    public bool TryRefreshRect()
    {
        try
        {
            var rect = _lastVisibleRef.GetRect();
            if (rect.Width <= 4 || rect.Height <= 4 || rect.Left < -10000 || rect.Top < -10000)
                return false;

            Rect = rect;
            Location = _lastVisibleRef.Location;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool HasInternalToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var fieldMode = "name";
        var valueToken = token.Trim();
        var separator = valueToken.IndexOf(':');
        if (separator > 0)
        {
            fieldMode = valueToken[..separator].Trim().ToLowerInvariant();
            valueToken = valueToken[(separator + 1)..];
        }

        var normalizedToken = TabletModInfo.NormalizeIdentifier(valueToken);
        if (normalizedToken.Length == 0)
            return false;

        return fieldMode switch
        {
            "raw" => ContainsInternalKey("raw", normalizedToken),
            "group" => ContainsInternalKey("group", normalizedToken),
            "translation" => ContainsInternalKey("translation", normalizedToken),
            _ => ContainsInternalKey("name", normalizedToken)
        };
    }

    private bool ContainsInternalKey(string field, string normalizedValue)
    {
        if (InternalMatchKeys.Contains(field + ":" + normalizedValue))
            return true;

        var stripped = TabletModInfo.StripTrailingDigits(normalizedValue);
        return stripped.Length > 0 && !string.Equals(stripped, normalizedValue, StringComparison.OrdinalIgnoreCase) && InternalMatchKeys.Contains(field + ":" + stripped);
    }

    private void Parse(Mods? mods, bool includeDebugData)
    {
        if (mods == null)
            return;

        try { ExplicitModCount = CountModCollection(mods, "ExplicitMods"); } catch { ExplicitModCount = 0; }
        Rarity = mods.ItemRarity;
        Identified = mods.Identified;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddModsFromCollection(mods, "ImplicitMods", "Implicit", seen, includeDebugData);
        AddModsFromCollection(mods, "CorruptionImplicitMods", "CorruptionImplicit", seen, includeDebugData);
        AddModsFromCollection(mods, "ExplicitMods", "Explicit", seen, includeDebugData);
        AddModsFromCollection(mods, "EnchantedMods", "Enchant", seen, includeDebugData);
        AddModsFromCollection(mods, "ItemMods", "ItemMods", seen, includeDebugData);

        foreach (var info in Mods)
            info.AddInternalMatchKeys(InternalMatchKeys);

        foreach (var info in Mods)
        {
            var lowerName = (info.Source + " " + info.AffixType + " " + info.Name + " " + info.RawName + " " + info.Group).ToLowerInvariant();
            if (lowerName.Contains("implicit") && lowerName.Contains("toweradd"))
            {
                var firstValue = info.Values.Count > 0 ? info.Values[0] : 0;
                if (firstValue > 0)
                    UsesLeft += firstValue;
            }
        }

        TabletTypeKey = DetectTabletType();
        TabletTypeName = ToDisplayName(TabletTypeKey);
    }

    private void AddModsFromCollection(Mods mods, string propertyName, string source, HashSet<string> seen, bool includeDebugData)
    {
        foreach (var mod in GetModCollection(mods, propertyName))
        {
            if (mod == null)
                continue;

            var info = TabletModInfo.FromItemMod(mod, source, includeDebugData);
            var key = $"{info.Name}|{info.RawName}|{info.Group}|{info.Translation}|{string.Join(",", info.Values)}";
            if (!seen.Add(key))
                continue;

            Mods.Add(info);
        }
    }

    private static IReadOnlyList<ItemMod> GetModCollection(Mods mods, string propertyName)
    {
        var result = new List<ItemMod>();
        object? value;

        try
        {
            var property = mods.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            value = property?.GetValue(mods);
        }
        catch
        {
            return result;
        }

        if (value is not IEnumerable enumerable)
            return result;

        foreach (var entry in enumerable)
        {
            if (entry is ItemMod itemMod)
                result.Add(itemMod);
        }

        return result;
    }

    private static int CountModCollection(Mods mods, string propertyName)
    {
        var count = 0;
        foreach (var _ in GetModCollection(mods, propertyName))
            count++;
        return count;
    }

    private string DetectTabletType()
    {
        var meta = Metadata.ToLowerInvariant();

        if (meta.Contains("abyss")) return TabletTypeKeys.Abyss;
        if (meta.Contains("breach")) return TabletTypeKeys.Breach;
        if (meta.Contains("delirium")) return TabletTypeKeys.Delirium;
        if (meta.Contains("ritual")) return TabletTypeKeys.Ritual;
        if (meta.Contains("boss") || meta.Contains("overseer")) return TabletTypeKeys.Overseer;
        if (meta.Contains("incursion") || meta.Contains("temple") || meta.Contains("vaal")) return TabletTypeKeys.Temple;
        if (meta.Contains("generic") || meta.Contains("irradiated") || meta.Contains("toweraugment")) return TabletTypeKeys.Irradiated;

        foreach (var mod in Mods)
        {
            var text = mod.SearchText;
            if (!text.Contains("implicit", StringComparison.OrdinalIgnoreCase))
                continue;

            if (text.Contains("abyss", StringComparison.OrdinalIgnoreCase)) return TabletTypeKeys.Abyss;
            if (text.Contains("breach", StringComparison.OrdinalIgnoreCase)) return TabletTypeKeys.Breach;
            if (text.Contains("delirium", StringComparison.OrdinalIgnoreCase)) return TabletTypeKeys.Delirium;
            if (text.Contains("ritual", StringComparison.OrdinalIgnoreCase)) return TabletTypeKeys.Ritual;
            if (text.Contains("mapboss", StringComparison.OrdinalIgnoreCase) || text.Contains("boss", StringComparison.OrdinalIgnoreCase)) return TabletTypeKeys.Overseer;
            if (text.Contains("incursion", StringComparison.OrdinalIgnoreCase) || text.Contains("beacon", StringComparison.OrdinalIgnoreCase) || text.Contains("vaal", StringComparison.OrdinalIgnoreCase)) return TabletTypeKeys.Temple;
            if (text.Contains("irradiated", StringComparison.OrdinalIgnoreCase)) return TabletTypeKeys.Irradiated;
        }

        return TabletTypeKeys.Unknown;
    }

    private static string ToDisplayName(string key)
    {
        return key switch
        {
            TabletTypeKeys.Irradiated => "Irradiated Tablet",
            TabletTypeKeys.Breach => "Breach Tablet",
            TabletTypeKeys.Delirium => "Delirium Tablet",
            TabletTypeKeys.Abyss => "Abyss Tablet",
            TabletTypeKeys.Ritual => "Ritual Tablet",
            TabletTypeKeys.Overseer => "Overseer Tablet",
            TabletTypeKeys.Temple => "Temple Tablet",
            _ => "Unknown Tablet"
        };
    }
}

internal sealed class TabletModInfo
{
    public string Source { get; private init; } = string.Empty;
    public string AffixType { get; private init; } = string.Empty;
    public string Name { get; private init; } = string.Empty;
    public string RawName { get; private init; } = string.Empty;
    public string DisplayName { get; private init; } = string.Empty;
    public string Group { get; private init; } = string.Empty;
    public string Translation { get; private init; } = string.Empty;
    public List<int> Values { get; private init; } = new List<int>();
    public string SearchText { get; private init; } = string.Empty;
    public string ModRecordDebug { get; private init; } = string.Empty;

    public static TabletModInfo FromItemMod(ItemMod mod, string source, bool includeDebugData = false)
    {
        var name = mod.Name ?? string.Empty;
        var displayName = mod.DisplayName ?? string.Empty;
        var rawName = TryGetString(mod, "RawName");
        var group = TryGetString(mod, "Group");
        var translation = TryGetString(mod, "Translation");
        var values = new List<int>();

        try
        {
            if (mod.Values != null)
                values.AddRange(mod.Values);
        }
        catch
        {
            // Some synthetic item mods can throw while values are being updated by memory reads.
        }

        var modRecord = TryGetObject(mod, "ModRecord");
        var affixType = GuessAffixType(mod, modRecord, source);
        var modRecordDebug = includeDebugData ? BuildObjectDebug(modRecord, 24) : string.Empty;
        var searchText = BuildSearchText(source, affixType, name, rawName, displayName, group, translation, values);

        return new TabletModInfo
        {
            Source = source,
            AffixType = affixType,
            Name = name,
            RawName = rawName,
            DisplayName = displayName,
            Group = group,
            Translation = translation,
            Values = values,
            SearchText = searchText,
            ModRecordDebug = modRecordDebug
        };
    }

    public void AddInternalMatchKeys(HashSet<string> keys)
    {
        AddInternalMatchKey(keys, "name", Name);
        AddInternalMatchKey(keys, "name", RawName);
        AddInternalMatchKey(keys, "raw", RawName);
        AddInternalMatchKey(keys, "group", Group);
        AddInternalMatchKey(keys, "translation", Translation);
    }

    private static void AddInternalMatchKey(HashSet<string> keys, string field, string value)
    {
        var normalized = NormalizeIdentifier(value);
        if (normalized.Length == 0)
            return;

        keys.Add(field + ":" + normalized);

        var stripped = StripTrailingDigits(normalized);
        if (stripped.Length > 0 && !string.Equals(stripped, normalized, StringComparison.OrdinalIgnoreCase))
            keys.Add(field + ":" + stripped);
    }

    public bool MatchesInternalToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var fieldMode = "name";
        var valueToken = token.Trim();
        var separator = valueToken.IndexOf(':');
        if (separator > 0)
        {
            fieldMode = valueToken[..separator].Trim().ToLowerInvariant();
            valueToken = valueToken[(separator + 1)..];
        }

        var normalizedToken = NormalizeIdentifier(valueToken);
        if (normalizedToken.Length == 0)
            return false;

        return fieldMode switch
        {
            "raw" => MatchesField(RawName, normalizedToken),
            "group" => MatchesField(Group, normalizedToken),
            "translation" => MatchesField(Translation, normalizedToken),
            // Default: exact internal mod identity only. Do not match Group/Translation here,
            // because several different tablet bonuses share the same Group value.
            _ => MatchesField(Name, normalizedToken) || MatchesField(RawName, normalizedToken)
        };
    }

    private static bool MatchesField(string value, string normalizedToken)
    {
        var field = NormalizeIdentifier(value);
        if (field.Length == 0)
            return false;

        if (string.Equals(field, normalizedToken, StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(StripTrailingDigits(field), normalizedToken, StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(field, StripTrailingDigits(normalizedToken), StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    internal static string StripTrailingDigits(string value)
    {
        var end = value.Length;
        while (end > 0 && char.IsDigit(value[end - 1]))
            end--;
        return value[..end];
    }

    internal static string NormalizeIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var sb = new StringBuilder(value.Length);
        foreach (var c in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(c);
        }

        return sb.ToString();
    }

    private static string GuessAffixType(ItemMod mod, object? modRecord, string source)
    {
        var combined = string.Join(" ", new[]
        {
            source,
            TryGetString(mod, "AffixType"),
            TryGetString(mod, "GenerationType"),
            TryGetString(mod, "GenType"),
            TryGetString(mod, "ModType"),
            TryGetString(mod, "Type"),
            TryGetString(modRecord, "AffixType"),
            TryGetString(modRecord, "GenerationType"),
            TryGetString(modRecord, "GenType"),
            TryGetString(modRecord, "ModType"),
            TryGetString(modRecord, "Type")
        }).ToLowerInvariant();

        if (combined.Contains("prefix")) return "Prefix";
        if (combined.Contains("suffix")) return "Suffix";
        if (combined.Contains("implicit")) return "Implicit";
        if (combined.Contains("enchant")) return "Enchant";
        if (combined.Contains("corrupt")) return "CorruptionImplicit";
        if (combined.Contains("explicit")) return "Explicit";

        return string.IsNullOrWhiteSpace(source) ? "Unknown" : source;
    }

    private static string BuildSearchText(string source, string affixType, string name, string rawName, string displayName, string group, string translation, List<int> values)
    {
        var sb = new StringBuilder();
        Append(sb, source);
        Append(sb, affixType);
        Append(sb, name);
        Append(sb, rawName);
        Append(sb, displayName);
        Append(sb, group);
        Append(sb, translation);
        foreach (var value in values)
            Append(sb, value.ToString());
        return sb.ToString().ToLowerInvariant();
    }

    private static void Append(StringBuilder sb, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        if (sb.Length > 0)
            sb.Append(' ');
        sb.Append(value);
    }

    private static object? TryGetObject(object? obj, string propertyName)
    {
        if (obj == null)
            return null;

        try
        {
            var property = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            return property?.GetValue(obj);
        }
        catch
        {
            return null;
        }
    }

    private static string TryGetString(object? obj, string propertyName)
    {
        try
        {
            var value = TryGetObject(obj, propertyName);
            return value?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string BuildObjectDebug(object? obj, int maxProperties)
    {
        if (obj == null)
            return string.Empty;

        try
        {
            var parts = new List<string>();
            foreach (var property in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (parts.Count >= maxProperties)
                    break;
                if (property.GetIndexParameters().Length > 0)
                    continue;

                object? value;
                try { value = property.GetValue(obj); }
                catch { continue; }

                if (value == null)
                    continue;

                var type = value.GetType();
                if (value is string || type.IsPrimitive || type.IsEnum || value is decimal)
                    parts.Add($"{property.Name}={value}");
            }

            return string.Join("; ", parts);
        }
        catch
        {
            return string.Empty;
        }
    }
}
