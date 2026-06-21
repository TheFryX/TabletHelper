using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using ExileCore2;
using ExileCore2.PoEMemory.Elements;
using ExileCore2.PoEMemory.Elements.InventoryElements;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using ImGuiNET;
using RectangleF = ExileCore2.Shared.RectangleF;

namespace TabletHelper;

public class TabletHelper : BaseSettingsPlugin<TabletHelperSettings>
{
    private readonly Dictionary<long, TabletItem> _tablets = new Dictionary<long, TabletItem>();
    private readonly Dictionary<long, CachedTabletMatches> _matchCache = new Dictionary<long, CachedTabletMatches>();
    private readonly HashSet<long> _discarded = new HashSet<long>();
    private readonly Queue<VisibleItemRef> _pending = new Queue<VisibleItemRef>();
    private readonly HashSet<long> _pendingKeys = new HashSet<long>();
    private readonly Dictionary<string, string> _bonusSearch = new Dictionary<string, string>();
    private readonly HashSet<string> _loggedMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private bool _debugFileWriteFailed;
    private readonly HashSet<string> _collapsedSettingsNodesThisSession = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> _newlyAddedGroupsToOpen = new HashSet<string>(StringComparer.Ordinal);

    private const string PluginVersion = "v1.0";

    // Legacy full path is kept as a fallback. The preferred path is dynamic:
    // StashElement.StashInventoryPanel.Children[IndexVisibleStash] -> 0 -> 0 -> 0 -> 1 -> 0 -> 0 -> 0.
    private static readonly int[] LegacySpecialTabletStashItemsContainerPath = { 2, 0, 0, 0, 1, 1, 6, 0, 0, 0, 1, 0, 0, 0 };
    private static readonly int[] SpecialTabletStashItemsRelativePath = { 0, 0, 0, 1, 0, 0, 0 };

    private InventoryElement? _inventoryPanel;
    private StashElement? _stashPanel;
    private StashElement? _guildStashPanel;
    private VendorStashTabContainer? _merchantPanel;
    private StashElement? _ownMerchantPanel;

    private long _lastFastRefreshMs;
    private long _lastFullScanMs;
    private int _lastContextHash;
    private int _settingsVersion = 1;

    private IngameState InGameState => GameController.IngameState;

    public override bool Initialise()
    {
        Settings.EnsureDefaults();
        LoadExistingDebugKeys();
        return base.Initialise();
    }

    public override void Tick()
    {
        if (!Settings.Enable.Value)
        {
            ClearRuntimeState();
            return;
        }

        UpdatePanels();

        if (!AnySupportedWindowVisible())
        {
            ClearRuntimeState();
            return;
        }

        var contextHash = GetVisibleContextHash();
        if (contextHash != _lastContextHash)
        {
            _lastContextHash = contextHash;
            _tablets.Clear();
            _matchCache.Clear();
            _discarded.Clear();
            _pending.Clear();
            _pendingKeys.Clear();
            _lastFastRefreshMs = 0;
            _lastFullScanMs = 0;
        }

        var now = Environment.TickCount64;

        if (Settings.SmoothUiRefresh.Value)
        {
            if (now - _lastFastRefreshMs >= Settings.FastRefreshIntervalMs.Value)
            {
                _lastFastRefreshMs = now;
                RefreshKnownTabletRects();
            }

            if (now - _lastFullScanMs >= Settings.ScanIntervalMs.Value)
            {
                _lastFullScanMs = now;
                ScanVisibleItems();
            }
        }
        else if (now - _lastFullScanMs >= Settings.ScanIntervalMs.Value)
        {
            _lastFullScanMs = now;
            ScanVisibleItems();
        }

        ProcessPendingItems(Settings.ItemsPerTick.Value);

        if (Settings.LogSeenMods.Value && _tablets.Count > 0)
        {
            foreach (var tablet in _tablets.Values.ToArray())
                LogTabletMods(tablet);
        }
    }

    public override void Render()
    {
        if (!Settings.Enable.Value || !Settings.OverlayEnabled.Value)
            return;

        if (!AnySupportedWindowVisible())
            return;

        var hoveredItemTooltip = Settings.HideWhenTooltipOverItem.Value && GameController.IngameState.UIHoverElement.Tooltip is { Address: not 0, IsValid: true } hover
            ? hover
            : null;

        IDisposable? textScaleScope = null;
        try
        {
            if (Settings.ShowGroupLabel.Value)
                textScaleScope = Graphics.SetTextScale(Settings.LabelScale.Value);

            foreach (var tablet in _tablets.Values)
            {
                if (tablet == null || !TabletBonusCatalog.IsKnownTabletType(tablet.TabletTypeKey) || !IsUsableRect(tablet.Rect))
                    continue;

                if (hoveredItemTooltip != null)
                {
                    var tooltip = hoveredItemTooltip.GetClientRectCache;
                    tooltip.Inflate(-10, -10);
                    if (tooltip.Intersects(tablet.Rect))
                        continue;
                }

                var matches = FindMatches(tablet);
                if (matches.Count == 0)
                    continue;

                // The first matching group keeps priority for the highlight color,
                // but all matching group names are shown on the item label.
                DrawHighlight(tablet.Rect, matches[0].Group.Color, tablet.Location);

                if (Settings.ShowGroupLabel.Value && tablet.Location != ItemLocation.QuadStash)
                    DrawGroupLabels(tablet, matches);
            }
        }
        finally
        {
            textScaleScope?.Dispose();
        }
    }

    public override void DrawSettings()
    {
        Settings.EnsureDefaults();

        DrawGeneralSettings();
        DrawTabletTypeSettings();
        DrawDebugSettings();
    }

    private void DrawGeneralSettings()
    {
        CollapseSettingsNodeOnFirstUse("general");

        if (!ImGui.CollapsingHeader("General###tablet_helper_general_v1", ImGuiTreeNodeFlags.None))
            return;

        ImGui.Indent();
        ImGui.TextDisabled($"Tablet Helper {PluginVersion}");

        Checkbox("Enable", Settings.Enable);
        Checkbox("Enable tablet overlay", Settings.OverlayEnabled);
        Checkbox("Show group label on tablet", Settings.ShowGroupLabel);
        Checkbox("Show uses left in label", Settings.ShowUsesLeft);
        Checkbox("Hide overlay when item tooltip covers item", Settings.HideWhenTooltipOverItem);

        ImGui.Separator();
        Checkbox("Smooth overlay refresh", Settings.SmoothUiRefresh);
        SliderInt("Known tablet rect refresh interval ms", Settings.FastRefreshIntervalMs);
        SliderInt("Full discovery scan interval ms", Settings.ScanIntervalMs);
        SliderInt("Items parsed per tick", Settings.ItemsPerTick);
        ImGui.TextDisabled("Fast refresh only updates already known tablet rectangles. Full discovery finds new/missing items at a lower rate.");

        ImGui.Separator();
        SliderInt("Border thickness", Settings.BorderThickness);
        SliderFloat("Label scale", Settings.LabelScale);

        ImGui.TextDisabled($"Cached tablets: {_tablets.Count}, pending: {_pending.Count}");
        ImGui.Unindent();
    }

    private void DrawTabletTypeSettings()
    {
        foreach (var typeSettings in Settings.TabletTypes)
        {
            DrawSingleTabletTypeSettings(typeSettings);
        }
    }

    private void DrawSingleTabletTypeSettings(TabletTypeSettings typeSettings)
    {
        ImGui.PushID(typeSettings.Key);

        var groupCount = typeSettings.Groups?.Count ?? 0;
        var header = groupCount > 0 ? $"{typeSettings.Header}  ({groupCount})" : typeSettings.Header;
        CollapseSettingsNodeOnFirstUse("tablet_type:" + typeSettings.Key);

        if (ImGui.CollapsingHeader($"{header}###tablet_type_header_v1", ImGuiTreeNodeFlags.None))
        {
            ImGui.Indent();

            var enabled = typeSettings.Enabled;
            if (ImGui.Checkbox("Enable", ref enabled))
            {
                typeSettings.Enabled = enabled;
                MarkMatchingSettingsChanged();
            }

            ImGui.SameLine();
            if (ImGui.Button("Add Group"))
            {
                var newGroup = new TabletRuleGroup
                {
                    Name = GetNextGroupName(typeSettings),
                    Color = DefaultGroupColor(typeSettings.Groups.Count)
                };

                typeSettings.Groups.Add(newGroup);
                _newlyAddedGroupsToOpen.Add(newGroup.Id);
                MarkMatchingSettingsChanged();
            }

            if (typeSettings.Groups.Count == 0)
                ImGui.TextDisabled("No groups yet. Add Group creates a rule for this tablet type.");
            else
                ImGui.TextDisabled("All matching groups are shown on the label. First matching group controls highlight color.");

            for (var i = 0; i < typeSettings.Groups.Count; i++)
            {
                DrawGroupEditor(typeSettings, i);
            }

            ImGui.Unindent();
        }

        ImGui.PopID();
    }

    private void DrawGroupEditor(TabletTypeSettings typeSettings, int index)
    {
        var group = typeSettings.Groups[index];
        group.EnsureDefaults();

        ImGui.PushID(group.Id);
        var selectedCount = group.SelectedBonusIds?.Count ?? 0;
        var header = $"{group.Name}  [{selectedCount} selected]";

        var groupCollapseKey = "group:" + group.Id;
        if (_newlyAddedGroupsToOpen.Remove(group.Id))
        {
            _collapsedSettingsNodesThisSession.Add(groupCollapseKey);
            ImGui.SetNextItemOpen(true, ImGuiCond.Always);
        }
        else
        {
            CollapseSettingsNodeOnFirstUse(groupCollapseKey);
        }

        if (ImGui.TreeNodeEx($"{header}###group_editor_v1", ImGuiTreeNodeFlags.None))
        {
            var enabled = group.Enabled;
            if (ImGui.Checkbox("Enabled", ref enabled))
            {
                group.Enabled = enabled;
                MarkMatchingSettingsChanged();
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Move Up") && index > 0)
            {
                (typeSettings.Groups[index - 1], typeSettings.Groups[index]) = (typeSettings.Groups[index], typeSettings.Groups[index - 1]);
                MarkMatchingSettingsChanged();
                ImGui.TreePop();
                ImGui.PopID();
                return;
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Move Down") && index < typeSettings.Groups.Count - 1)
            {
                (typeSettings.Groups[index + 1], typeSettings.Groups[index]) = (typeSettings.Groups[index], typeSettings.Groups[index + 1]);
                MarkMatchingSettingsChanged();
                ImGui.TreePop();
                ImGui.PopID();
                return;
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Delete"))
            {
                typeSettings.Groups.RemoveAt(index);
                MarkMatchingSettingsChanged();
                ImGui.TreePop();
                ImGui.PopID();
                return;
            }

            var name = group.Name ?? string.Empty;
            ImGui.SetNextItemWidth(260);
            if (ImGui.InputText("Name", ref name, 96))
            {
                group.Name = name;
                MarkMatchingSettingsChanged();
            }

            var minBonuses = group.MinimumMatchedBonuses;
            ImGui.SetNextItemWidth(90);
            if (ImGui.InputInt("Minimum matched bonuses", ref minBonuses, 1, 1))
            {
                group.MinimumMatchedBonuses = Math.Clamp(minBonuses, 1, 20);
                MarkMatchingSettingsChanged();
            }

            var minUses = group.MinimumUsesLeft;
            ImGui.SetNextItemWidth(90);
            if (ImGui.InputInt("Minimum uses left", ref minUses, 1, 1))
            {
                group.MinimumUsesLeft = Math.Clamp(minUses, 0, 20);
                MarkMatchingSettingsChanged();
            }

            if (DrawColorEdit("Group color", group.Color, c => group.Color = c))
                MarkMatchingSettingsChanged();

            var searchKey = typeSettings.Key + ":" + group.Id;
            if (!_bonusSearch.TryGetValue(searchKey, out var search))
                search = string.Empty;

            ImGui.SetNextItemWidth(320);
            if (ImGui.InputText("Search bonuses", ref search, 128))
                _bonusSearch[searchKey] = search;

            ImGui.SameLine();
            if (ImGui.SmallButton("Clear All"))
            {
                group.SelectedBonusIds.Clear();
                MarkMatchingSettingsChanged();
            }

            DrawBonusList(typeSettings.Key, group, search);

            ImGui.TreePop();
        }
        ImGui.PopID();
    }

    private void DrawBonusList(string tabletTypeKey, TabletRuleGroup group, string search)
    {
        var bonuses = TabletBonusCatalog.GetBonusesFor(tabletTypeKey)
            .Where(b => string.IsNullOrWhiteSpace(search) || b.Label.Contains(search, StringComparison.OrdinalIgnoreCase) || b.Category.Contains(search, StringComparison.OrdinalIgnoreCase) || b.Id.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var selected = group.SelectedBonusIds ??= new List<string>();

        var childHeight = Math.Clamp(bonuses.Count * 24 + 28, 140, 420);
        var childVisible = ImGui.BeginChild("##bonus_list", new Vector2(0, childHeight), ImGuiChildFlags.Border);
        if (childVisible)
        {
            foreach (var bonus in bonuses)
            {
                var isSelected = selected.Contains(bonus.Id, StringComparer.OrdinalIgnoreCase);
                if (ImGui.Checkbox($"{bonus.Label}##{bonus.Id}", ref isSelected))
                {
                    if (isSelected)
                    {
                        if (!selected.Contains(bonus.Id, StringComparer.OrdinalIgnoreCase))
                        {
                            selected.Add(bonus.Id);
                            MarkMatchingSettingsChanged();
                        }
                    }
                    else
                    {
                        if (selected.RemoveAll(x => string.Equals(x, bonus.Id, StringComparison.OrdinalIgnoreCase)) > 0)
                            MarkMatchingSettingsChanged();
                    }
                }

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{bonus.Category} | {bonus.Id}");
            }

            if (bonuses.Count == 0)
                ImGui.TextDisabled("No bonuses match the search.");
        }

        ImGui.EndChild();
    }

    private void DrawDebugSettings()
    {
        CollapseSettingsNodeOnFirstUse("debug");

        if (!ImGui.CollapsingHeader("Debug###tablet_helper_debug_v1", ImGuiTreeNodeFlags.None))
            return;

        ImGui.Indent();
        Checkbox("Live dump unique tablet bonuses to txt", Settings.LogSeenMods);
        Checkbox("Use direct Tablet stash path", Settings.UseDirectSpecialStashPath);
        Checkbox("Allow deep fallback scan for special stash", Settings.AllowSpecialStashDeepFallback);
        ImGui.TextDisabled("Direct path uses StashElement.StashInventoryPanel[IndexVisibleStash] -> 0 -> 0 -> 0 -> 1 -> 0 -> 0 -> 0.");
        ImGui.TextDisabled("Keep deep fallback disabled unless the special Tablet stash stops highlighting after a game UI patch.");
        ImGui.TextDisabled("When enabled, this writes only newly seen tablet internal mods to Plugins\\Temp\\TabletHelper\\TabletModsDump.txt.");
        ImGui.TextDisabled("Roll values are ignored, so 21% and 30% of the same mod are one entry.");
        ImGui.TextDisabled(GetDebugDumpPath());

        if (ImGui.Button("Clear debug txt"))
            ClearDebugDumpFile();

        ImGui.SameLine();
        if (ImGui.Button("Clear runtime cache"))
            ClearRuntimeState();
        ImGui.Unindent();
    }

    private void UpdatePanels()
    {
        _inventoryPanel = InGameState.IngameUi.InventoryPanel;
        _stashPanel = InGameState.IngameUi.StashElement;
        _guildStashPanel = InGameState.IngameUi.GuildStashElement;
        _merchantPanel = InGameState.IngameUi.PurchaseWindowHideout.TabContainer;
        _ownMerchantPanel = InGameState.IngameUi.OfflineMerchantPanel;
    }

    private bool AnySupportedWindowVisible()
    {
        return (_inventoryPanel?.IsVisible ?? false)
               || (_stashPanel?.IsVisible ?? false)
               || (_guildStashPanel?.IsVisible ?? false)
               || (_merchantPanel?.IsVisible ?? false)
               || (_ownMerchantPanel?.IsVisible ?? false);
    }

    private int GetVisibleContextHash()
    {
        unchecked
        {
            var hash = 17;
            AddContext(ref hash, _inventoryPanel?.IsVisible ?? false, 1, GetInventoryCount());
            AddContext(ref hash, _stashPanel?.IsVisible ?? false, _stashPanel?.VisibleStash?.GetHashCode() ?? 0, _stashPanel?.VisibleStash?.VisibleInventoryItems?.Count ?? 0);
            AddContext(ref hash, _guildStashPanel?.IsVisible ?? false, _guildStashPanel?.VisibleStash?.GetHashCode() ?? 0, _guildStashPanel?.VisibleStash?.VisibleInventoryItems?.Count ?? 0);
            AddContext(ref hash, _merchantPanel?.IsVisible ?? false, _merchantPanel?.VisibleStash?.GetHashCode() ?? 0, _merchantPanel?.VisibleStash?.VisibleInventoryItems?.Count ?? 0);
            AddContext(ref hash, _ownMerchantPanel?.IsVisible ?? false, _ownMerchantPanel?.VisibleStash?.GetHashCode() ?? 0, _ownMerchantPanel?.VisibleStash?.VisibleInventoryItems?.Count ?? 0);
            return hash;
        }
    }

    private static void AddContext(ref int hash, bool visible, int panelHash, int count)
    {
        hash = hash * 31 + (visible ? 1 : 0);
        if (!visible)
            return;
        hash = hash * 31 + panelHash;
        hash = hash * 31 + count;
    }

    private int GetInventoryCount()
    {
        try
        {
            return GameController.IngameState.ServerData.PlayerInventories[0].Inventory.InventorySlotItems.Count;
        }
        catch
        {
            return 0;
        }
    }

    private void RefreshKnownTabletRects()
    {
        if (_tablets.Count == 0)
            return;

        foreach (var key in _tablets.Keys.ToArray())
        {
            if (!_tablets.TryGetValue(key, out var tablet))
                continue;

            if (!tablet.TryRefreshRect())
            {
                _tablets.Remove(key);
                _matchCache.Remove(key);
            }
        }
    }

    private void ScanVisibleItems()
    {
        var currentKeys = new HashSet<long>();
        var isQuad = IsQuadTab();

        if (_inventoryPanel?.IsVisible == true)
        {
            try
            {
                foreach (var item in GameController.IngameState.ServerData.PlayerInventories[0].Inventory.InventorySlotItems)
                    TrackInventoryItem(item, ItemLocation.Inventory, currentKeys);
            }
            catch (Exception ex)
            {
                DebugLog($"Inventory scan failed: {ex.Message}");
            }
        }

        if (_stashPanel?.IsVisible == true && _stashPanel.VisibleStash?.VisibleInventoryItems != null)
        {
            foreach (var item in _stashPanel.VisibleStash.VisibleInventoryItems)
                TrackStashItem(item, isQuad ? ItemLocation.QuadStash : ItemLocation.Stash, currentKeys);
        }

        // Special stash tabs, for example the Fragment/Tablet tab, do not always expose
        // their slots through VisibleStash.VisibleInventoryItems. The Tablet sub-tab has
        // a stable direct container path, so use that first and avoid scanning the whole UI tree.
        if (_stashPanel?.IsVisible == true && (_stashPanel.VisibleStash?.VisibleInventoryItems?.Count ?? 0) == 0)
        {
            var foundViaDirectPath = Settings.UseDirectSpecialStashPath.Value && ScanSpecialStashDirectPath(_stashPanel, currentKeys);
            if (!foundViaDirectPath && Settings.AllowSpecialStashDeepFallback.Value)
                ScanSpecialStashUiItems(_stashPanel, currentKeys);
        }

        if (_guildStashPanel?.IsVisible == true && _guildStashPanel.VisibleStash?.VisibleInventoryItems != null)
        {
            foreach (var item in _guildStashPanel.VisibleStash.VisibleInventoryItems)
                TrackStashItem(item, isQuad ? ItemLocation.QuadStash : ItemLocation.Stash, currentKeys);
        }

        if (_guildStashPanel?.IsVisible == true && (_guildStashPanel.VisibleStash?.VisibleInventoryItems?.Count ?? 0) == 0)
        {
            var foundViaDirectPath = Settings.UseDirectSpecialStashPath.Value && ScanSpecialStashDirectPath(_guildStashPanel, currentKeys);
            if (!foundViaDirectPath && Settings.AllowSpecialStashDeepFallback.Value)
                ScanSpecialStashUiItems(_guildStashPanel, currentKeys);
        }

        if (_merchantPanel?.IsVisible == true && _merchantPanel.VisibleStash?.VisibleInventoryItems != null)
        {
            foreach (var item in _merchantPanel.VisibleStash.VisibleInventoryItems)
                TrackStashItem(item, ItemLocation.Merchant, currentKeys);
        }

        if (_ownMerchantPanel?.IsVisible == true && _ownMerchantPanel.VisibleStash?.VisibleInventoryItems != null)
        {
            foreach (var item in _ownMerchantPanel.VisibleStash.VisibleInventoryItems)
                TrackStashItem(item, ItemLocation.OwnMerchant, currentKeys);
        }

        RemoveMissingItems(currentKeys);
    }

    private bool ScanSpecialStashDirectPath(object root, HashSet<long> currentKeys)
    {
        var itemsContainer = TryGetSpecialStashItemsContainer(root);
        if (itemsContainer == null || !IsUiElementVisible(itemsContainer))
            return false;

        var foundAnyTablet = false;
        foreach (var child in GetUiChildren(itemsContainer))
        {
            if (child == null || !IsUiElementVisible(child))
                continue;

            var entity = TryGetUiEntity(child);
            if (entity == null || string.IsNullOrEmpty(entity.Metadata) || !entity.Metadata.Contains("TowerAugment", StringComparison.OrdinalIgnoreCase))
                continue;

            var rect = TryGetUiRect(child);
            if (!IsUsableRect(rect))
                continue;

            TrackUiElementItem(child, entity, ItemLocation.SpecialStash, currentKeys);
            foundAnyTablet = true;
        }

        return foundAnyTablet;
    }

    private static object? TryGetSpecialStashItemsContainer(object stashElement)
    {
        // Preferred route based on StashElement internals from the UI dump.
        // This avoids scanning the full StashElement tree and does not rely on the visible stash being index 6.
        var dynamicContainer = TryGetDynamicSpecialStashItemsContainer(stashElement);
        if (dynamicContainer != null && IsUiElementVisible(dynamicContainer))
            return dynamicContainer;

        // Safety fallback for older builds/dumps where the selected index path is unavailable.
        return TryGetChildByPath(stashElement, LegacySpecialTabletStashItemsContainerPath);
    }

    private static object? TryGetDynamicSpecialStashItemsContainer(object stashElement)
    {
        var inventoryPanel = TryGetObjectProperty(stashElement, "StashInventoryPanel");
        if (inventoryPanel == null)
            return null;

        var visibleIndex = TryGetIntProperty(stashElement, "IndexVisibleStash");
        if (visibleIndex < 0)
            return null;

        var visibleTabRoot = TryGetChildAt(inventoryPanel, visibleIndex);
        if (visibleTabRoot == null)
            return null;

        return TryGetChildByPath(visibleTabRoot, SpecialTabletStashItemsRelativePath);
    }

    private static object? TryGetObjectProperty(object element, string propertyName)
    {
        try
        {
            var property = element.GetType().GetProperty(propertyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            return property?.GetValue(element);
        }
        catch
        {
            return null;
        }
    }

    private static int TryGetIntProperty(object element, string propertyName)
    {
        try
        {
            var property = element.GetType().GetProperty(propertyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            var value = property?.GetValue(element);
            if (value is int intValue)
                return intValue;
            if (value is short shortValue)
                return shortValue;
            if (value is long longValue && longValue >= int.MinValue && longValue <= int.MaxValue)
                return (int)longValue;
            if (value != null && int.TryParse(value.ToString(), out var parsed))
                return parsed;
        }
        catch
        {
            return -1;
        }

        return -1;
    }

    private static object? TryGetChildByPath(object root, IReadOnlyList<int> path)
    {
        var current = root;
        foreach (var index in path)
        {
            current = TryGetChildAt(current, index);
            if (current == null)
                return null;
        }

        return current;
    }

    private static object? TryGetChildAt(object element, int index)
    {
        if (index < 0)
            return null;

        object? children;
        try
        {
            var property = element.GetType().GetProperty("Children", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            children = property?.GetValue(element);
        }
        catch
        {
            return null;
        }

        if (children is IList list)
            return index < list.Count ? list[index] : null;

        if (children is not IEnumerable enumerable)
            return null;

        var i = 0;
        foreach (var child in enumerable)
        {
            if (i == index)
                return child;
            i++;
        }

        return null;
    }

    private void ScanSpecialStashUiItems(object root, HashSet<long> currentKeys)
    {
        const int maxNodes = 1200;
        const int maxDepth = 18;

        var visited = new HashSet<int>();
        var stack = new Stack<(object Element, int Depth)>();
        stack.Push((root, 0));
        var visitedNodes = 0;

        while (stack.Count > 0 && visitedNodes < maxNodes)
        {
            var (element, depth) = stack.Pop();
            if (element == null || depth > maxDepth)
                continue;

            var elementHash = element.GetHashCode();
            if (!visited.Add(elementHash))
                continue;

            visitedNodes++;

            if (!IsUiElementVisible(element))
                continue;

            var entity = TryGetUiEntity(element);
            if (entity != null && !string.IsNullOrEmpty(entity.Metadata) && entity.Metadata.Contains("TowerAugment", StringComparison.OrdinalIgnoreCase))
            {
                var rect = TryGetUiRect(element);
                if (IsUsableRect(rect))
                    TrackUiElementItem(element, entity, ItemLocation.SpecialStash, currentKeys);
            }

            foreach (var child in GetUiChildren(element))
                stack.Push((child, depth + 1));
        }
    }

    private static bool IsUiElementVisible(object element)
    {
        try
        {
            var property = element.GetType().GetProperty("IsVisible", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (property?.PropertyType == typeof(bool))
                return (bool)(property.GetValue(element) ?? false);
        }
        catch
        {
            return false;
        }

        return true;
    }

    private static Entity? TryGetUiEntity(object element)
    {
        try
        {
            var property = element.GetType().GetProperty("Entity", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            return property?.GetValue(element) as Entity;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<object> GetUiChildren(object element)
    {
        object? children;
        try
        {
            var property = element.GetType().GetProperty("Children", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            children = property?.GetValue(element);
        }
        catch
        {
            yield break;
        }

        if (children is not IEnumerable enumerable)
            yield break;

        foreach (var child in enumerable)
        {
            if (child != null)
                yield return child;
        }
    }

    private static RectangleF TryGetUiRect(object element)
    {
        try
        {
            var property = element.GetType().GetProperty("GetClientRectCache", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (property?.GetValue(element) is RectangleF rect)
                return rect;
        }
        catch
        {
            // Fall through to method fallback.
        }

        try
        {
            var method = element.GetType().GetMethod("GetClientRect", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, Type.DefaultBinder, Type.EmptyTypes, null);
            if (method?.Invoke(element, Array.Empty<object>()) is RectangleF rect)
                return rect;
        }
        catch
        {
            // Ignore invalid UI element reads.
        }

        return default;
    }

    private static bool IsUsableRect(RectangleF rect)
    {
        return rect.Width > 4 && rect.Height > 4 && rect.Left >= -10000 && rect.Top >= -10000;
    }

    private void TrackInventoryItem(ServerInventory.InventSlotItem item, ItemLocation location, HashSet<long> currentKeys)
    {
        if (item?.Item == null)
            return;

        var key = GetEntityKey(item.Item, item);
        currentKeys.Add(key);

        if (_tablets.TryGetValue(key, out var existing))
        {
            existing.UpdateRect(new VisibleItemRef { Key = key, InventoryItem = item, Location = location });
            return;
        }

        if (_discarded.Contains(key) || IsAlreadyPending(key))
            return;

        if (!IsTabletEntity(item.Item))
        {
            _discarded.Add(key);
            return;
        }

        EnqueuePending(new VisibleItemRef { Key = key, InventoryItem = item, Location = location });
    }

    private void TrackStashItem(NormalInventoryItem item, ItemLocation location, HashSet<long> currentKeys)
    {
        if (item?.Item == null)
            return;

        var key = GetEntityKey(item.Item, item);
        currentKeys.Add(key);

        if (_tablets.TryGetValue(key, out var existing))
        {
            existing.UpdateRect(new VisibleItemRef { Key = key, StashItem = item, Location = location });
            return;
        }

        if (_discarded.Contains(key) || IsAlreadyPending(key))
            return;

        if (!IsTabletEntity(item.Item))
        {
            _discarded.Add(key);
            return;
        }

        EnqueuePending(new VisibleItemRef { Key = key, StashItem = item, Location = location });
    }

    private void TrackUiElementItem(object element, Entity entity, ItemLocation location, HashSet<long> currentKeys)
    {
        if (entity == null)
            return;

        var key = GetEntityKey(entity, element);
        currentKeys.Add(key);

        if (_tablets.TryGetValue(key, out var existing))
        {
            existing.UpdateRect(new VisibleItemRef { Key = key, UiElementItem = element, Location = location });
            return;
        }

        if (_discarded.Contains(key) || IsAlreadyPending(key))
            return;

        if (!IsTabletEntity(entity))
        {
            _discarded.Add(key);
            return;
        }

        EnqueuePending(new VisibleItemRef { Key = key, UiElementItem = element, Location = location });
    }

    private bool IsAlreadyPending(long key)
    {
        return _pendingKeys.Contains(key);
    }

    private void EnqueuePending(VisibleItemRef itemRef)
    {
        _pending.Enqueue(itemRef);
        _pendingKeys.Add(itemRef.Key);
    }

    private bool IsTabletEntity(Entity? entity)
    {
        return entity != null && !string.IsNullOrEmpty(entity.Metadata) && entity.Metadata.Contains("TowerAugment", StringComparison.OrdinalIgnoreCase);
    }

    private void ProcessPendingItems(int maxItems)
    {
        maxItems = Math.Max(1, maxItems);
        var processed = 0;

        while (_pending.Count > 0 && processed < maxItems)
        {
            processed++;
            var itemRef = _pending.Dequeue();
            _pendingKeys.Remove(itemRef.Key);

            if (_tablets.ContainsKey(itemRef.Key) || _discarded.Contains(itemRef.Key))
                continue;

            var entity = itemRef.Entity;
            if (entity == null || string.IsNullOrEmpty(entity.Metadata) || !entity.Metadata.Contains("TowerAugment", StringComparison.OrdinalIgnoreCase))
            {
                _discarded.Add(itemRef.Key);
                continue;
            }

            try
            {
                var tablet = new TabletItem(itemRef, Settings.LogSeenMods.Value);
                if (!TabletBonusCatalog.IsKnownTabletType(tablet.TabletTypeKey))
                    DebugLog($"Unknown tablet type for metadata: {tablet.Metadata}");

                _tablets[itemRef.Key] = tablet;
                LogTabletMods(tablet);
            }
            catch (Exception ex)
            {
                _discarded.Add(itemRef.Key);
                DebugLog($"Tablet parse failed: {ex.Message}");
            }
        }
    }

    private List<TabletMatchResult> FindMatches(TabletItem tablet)
    {
        if (_matchCache.TryGetValue(tablet.Key, out var cached) && cached.SettingsVersion == _settingsVersion)
            return cached.Matches;

        var results = new List<TabletMatchResult>();
        var typeSettings = Settings.TabletTypes.FirstOrDefault(x => string.Equals(x.Key, tablet.TabletTypeKey, StringComparison.OrdinalIgnoreCase));
        if (typeSettings == null || !typeSettings.Enabled)
        {
            _matchCache[tablet.Key] = new CachedTabletMatches(_settingsVersion, results);
            return results;
        }

        foreach (var group in typeSettings.Groups)
        {
            if (!group.Enabled || group.SelectedBonusIds.Count == 0)
                continue;

            if (tablet.UsesLeft < group.MinimumUsesLeft)
                continue;

            var matchedBonuses = 0;
            foreach (var bonusId in group.SelectedBonusIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var bonus = TabletBonusCatalog.Find(typeSettings.Key, bonusId);
                if (bonus == null)
                    continue;

                if (bonus.Matches(tablet))
                    matchedBonuses++;
            }

            if (matchedBonuses >= group.MinimumMatchedBonuses)
                results.Add(new TabletMatchResult(group, matchedBonuses));
        }

        _matchCache[tablet.Key] = new CachedTabletMatches(_settingsVersion, results);
        return results;
    }

    private void RemoveMissingItems(HashSet<long> currentKeys)
    {
        foreach (var key in _tablets.Keys.Where(key => !currentKeys.Contains(key)).ToArray())
        {
            _tablets.Remove(key);
            _matchCache.Remove(key);
        }

        if (_discarded.Count > 2000)
            _discarded.Clear();
    }

    private bool IsQuadTab()
    {
        try
        {
            if (_stashPanel?.IsVisible == true && _stashPanel.VisibleStash != null)
                return _stashPanel.VisibleStash.TotalBoxesInInventoryRow == 24;
            if (_guildStashPanel?.IsVisible == true && _guildStashPanel.VisibleStash != null)
                return _guildStashPanel.VisibleStash.TotalBoxesInInventoryRow == 24;
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static long GetEntityKey(Entity entity, object fallback)
    {
        try
        {
            if (entity.Address != 0)
                return entity.Address;
        }
        catch
        {
            // Fall back to UI item hash when Address is not available.
        }

        return fallback.GetHashCode();
    }

    private void ClearRuntimeState()
    {
        _tablets.Clear();
        _matchCache.Clear();
        _discarded.Clear();
        _pending.Clear();
        _pendingKeys.Clear();
        _lastContextHash = 0;
        _lastFastRefreshMs = 0;
        _lastFullScanMs = 0;
    }

    private void DrawGroupLabels(TabletItem tablet, IReadOnlyList<TabletMatchResult> matches)
    {
        if (matches.Count == 0)
            return;

        var x = tablet.Rect.Left + 3;
        var y = tablet.Rect.Top + 2;
        var lineHeight = Math.Max(10f, 14f * Settings.LabelScale.Value);

        for (var i = 0; i < matches.Count; i++)
        {
            var label = matches[i].Group.Name;
            if (Settings.ShowUsesLeft.Value)
                label += $" ({tablet.UsesLeft})";

            Graphics.DrawText(label, new Vector2(x, y + i * lineHeight), FontAlign.Left);
        }
    }

    private void DrawHighlight(RectangleF rect, Color color, ItemLocation location)
    {
        if (location == ItemLocation.QuadStash)
            color = Color.FromArgb(color.A, color.R, color.G, color.B);

        DrawBorderHighlight(rect, color, Settings.BorderThickness.Value);
    }

    private void DrawBorderHighlight(RectangleF rect, Color color, int thickness)
    {
        var scale = thickness - 1;
        var innerX = (int)rect.X + 1 + (int)(0.5 * scale);
        var innerY = (int)rect.Y + 1 + (int)(0.5 * scale);
        var innerWidth = (int)rect.Width - 1 - scale;
        var innerHeight = (int)rect.Height - 1 - scale;
        var scaledFrame = new RectangleF(innerX, innerY, innerWidth, innerHeight);
        Graphics.DrawFrame(scaledFrame, color, thickness);
    }

    private void LoadExistingDebugKeys()
    {
        try
        {
            var path = GetDebugDumpPath();
            if (!File.Exists(path))
                return;

            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                var key = BuildDebugUniqueKeyFromDumpLine(line);
                if (!string.IsNullOrWhiteSpace(key))
                    _loggedMods.Add(key);
            }
        }
        catch (Exception ex)
        {
            DebugWindow.LogMsg("[TabletHelper] Failed to load existing debug txt keys: " + ex.Message, 10);
        }
    }

    private static string BuildDebugUniqueKey(TabletItem tablet, TabletModInfo mod)
    {
        return string.Join("|", new[]
        {
            NormalizeDebugKey(tablet.TabletTypeKey),
            NormalizeDebugKey(tablet.Metadata),
            NormalizeDebugKey(mod.Source),
            NormalizeDebugKey(mod.AffixType),
            NormalizeDebugKey(mod.Name),
            NormalizeDebugKey(mod.RawName),
            NormalizeDebugKey(mod.Group)
        });
    }

    private static string BuildDebugUniqueKeyFromDumpLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
            return string.Empty;

        var tabletKey = ExtractDumpField(line, "TabletKey");
        var metadata = ExtractDumpField(line, "Metadata");
        var source = ExtractDumpField(line, "Source");
        var affix = ExtractDumpField(line, "Affix");
        var name = ExtractDumpField(line, "BonusName");
        var rawName = ExtractDumpField(line, "RawName");
        var group = ExtractDumpField(line, "Group");

        if (string.IsNullOrWhiteSpace(tabletKey) || string.IsNullOrWhiteSpace(name + rawName))
            return string.Empty;

        return string.Join("|", new[]
        {
            NormalizeDebugKey(tabletKey),
            NormalizeDebugKey(metadata),
            NormalizeDebugKey(source),
            NormalizeDebugKey(affix),
            NormalizeDebugKey(name),
            NormalizeDebugKey(rawName),
            NormalizeDebugKey(group)
        });
    }

    private static string ExtractDumpField(string line, string fieldName)
    {
        var prefix = " | " + fieldName + "=";
        var start = line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return string.Empty;

        start += prefix.Length;
        var end = line.IndexOf(" | ", start, StringComparison.Ordinal);
        return end < 0 ? line[start..].Trim() : line[start..end].Trim();
    }

    private static string NormalizeDebugKey(string value)
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

    private void LogTabletMods(TabletItem tablet)
    {
        if (!Settings.LogSeenMods.Value || tablet == null)
            return;

        var lines = new List<string>();

        foreach (var mod in tablet.Mods)
        {
            var key = BuildDebugUniqueKey(tablet, mod);
            if (!_loggedMods.Add(key))
                continue;

            lines.Add(BuildDebugDumpLine(tablet, mod));
            DebugWindow.LogMsg($"[TabletHelper] dumped {tablet.TabletTypeName}: {mod.AffixType}/{mod.Source} {mod.Name}", 5);
        }

        if (lines.Count == 0)
            return;

        AppendDebugDumpLines(lines);
    }

    private static string BuildDebugDumpLine(TabletItem tablet, TabletModInfo mod)
    {
        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        AppendField(sb, "Tablet", tablet.TabletTypeName);
        AppendField(sb, "TabletKey", tablet.TabletTypeKey);
        AppendField(sb, "Metadata", tablet.Metadata);
        AppendField(sb, "Source", mod.Source);
        AppendField(sb, "Affix", mod.AffixType);
        AppendField(sb, "BonusName", mod.Name);
        AppendField(sb, "RawName", mod.RawName);
        AppendField(sb, "DisplayName", mod.DisplayName);
        AppendField(sb, "Group", mod.Group);
        AppendField(sb, "Translation", mod.Translation);
        AppendField(sb, "Values", string.Join(",", mod.Values));
        AppendField(sb, "UniqueKey", BuildDebugUniqueKey(tablet, mod));
        AppendField(sb, "ModRecord", mod.ModRecordDebug);
        return sb.ToString();
    }

    private static void AppendField(StringBuilder sb, string name, string value)
    {
        sb.Append(" | ");
        sb.Append(name);
        sb.Append('=');
        sb.Append(SanitizeDebugField(value));
    }

    private static string SanitizeDebugField(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
    }

    private void AppendDebugDumpLines(IReadOnlyCollection<string> lines)
    {
        if (_debugFileWriteFailed)
            return;

        try
        {
            var directory = GetDebugDumpDirectory();
            Directory.CreateDirectory(directory);
            var path = GetDebugDumpPath();

            if (!File.Exists(path))
            {
                File.AppendAllText(path, "# Tablet Helper tablet mod dump\r\n", Encoding.UTF8);
                File.AppendAllText(path, "# Format: Tablet -> Source/Affix -> internal bonus fields. Send this file back for exact catalog building.\r\n", Encoding.UTF8);
            }

            File.AppendAllLines(path, lines, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _debugFileWriteFailed = true;
            DebugWindow.LogMsg("[TabletHelper] Failed to write debug txt: " + ex.Message, 10);
        }
    }

    private void ClearDebugDumpFile()
    {
        try
        {
            var path = GetDebugDumpPath();
            if (File.Exists(path))
                File.Delete(path);

            _loggedMods.Clear();
            _debugFileWriteFailed = false;
            DebugWindow.LogMsg("[TabletHelper] Debug txt cleared: " + path, 5);
        }
        catch (Exception ex)
        {
            DebugWindow.LogMsg("[TabletHelper] Failed to clear debug txt: " + ex.Message, 10);
        }
    }

    private static string GetDebugDumpPath()
    {
        return Path.Combine(GetDebugDumpDirectory(), "TabletModsDump.txt");
    }

    private static string GetDebugDumpDirectory()
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins", "Temp", "TabletHelper");
    }

    private void DebugLog(string message)
    {
        if (Settings.LogSeenMods.Value)
            DebugWindow.LogMsg("[TabletHelper] " + message, 10);
    }


    private static string GetNextGroupName(TabletTypeSettings typeSettings)
    {
        var usedNumbers = new HashSet<int>();

        foreach (var group in typeSettings.Groups ?? Enumerable.Empty<TabletRuleGroup>())
        {
            var name = group?.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            const string prefix = "Group ";
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var numberText = name.Substring(prefix.Length).Trim();
            if (int.TryParse(numberText, out var number) && number > 0)
                usedNumbers.Add(number);
        }

        var next = 1;
        while (usedNumbers.Contains(next))
            next++;

        return $"Group {next}";
    }

    private static Color DefaultGroupColor(int index)
    {
        var colors = new[]
        {
            Color.DeepSkyBlue,
            Color.LimeGreen,
            Color.Gold,
            Color.OrangeRed,
            Color.MediumOrchid,
            Color.White,
            Color.HotPink
        };
        return colors[Math.Abs(index) % colors.Length];
    }

    private static void Checkbox(string label, ExileCore2.Shared.Nodes.ToggleNode node)
    {
        var value = node.Value;
        if (ImGui.Checkbox(label, ref value))
            node.Value = value;
    }

    private static void SliderInt(string label, ExileCore2.Shared.Nodes.RangeNode<int> node)
    {
        var value = node.Value;
        if (ImGui.SliderInt(label, ref value, node.Min, node.Max))
            node.Value = value;
    }

    private static void SliderFloat(string label, ExileCore2.Shared.Nodes.RangeNode<float> node)
    {
        var value = node.Value;
        if (ImGui.SliderFloat(label, ref value, node.Min, node.Max))
            node.Value = value;
    }

    private static bool DrawColorEdit(string label, Color color, Action<Color> setColor)
    {
        var vec = new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
        if (!ImGui.ColorEdit4(label, ref vec))
            return false;

        var a = Math.Clamp((int)(vec.W * 255f), 0, 255);
        var r = Math.Clamp((int)(vec.X * 255f), 0, 255);
        var g = Math.Clamp((int)(vec.Y * 255f), 0, 255);
        var b = Math.Clamp((int)(vec.Z * 255f), 0, 255);
        setColor(Color.FromArgb(a, r, g, b));
        return true;
    }

    private void CollapseSettingsNodeOnFirstUse(string key)
    {
        if (_collapsedSettingsNodesThisSession.Add(key))
            ImGui.SetNextItemOpen(false, ImGuiCond.Always);
    }

    private void MarkMatchingSettingsChanged()
    {
        unchecked
        {
            _settingsVersion++;
            if (_settingsVersion == 0)
                _settingsVersion = 1;
        }

        _matchCache.Clear();
    }
}

internal sealed class CachedTabletMatches
{
    public int SettingsVersion { get; }
    public List<TabletMatchResult> Matches { get; }

    public CachedTabletMatches(int settingsVersion, List<TabletMatchResult> matches)
    {
        SettingsVersion = settingsVersion;
        Matches = matches;
    }
}

internal sealed class TabletMatchResult
{
    public TabletRuleGroup Group { get; }
    public int MatchedBonuses { get; }

    public TabletMatchResult(TabletRuleGroup group, int matchedBonuses)
    {
        Group = group;
        MatchedBonuses = matchedBonuses;
    }
}
