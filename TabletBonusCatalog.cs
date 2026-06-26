using System;
using System.Collections.Generic;
using System.Linq;

namespace TabletHelper;

internal sealed class TabletBonusDefinition
{
    public string Id { get; }
    public string Label { get; }
    public string Category { get; }
    public IReadOnlyList<string[]> TokenSets { get; }

    public TabletBonusDefinition(string id, string label, string category, params string[][] tokenSets)
    {
        Id = id;
        Label = label;
        Category = category;
        TokenSets = tokenSets
            .Select(set => set.Select(Normalize).Where(x => x.Length > 0).ToArray())
            .Where(set => set.Length > 0)
            .ToArray();
    }

    public bool Matches(TabletItem tablet)
    {
        if (tablet == null || tablet.InternalMatchKeys.Count == 0)
            return false;

        // Match only exact internal mod identity. Do not match Group or translated tooltip text here,
        // because multiple tablet bonuses can share the same Group or similar wording.
        foreach (var set in TokenSets)
        {
            if (set.Length != 1)
                continue;

            if (tablet.HasInternalToken(set[0]))
                return true;
        }

        return false;
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        return value.Trim().ToLowerInvariant();
    }
}

internal static class TabletBonusCatalog
{
    private const string Common = "Common";
    private const string Mechanic = "Mechanic-specific";
    private const string Unique = "Unique tablet";

    // Generic explicit tablet mods seen across multiple tablet types.
    // Kept shared intentionally. Mechanic-specific mods are kept only in their own tablet sections.
    private static readonly IReadOnlyList<TabletBonusDefinition> CommonBonuses = new List<TabletBonusDefinition>
    {
        B("TowerDroppedItemRarityIncrease", "Increased Rarity of Items found in Map", Common, N("TowerDroppedItemRarityIncrease")),
        B("TowerMapDroppedMapsIncrease", "Increased Quantity of Waystones found in Map", Common, N("TowerMapDroppedMapsIncrease")),
        B("TowerDroppedGoldIncrease", "Increased Gold found in Map", Common, N("TowerDroppedGoldIncrease")),
        B("TowerExperienceGainIncrease", "Increased Experience gain in Map", Common, N("TowerExperienceGainIncrease")),

        B("TowerMonsterEffectiveness", "Monsters have increased Effectiveness", Common, N("TowerMonsterEffectiveness")),
        B("TowerMonsterRarityIncrease", "Map has increased Monster Rarity", Common, N("TowerMonsterRarityIncrease")),
        B("TowerRarePackIncrease", "Map has increased number of Rare Monsters", Common, N("TowerRarePackIncrease")),
        B("TowerMagicPackIncrease", "Map has increased Magic Monsters", Common, N("TowerMagicPackIncrease")),
        B("TowerPackSizeIncrease", "Increased Pack Size in Map", Common, N("TowerPackSizeIncrease")),
        B("TowerRareMonsterSurpassing", "Rare Monsters have Surpassing chance to have an additional Modifier", Common, N("TowerRareMonsterSurpassing")),
        B("TowerReducedPackSize", "Reduced Pack Size in Map", Common, N("TowerReducedPackSize")),

        B("TowerRareChestCount", "Map contains additional Rare Chests", Common, N("TowerRareChestCount")),
        B("TowerAdditionalStoneCircle", "Map contains 1 additional Summoning Circle", Common, N("TowerAdditionalStoneCircle")),
        B("TowerAdditionalExile", "Map is inhabited by 1 additional Rogue Exile", Common, N("TowerAdditionalExile")),
        B("TowerAdditionalAzmeriWisp", "Map contains 1 additional Azmeri Spirit", Common, N("TowerAdditionalAzmeriWisp")),
        B("TowerAdditionalEssence", "Map contains 1 additional Essence", Common, N("TowerAdditionalEssence")),
        B("TowerAdditionalShrine", "Map contains 1 additional Shrine", Common, N("TowerAdditionalShrine")),
        B("TowerAdditionalStrongbox", "Map contains 1 additional Strongbox", Common, N("TowerAdditionalStrongbox")),

        B("TowerStoneCircleChance", "Map has increased chance to contain a Summoning Circle", Common, N("TowerStoneCircleChance")),
        B("TowerAdditionalExileChance", "Map has increased chance to contain Rogue Exiles", Common, N("TowerAdditionalExileChance")),
        B("TowerAdditionalSpiritChance", "Map has increased chance to contain Azmeri Spirits", Common, N("TowerAdditionalSpiritChance")),
        B("TowerAdditionalEssenceChance", "Map has increased chance to contain Essences", Common, N("TowerAdditionalEssenceChance")),
        B("TowerAdditionalShrineChance", "Map has increased chance to contain Shrines", Common, N("TowerAdditionalShrineChance")),
        B("TowerAdditionalStrongboxChance", "Map has increased chance to contain Strongboxes", Common, N("TowerAdditionalStrongboxChance")),

        B("TowerMapAdditionalModifier", "Map has additional random Modifiers", Common, N("TowerMapAdditionalModifier")),
        B("TowerMapAdditionalUniqueMonsterModifier", "Unique Monsters have 1 additional Rare Modifier", Common, N("TowerMapAdditionalUniqueMonsterModifier")),
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<TabletBonusDefinition>> SpecificBonuses = new Dictionary<string, IReadOnlyList<TabletBonusDefinition>>(StringComparer.OrdinalIgnoreCase)
    {
        [TabletTypeKeys.Irradiated] = new List<TabletBonusDefinition>
        {
            B("UniqueBiomeTabletForest", "Map also counts as a Forest Map", Unique, N("UniqueBiomeTabletForest")),
            B("UniqueBiomeTabletMountain", "Map also counts as a Mountain Map", Unique, N("UniqueBiomeTabletMountain")),
            B("UniqueBiomeTabletWater", "Map also counts as a Water Map", Unique, N("UniqueBiomeTabletWater")),
        },

        [TabletTypeKeys.Breach] = new List<TabletBonusDefinition>
        {
            B("TowerPackSizeIncrease", "Breaches in Map have increased Pack Size", Common, N("TowerPackSizeIncrease")),

            B("TowerBreachAdditionalRares", "Unstable Breaches spawn an additional Rare Monster when Stabilised", Mechanic, N("TowerBreachAdditionalRares")),
            B("TowerBreachBossChance", "Unstable Breaches have increased chance to contain Vruun, Marshal of Xesht", Mechanic, N("TowerBreachBossChance")),
            B("TowerBreachWombgiftLevelChance", "Wombgifts have chance to drop one Level higher", Mechanic, N("TowerBreachWombgiftLevelChance")),
            B("TowerBreachWombgiftQuantity", "Increased Quantity of Wombgifts found in Map", Mechanic, N("TowerBreachWombgiftQuantity")),
            B("TowerBreachHivebloodQuantity", "Increased Quantity of Hiveblood found in Map", Mechanic, N("TowerBreachHivebloodQuantity")),
            B("TowerBreachRareMonsterPotency", "Increased Effectiveness of Rare Breach Monsters", Mechanic, N("TowerBreachRareMonsterPotency")),
            B("TowerBreachMonsterQuantity", "Breaches have increased Monster density", Mechanic, N("TowerBreachMonsterQuantity")),

            B("UniqueBreachHiveAdditionalWaves", "Breach Hives have additional waves of Hiveborn Monsters", Unique, N("UniqueBreachHiveAdditionalWaves")),
            B("UniqueBreachMinimumRadius", "Unstable Breaches take additional seconds to collapse after timer is filled", Unique, N("UniqueBreachMinimumRadius")),
            B("UniqueBreachUnstableAdditionalRares", "Unstable Breaches spawn additional Rare Monsters when Stabilised", Unique, N("UniqueBreachUnstableAdditionalRares")),
            B("UniqueTowerBreachDensityIncrease", "Breaches have changed Monster density", Unique, N("UniqueTowerBreachDensityIncrease")),
        },

        [TabletTypeKeys.Delirium] = new List<TabletBonusDefinition>
        {
            B("TowerDeliriumAdditionalShardsChance", "Delirium Fog spawns increased Mirror Shards", Mechanic, N("TowerDeliriumAdditionalShardsChance")),
            B("TowerDeliriumRareMonsterPause", "Slaying Rare Monsters pauses the Delirium Mirror Timer", Mechanic, N("TowerDeliriumRareMonsterPause")),
            B("TowerDeliriumDoodadsIncrease", "Delirium Fog spawns increased Fracturing Mirrors", Mechanic, N("TowerDeliriumDoodadsIncrease")),
            B("TowerDeliriumPackSizeIncrease", "Delirium Monsters have increased Pack Size", Mechanic, N("TowerDeliriumPackSizeIncrease")),
            B("TowerDeliriumDifficultyIncrease", "Delirium Fog applies increased Deliriousness to Players", Mechanic, N("TowerDeliriumDifficultyIncrease")),
            B("TowerDeliriumFogPersistence", "Delirium Fog dissipates slower", Mechanic, N("TowerDeliriumFogPersistence")),
            B("TowerDeliriumFogDissipationDelayNew", "Delirium Fog lasts additional seconds before dissipating", Mechanic, N("TowerDeliriumFogDissipationDelayNew")),
            B("TowerDeliriumMonsterSplinterIncrease", "Increased Stack size of Simulacrum Splinters found in Map", Mechanic, N("TowerDeliriumMonsterSplinterIncrease")),
            B("TowerDeliriumBossChance", "Delirium Encounters are more likely to spawn Unique Bosses", Mechanic, N("TowerDeliriumBossChance")),

            B("UniqueDeliriumDifficultyIncrease", "Delirium Fog changes Deliriousness applied to Players", Unique, N("UniqueDeliriumDifficultyIncrease")),
            B("UniqueDeliriumEndlessFog", "Delirium Fog in your Maps never dissipates", Unique, N("UniqueDeliriumEndlessFog")),
        },

        [TabletTypeKeys.Abyss] = new List<TabletBonusDefinition>
        {
            B("TowerAbyssAdditionalChance", "Map contains an additional Abyss", Mechanic, N("TowerAbyssAdditionalChance")),
            B("TowerAbyss4AdditionalChance", "Map has chance to contain four additional Abysses", Mechanic, N("TowerAbyss4AdditionalChance")),
            B("TowerAbyssExtraTickets", "Increased chance for Desecrated Currency from Abysses", Mechanic, N("TowerAbyssExtraTickets")),
            B("TowerAbyssExtraModifiers", "Increased chance for Abyssal monsters to have Abyssal Modifiers", Mechanic, N("TowerAbyssExtraModifiers")),
            B("TowerAbyssIncreasedRewards", "Abyss Pits are twice as likely to have Rewards", Mechanic, N("TowerAbyssIncreasedRewards")),
            B("TowerAbyssDepthsChance", "Abysses have increased chance to lead to an Abyssal Depths", Mechanic, N("TowerAbyssDepthsChance")),
            B("TowerAbyssEffectivenessPerChasm", "Abyssal Monsters have increased Difficulty and Reward for each closed Pit", Mechanic, N("TowerAbyssEffectivenessPerChasm")),
            B("TowerAbyssRareMonsterIncrease", "Additional Rare Monsters are spawned from Abysses", Mechanic, N("TowerAbyssRareMonsterIncrease")),
            B("TowerAbyssMonsterIncrease", "Abysses spawn increased Monsters", Mechanic, N("TowerAbyssMonsterIncrease")),
        },

        [TabletTypeKeys.Ritual] = new List<TabletBonusDefinition>
        {
            B("TowerRitualOmenChance", "Ritual Favours have increased chance to be Omens", Mechanic, N("TowerRitualOmenChance")),
            B("TowerRitualMagicMonsters", "Revived Monsters from Ritual Altars have increased chance to be Rare", Mechanic, N("TowerRitualMagicMonsters")),
            B("TowerRitualRareMonsters", "Revived Monsters from Ritual Altars have increased chance to be Magic", Mechanic, N("TowerRitualRareMonsters")),
            B("TowerRitualChanceForNoCost", "Favours Rerolled have chance to cost no Tribute", Mechanic, N("TowerRitualChanceForNoCost")),
            B("TowerRitualAdditionalReroll", "Ritual Altars allow rerolling Favours additional times", Mechanic, N("TowerRitualAdditionalReroll")),
            B("TowerRitualDeferSpeed", "Favours Deferred reappear sooner", Mechanic, N("TowerRitualDeferSpeed")),
            B("TowerRitualDeferCostIncrease", "Deferring Favours costs reduced Tribute", Mechanic, N("TowerRitualDeferCostIncrease")),
            B("TowerRitualRerollCostIncrease", "Rerolling Favours costs reduced Tribute", Mechanic, N("TowerRitualRerollCostIncrease")),
            B("TowerRitualTributeIncrease", "Monsters Sacrificed at Ritual Altars grant increased Tribute", Mechanic, N("TowerRitualTributeIncrease")),

            B("UniqueRitualTributeCostIncrease", "Favours at Ritual Altars cost increased Tribute", Unique, N("UniqueRitualTributeCostIncrease")),
            B("UniqueRitualUnlimitedRerolls", "Can Reroll Favours at Ritual Altars twice as many times", Unique, N("UniqueRitualUnlimitedRerolls")),
        },

        [TabletTypeKeys.Overseer] = new List<TabletBonusDefinition>
        {
            B("TowerMapBossExperience", "Map Bosses grant increased Experience", Mechanic, N("TowerMapBossExperience")),
            B("TowerMapBossWaystoneChance", "Increased Quantity of Waystones dropped by Map Bosses", Mechanic, N("TowerMapBossWaystoneChance")),
            B("TowerMapBossRarity", "Increased Rarity of Items dropped by Map Bosses", Mechanic, N("TowerMapBossRarity")),
            B("TowerMapBossQuantity", "Increased Quantity of Items dropped by Map Bosses", Mechanic, N("TowerMapBossQuantity")),
            B("TowerMapBossAdditionalSpirit", "Areas with Powerful Map Bosses contain an additional Azmeri Spirit", Mechanic, N("TowerMapBossAdditionalSpirit")),
            B("TowerMapBossAdditionalEssence", "Areas with Powerful Map Bosses contain an additional Essence", Mechanic, N("TowerMapBossAdditionalEssence")),
            B("TowerMapBossAdditionalShrine", "Areas with Powerful Map Bosses contain an additional Shrine", Mechanic, N("TowerMapBossAdditionalShrine")),
            B("TowerMapBossAdditionalStrongbox", "Areas with Powerful Map Bosses contain additional Strongboxes", Mechanic, N("TowerMapBossAdditionalStrongbox")),

            B("UniqueMapBossAdditionalModifier", "Map Bosses have an additional Modifier", Unique, N("UniqueMapBossAdditionalModifier")),
            B("UniqueMapBossPossession", "Map Bosses are Hunted by Azmeri Spirits", Unique, N("UniqueMapBossPossession")),
        },

        [TabletTypeKeys.Temple] = new List<TabletBonusDefinition>
        {
            B("TowerIncursionRareChestChance", "Increased chance Vaal Beacon Chests are Rare", Mechanic, N("TowerIncursionRareChestChance")),
            B("TowerIncursionBossChance", "Chance to add a Vaal Beacon Unique Monster", Mechanic, N("TowerIncursionBossChance")),
            B("TowerIncursionTokenChance", "Chance to gain an additional Crystal from Vaal Beacons", Mechanic, N("TowerIncursionTokenChance")),
            B("TowerIncursionSecondaryEncounters", "Increased chance Vaal Beacons summon additional Monsters", Mechanic, N("TowerIncursionSecondaryEncounters")),
            B("TowerIncursionExtraPacksChance", "Chance for extra packs of Monsters around Vaal Beacons", Mechanic, N("TowerIncursionExtraPacksChance")),
            B("TowerIncursionExtraPacks", "1 extra pack of Monsters around Vaal Beacons", Mechanic, N("TowerIncursionExtraPacks")),
            B("TowerIncursionPackSize", "Increased Pack Size for Monsters around Vaal Beacons", Mechanic, N("TowerIncursionPackSize")),
        }
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<TabletBonusDefinition>> BonusCache = BuildBonusCache();
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, TabletBonusDefinition>> BonusLookupCache = BuildBonusLookupCache();

    public static IReadOnlyList<TabletBonusDefinition> GetBonusesFor(string tabletTypeKey)
    {
        return BonusCache.TryGetValue(tabletTypeKey ?? string.Empty, out var bonuses)
            ? bonuses
            : Array.Empty<TabletBonusDefinition>();
    }

    public static TabletBonusDefinition? Find(string tabletTypeKey, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        return BonusLookupCache.TryGetValue(tabletTypeKey ?? string.Empty, out var lookup) && lookup.TryGetValue(id, out var bonus)
            ? bonus
            : null;
    }

    public static bool IsKnownTabletType(string key)
    {
        return BonusCache.ContainsKey(key ?? string.Empty);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<TabletBonusDefinition>> BuildBonusCache()
    {
        var result = new Dictionary<string, IReadOnlyList<TabletBonusDefinition>>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in TabletTypeSettings.CreateDefaults().Where(x => !IsGlobalTypeKey(x.Key)))
        {
            var list = new List<TabletBonusDefinition>();
            if (SpecificBonuses.TryGetValue(type.Key, out var specific))
                list.AddRange(specific);
            list.AddRange(CommonBonuses);

            result[type.Key] = BuildDistinctSortedBonusList(list);
        }

        result[TabletTypeKeys.Global] = BuildDistinctSortedBonusList(result.Values.SelectMany(x => x));

        return result;
    }

    private static IReadOnlyList<TabletBonusDefinition> BuildDistinctSortedBonusList(IEnumerable<TabletBonusDefinition> bonuses)
    {
        return bonuses
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsGlobalTypeKey(string key)
    {
        return string.Equals(key, TabletTypeKeys.Global, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, TabletBonusDefinition>> BuildBonusLookupCache()
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, TabletBonusDefinition>>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in BonusCache)
        {
            result[pair.Key] = pair.Value
                .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }

        return result;
    }

    private static TabletBonusDefinition B(string id, string label, string category, params string[][] tokenSets) => new TabletBonusDefinition(id, label, category, tokenSets);
    private static string[] N(string name) => new[] { "name:" + name };
}
