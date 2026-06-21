using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;
using Newtonsoft.Json;

namespace TabletHelper;

public class TabletHelperSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    public ToggleNode OverlayEnabled { get; set; } = new ToggleNode(true);
    public ToggleNode ShowGroupLabel { get; set; } = new ToggleNode(true);
    public ToggleNode ShowUsesLeft { get; set; } = new ToggleNode(false);
    public ToggleNode HideWhenTooltipOverItem { get; set; } = new ToggleNode(true);

    public ToggleNode SmoothUiRefresh { get; set; } = new ToggleNode(true);
    public RangeNode<int> FastRefreshIntervalMs { get; set; } = new RangeNode<int>(60, 16, 250);
    public RangeNode<int> ScanIntervalMs { get; set; } = new RangeNode<int>(30, 16, 2000);
    public RangeNode<int> ItemsPerTick { get; set; } = new RangeNode<int>(20, 1, 200);

    public RangeNode<int> BorderThickness { get; set; } = new RangeNode<int>(3, 1, 8);
    public RangeNode<float> LabelScale { get; set; } = new RangeNode<float>(1.0f, 0.5f, 2.0f);

    public ToggleNode LogSeenMods { get; set; } = new ToggleNode(false);

    public ToggleNode UseDirectSpecialStashPath { get; set; } = new ToggleNode(true);
    public ToggleNode AllowSpecialStashDeepFallback { get; set; } = new ToggleNode(false);

    public List<TabletTypeSettings> TabletTypes { get; set; } = TabletTypeSettings.CreateDefaults();

    public void EnsureDefaults()
    {
        var existingTypes = TabletTypes ?? new List<TabletTypeSettings>();
        var rebuilt = new List<TabletTypeSettings>();

        foreach (var def in TabletTypeSettings.CreateDefaults())
        {
            var matches = existingTypes
                .Where(x => string.Equals(x?.Key, def.Key, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var target = matches.FirstOrDefault() ?? def;
            target.Key = def.Key;
            target.DisplayName = def.DisplayName;

            var mergedGroups = new List<TabletRuleGroup>();
            var seenGroupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var type in matches)
            {
                if (type?.Groups == null)
                    continue;

                foreach (var group in type.Groups)
                {
                    if (group == null)
                        continue;

                    group.EnsureDefaults();
                    if (!seenGroupIds.Add(group.Id))
                        continue;

                    mergedGroups.Add(group);
                }
            }

            target.Groups = mergedGroups;
            target.EnsureDefaults();
            rebuilt.Add(target);
        }

        TabletTypes = rebuilt;
    }
}

public class TabletTypeSettings
{
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public List<TabletRuleGroup> Groups { get; set; } = new List<TabletRuleGroup>();

    [JsonIgnore]
    public string Header => string.IsNullOrWhiteSpace(DisplayName) ? Key : DisplayName;

    public void EnsureDefaults()
    {
        Groups ??= new List<TabletRuleGroup>();
        foreach (var group in Groups)
            group.EnsureDefaults();
    }

    public static List<TabletTypeSettings> CreateDefaults()
    {
        return new List<TabletTypeSettings>
        {
            new TabletTypeSettings { Key = TabletTypeKeys.Irradiated, DisplayName = "Irradiated Tablet" },
            new TabletTypeSettings { Key = TabletTypeKeys.Breach, DisplayName = "Breach Tablet" },
            new TabletTypeSettings { Key = TabletTypeKeys.Delirium, DisplayName = "Delirium Tablet" },
            new TabletTypeSettings { Key = TabletTypeKeys.Abyss, DisplayName = "Abyss Tablet" },
            new TabletTypeSettings { Key = TabletTypeKeys.Ritual, DisplayName = "Ritual Tablet" },
            new TabletTypeSettings { Key = TabletTypeKeys.Overseer, DisplayName = "Overseer Tablet" },
            new TabletTypeSettings { Key = TabletTypeKeys.Temple, DisplayName = "Temple Tablet" }
        };
    }
}

public class TabletRuleGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Group";
    public bool Enabled { get; set; } = true;
    public int MinimumMatchedBonuses { get; set; } = 1;
    public int MinimumUsesLeft { get; set; } = 1;
    public int ColorArgb { get; set; } = Color.DeepSkyBlue.ToArgb();
    public List<string> SelectedBonusIds { get; set; } = new List<string>();

    [JsonIgnore]
    public Color Color
    {
        get => Color.FromArgb(ColorArgb);
        set => ColorArgb = value.ToArgb();
    }

    public void EnsureDefaults()
    {
        if (string.IsNullOrWhiteSpace(Id))
            Id = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(Name))
            Name = "New Group";
        if (MinimumMatchedBonuses < 1)
            MinimumMatchedBonuses = 1;
        if (MinimumUsesLeft < 0)
            MinimumUsesLeft = 0;
        SelectedBonusIds ??= new List<string>();
    }
}

internal static class TabletTypeKeys
{
    internal const string Unknown = "Unknown";
    internal const string Irradiated = "Irradiated";
    internal const string Breach = "Breach";
    internal const string Delirium = "Delirium";
    internal const string Abyss = "Abyss";
    internal const string Ritual = "Ritual";
    internal const string Overseer = "Overseer";
    internal const string Temple = "Temple";
}
