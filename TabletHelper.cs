using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
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

    private const string PluginVersion = "v1.4";

    // Legacy full path is kept as a fallback. The preferred path is dynamic:
    // StashElement.StashInventoryPanel.Children[IndexVisibleStash] -> 0 -> 0 -> 0 -> 1 -> 0 -> 0 -> 0.
    private static readonly int[] LegacySpecialTabletStashItemsContainerPath = { 2, 0, 0, 0, 1, 1, 6, 0, 0, 0, 1, 0, 0, 0 };
    private static readonly int[] SpecialTabletStashItemsRelativePath = { 0, 0, 0, 1, 0, 0, 0 };
    private const int MaxSpecialStashTargetedScanNodes = 900;
    private const int MaxSpecialStashTargetedScanDepth = 12;

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

            // Priority groups are ordered before normal matches, so the first match controls
            // the highlight color while all matching group names remain visible on the label.
            DrawHighlight(tablet, matches);

            if (Settings.ShowGroupLabel.Value && tablet.Location != ItemLocation.QuadStash)
                DrawGroupLabels(tablet, matches);
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
        DrawEnumCombo("Highlight style", Settings.HighlightMode, v => Settings.HighlightMode = v);
        SliderInt("Highlight thickness", Settings.BorderThickness);
        ImGui.TextDisabled("Border draws the classic item frame. Cross draws an X over the tablet. Star draws a marker in the top-left corner.");

        ImGui.Separator();
        DrawEnumCombo("Label text color", Settings.LabelTextMode, v => Settings.LabelTextMode = v);
        Checkbox("Label dark background", Settings.LabelBackgroundEnabled);
        Checkbox("Label black outline", Settings.LabelOutlineEnabled);
        if (Settings.LabelBackgroundEnabled.Value)
            SliderInt("Label background alpha", Settings.LabelBackgroundAlpha);
        SliderFloat("Label scale", Settings.LabelScale);

        ImGui.Separator();
        Checkbox("Smooth overlay refresh", Settings.SmoothUiRefresh);
        SliderInt("Known tablet rect refresh interval ms", Settings.FastRefreshIntervalMs);
        SliderInt("Full discovery scan interval ms", Settings.ScanIntervalMs);
        SliderInt("Items parsed per tick", Settings.ItemsPerTick);
        ImGui.TextDisabled("Fast refresh only updates already known tablet rectangles. Full discovery finds new/missing items at a lower rate.");

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
                ImGui.TextDisabled("All matching groups are shown on the label. Priority groups control highlight color before normal groups.");

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
        var header = BuildGroupHeader(group);

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

            var highlightPriority = group.HighlightPriority;
            ImGui.SameLine();
            if (ImGui.Checkbox("Priority highlight", ref highlightPriority))
            {
                group.HighlightPriority = highlightPriority;
                MarkMatchingSettingsChanged();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("When this group matches, its color is preferred before non-priority matching groups.");

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

            var minUses = group.MinimumUsesLeft;
            ImGui.SetNextItemWidth(90);
            if (ImGui.InputInt("Minimum uses left", ref minUses, 1, 1))
            {
                group.MinimumUsesLeft = Math.Clamp(minUses, 0, 20);
                MarkMatchingSettingsChanged();
            }

            if (DrawColorEdit("Group color", group.Color, c => group.Color = c))
                MarkMatchingSettingsChanged();

            ImGui.Spacing();
            if (ImGui.BeginTabBar("##bonus_role_tabs", ImGuiTabBarFlags.None))
            {
                DrawBonusRoleTab(typeSettings.Key, group, BonusRole.Match);
                DrawBonusRoleTab(typeSettings.Key, group, BonusRole.Require);
                DrawBonusRoleTab(typeSettings.Key, group, BonusRole.Exclude);
                ImGui.EndTabBar();
            }

            ImGui.TreePop();
        }
        ImGui.PopID();
    }

    private void DrawBonusRoleTab(string tabletTypeKey, TabletRuleGroup group, BonusRole role)
    {
        var list = GetBonusRoleList(group, role);
        var accent = GetBonusRoleAccent(role);

        var title = role switch
        {
            BonusRole.Require => $"Require ({list.Count})###tab_require",
            BonusRole.Exclude => $"Exclude ({list.Count})###tab_exclude",
            _ => $"Match ({list.Count})###tab_match"
        };

        // Tint only the tab label, not the tab body.
        ImGui.PushStyleColor(ImGuiCol.Text, accent);
        var open = ImGui.BeginTabItem(title);
        ImGui.PopStyleColor();
        if (!open)
            return;

        switch (role)
        {
            case BonusRole.Require:
                HelpText("Tablet must also have at least the set number of these (AND). Leave empty to ignore.");
                DrawMinimumInput("Minimum required", group.MinimumRequiredBonuses, v => group.MinimumRequiredBonuses = v);
                break;
            case BonusRole.Exclude:
                if (group.SelectedBonusIds.Count == 0)
                    HelpText("This group has no Match bonuses, so it is a blocklist: a tablet with any of these is never highlighted (Global tab blocks every tablet; a tablet tab blocks that type).");
                else
                    HelpText("Skip this group's highlight if the tablet has any of these (NOT). Exclude does not remove Match or Require selections; clearing Exclude restores the previous highlight behavior.");
                break;
            default:
                HelpText("Highlight when at least the set number of these are present.");
                if (group.SelectedBonusIds.Count == 0 && group.ExcludedBonusIds.Count > 0)
                    HelpText("Empty + Exclude set = this group is acting as a blocklist (see the Exclude tab).");
                DrawMinimumInput("Minimum to match", group.MinimumMatchedBonuses, v => group.MinimumMatchedBonuses = v);
                break;
        }

        var searchKey = tabletTypeKey + ":" + group.Id + ":" + role;
        if (!_bonusSearch.TryGetValue(searchKey, out var search))
            search = string.Empty;

        ImGui.SetNextItemWidth(300);
        if (ImGui.InputText("Search##" + role, ref search, 128))
            _bonusSearch[searchKey] = search;

        ImGui.SameLine();
        if (ImGui.SmallButton("Clear##" + role) && list.Count > 0)
        {
            list.Clear();
            MarkMatchingSettingsChanged();
        }

        DrawBonusChecklist(tabletTypeKey, group, role, search);

        ImGui.EndTabItem();
    }

    private void DrawBonusChecklist(string tabletTypeKey, TabletRuleGroup group, BonusRole role, string search)
    {
        var bonuses = TabletBonusCatalog.GetBonusesFor(tabletTypeKey)
            .Where(b => string.IsNullOrWhiteSpace(search)
                || b.Label.Contains(search, StringComparison.OrdinalIgnoreCase)
                || b.Category.Contains(search, StringComparison.OrdinalIgnoreCase)
                || b.Id.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var list = GetBonusRoleList(group, role);
        var accent = GetBonusRoleAccent(role);

        var childHeight = Math.Clamp(bonuses.Count * 24 + 28, 140, 360);
        var childVisible = ImGui.BeginChild("##bonus_list_" + role, new Vector2(0, childHeight), ImGuiChildFlags.Border);
        if (childVisible)
        {
            ImGui.PushStyleColor(ImGuiCol.CheckMark, accent);

            foreach (var bonus in bonuses)
            {
                var isSelected = list.Contains(bonus.Id, StringComparer.OrdinalIgnoreCase);
                if (ImGui.Checkbox($"{bonus.Label}##{role}_{bonus.Id}", ref isSelected))
                    SetBonusRole(group, role, bonus.Id, isSelected);

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{bonus.Category} | {bonus.Id}{DescribeOtherRole(group, role, bonus.Id)}");
            }

            if (bonuses.Count == 0)
                ImGui.TextDisabled("No bonuses match the search.");

            ImGui.PopStyleColor();
        }

        ImGui.EndChild();
    }

    private void DrawMinimumInput(string label, int current, Action<int> setValue)
    {
        var value = current;
        ImGui.SetNextItemWidth(90);
        if (ImGui.InputInt(label, ref value, 1, 1))
        {
            setValue(Math.Clamp(value, 1, 20));
            MarkMatchingSettingsChanged();
        }
    }

    // Bonus roles are independent toggles. Require is an additional AND gate, while Exclude
    // is a soft NOT/veto gate. Exclude intentionally does not delete Match or Require selections:
    // users can temporarily block a bonus and later clear Exclude to restore the previous rule.
    private void SetBonusRole(TabletRuleGroup group, BonusRole role, string bonusId, bool selected)
    {
        var list = GetBonusRoleList(group, role);
        var changed = selected
            ? AddBonusId(list, bonusId)
            : RemoveBonusId(list, bonusId);

        if (changed)
            MarkMatchingSettingsChanged();
    }

    private static string DescribeOtherRole(TabletRuleGroup group, BonusRole role, string bonusId)
    {
        var isInMatch = group.SelectedBonusIds.Contains(bonusId, StringComparer.OrdinalIgnoreCase);
        var isInRequire = group.RequiredBonusIds.Contains(bonusId, StringComparer.OrdinalIgnoreCase);
        var isInExclude = group.ExcludedBonusIds.Contains(bonusId, StringComparer.OrdinalIgnoreCase);

        return role switch
        {
            BonusRole.Match when isInRequire && isInExclude => "\nAlso required. Currently excluded, so this bonus acts as a temporary veto until Exclude is cleared.",
            BonusRole.Match when isInRequire => "\nAlso required by this group.",
            BonusRole.Match when isInExclude => "\nCurrently excluded, so this Match entry is temporarily vetoed until Exclude is cleared.",
            BonusRole.Require when isInMatch && isInExclude => "\nAlso in Match. Currently excluded, so this requirement is temporarily vetoed until Exclude is cleared.",
            BonusRole.Require when isInMatch => "\nAlso in Match. Selecting here keeps the Match selection.",
            BonusRole.Require when isInExclude => "\nCurrently excluded, so this Require entry is temporarily vetoed until Exclude is cleared.",
            BonusRole.Exclude when isInMatch || isInRequire => "\nAlso selected in Match/Require. Exclude is a soft veto and will not remove those selections.",
            _ => string.Empty
        };
    }

    private static bool AddBonusId(List<string> list, string bonusId)
    {
        if (list == null || string.IsNullOrWhiteSpace(bonusId) || list.Contains(bonusId, StringComparer.OrdinalIgnoreCase))
            return false;

        list.Add(bonusId);
        return true;
    }

    private static bool RemoveBonusId(List<string> list, string bonusId)
    {
        return list.RemoveAll(x => string.Equals(x, bonusId, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    private static List<string> GetBonusRoleList(TabletRuleGroup group, BonusRole role)
    {
        return role switch
        {
            BonusRole.Require => group.RequiredBonusIds,
            BonusRole.Exclude => group.ExcludedBonusIds,
            _ => group.SelectedBonusIds
        };
    }

    private static Vector4 GetBonusRoleAccent(BonusRole role)
    {
        return role switch
        {
            BonusRole.Require => new Vector4(0.40f, 0.80f, 1.00f, 1f), // cyan: required "AND"
            BonusRole.Exclude => new Vector4(1.00f, 0.45f, 0.45f, 1f), // red: excluded "NOT"
            _ => new Vector4(0.55f, 0.90f, 0.55f, 1f)                  // green: wanted "match"
        };
    }

    private static string BuildGroupHeader(TabletRuleGroup group)
    {
        var match = group.SelectedBonusIds?.Count ?? 0;
        var require = group.RequiredBonusIds?.Count ?? 0;
        var exclude = group.ExcludedBonusIds?.Count ?? 0;

        // No Match bonuses but Exclude set -> the group acts as a blocklist.
        if (match == 0 && exclude > 0)
            return $"{group.Name}  [blocklist | {exclude} excl]";

        var badge = $"{match} match";
        if (require > 0)
            badge += $" | {require} req";
        if (exclude > 0)
            badge += $" | {exclude} excl";

        return $"{group.Name}  [{badge}]";
    }

    private static void HelpText(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
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
        // their slots through VisibleStash.VisibleInventoryItems. Some Tablet stash pages
        // expose a non-empty VisibleInventoryItems collection with stale or incomplete slots,
        // so the targeted special-tab scan runs whenever the stash panel is visible.
        if (_stashPanel?.IsVisible == true)
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

        if (_guildStashPanel?.IsVisible == true)
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
        var foundAnyTablet = false;

        // Fast path for the known Tablet sub-tab item container.
        // This still works for the first special page on the current UI layout.
        var directContainer = TryGetSpecialStashItemsContainer(root);
        if (directContainer != null && IsUiElementVisible(directContainer))
            foundAnyTablet |= ScanSpecialStashUiSubtree(directContainer, currentKeys, MaxSpecialStashTargetedScanNodes, MaxSpecialStashTargetedScanDepth);

        // Page buttons inside the Tablet stash can swap the active item container without
        // changing StashElement.IndexVisibleStash or VisibleStash.VisibleInventoryItems.
        // Scan only the currently visible stash tab root, not the full UI tree, so pages 2/3/4
        // are discovered without enabling the expensive debug fallback.
        var visibleTabRoot = TryGetDynamicVisibleStashRoot(root);
        if (visibleTabRoot != null && !ReferenceEquals(visibleTabRoot, directContainer) && IsUiElementVisible(visibleTabRoot))
            foundAnyTablet |= ScanSpecialStashUiSubtree(visibleTabRoot, currentKeys, MaxSpecialStashTargetedScanNodes, MaxSpecialStashTargetedScanDepth);

        return foundAnyTablet;
    }

    private bool ScanSpecialStashUiSubtree(object root, HashSet<long> currentKeys, int maxNodes, int maxDepth)
    {
        if (root == null || maxNodes <= 0 || maxDepth < 0)
            return false;

        var visited = new HashSet<int>();
        var stack = new Stack<(object Element, int Depth)>();
        stack.Push((root, 0));
        var visitedNodes = 0;
        var foundAnyTablet = false;

        while (stack.Count > 0 && visitedNodes < maxNodes)
        {
            var (element, depth) = stack.Pop();
            if (element == null || depth > maxDepth)
                continue;

            var elementHash = RuntimeHelpers.GetHashCode(element);
            if (!visited.Add(elementHash))
                continue;

            visitedNodes++;

            if (!IsUiElementVisible(element))
                continue;

            var entity = TryGetUiEntity(element);
            if (entity != null && IsTabletEntity(entity))
            {
                var rect = TryGetUiRect(element);
                if (IsUsableRect(rect))
                {
                    TrackUiElementItem(element, entity, ItemLocation.SpecialStash, currentKeys);
                    foundAnyTablet = true;
                }
            }

            foreach (var child in GetUiChildren(element))
                stack.Push((child, depth + 1));
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
        var visibleTabRoot = TryGetDynamicVisibleStashRoot(stashElement);
        return visibleTabRoot == null ? null : TryGetChildByPath(visibleTabRoot, SpecialTabletStashItemsRelativePath);
    }

    private static object? TryGetDynamicVisibleStashRoot(object stashElement)
    {
        var inventoryPanel = TryGetObjectProperty(stashElement, "StashInventoryPanel");
        if (inventoryPanel == null)
            return null;

        var visibleIndex = TryGetIntProperty(stashElement, "IndexVisibleStash");
        if (visibleIndex < 0)
            return null;

        return TryGetChildAt(inventoryPanel, visibleIndex);
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
        ScanSpecialStashUiSubtree(root, currentKeys, maxNodes, maxDepth);
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

        if (!TabletBonusCatalog.IsKnownTabletType(tablet.TabletTypeKey))
        {
            _matchCache[tablet.Key] = new CachedTabletMatches(_settingsVersion, results);
            return results;
        }

        // A blocklist (exclude-only) group suppresses the tablet entirely, before any group can match.
        if (!IsTabletBlocked(tablet))
        {
            foreach (var typeSettings in GetMatchingRuleScopes(tablet.TabletTypeKey))
                AddMatchesForRuleScope(tablet, typeSettings, results);

            PrioritizeHighlightMatches(results);
        }

        _matchCache[tablet.Key] = new CachedTabletMatches(_settingsVersion, results);
        return results;
    }

    // An "exclude-only" group (no Match bonuses, but with Exclude bonuses) acts as a blocklist:
    // if the tablet carries any of its excluded bonuses it is never highlighted, overriding every
    // other group. A blocklist in the Global tab blocks all tablets; in a tablet tab it blocks that type.
    private bool IsTabletBlocked(TabletItem tablet)
    {
        foreach (var typeSettings in GetMatchingRuleScopes(tablet.TabletTypeKey))
        {
            if (typeSettings == null || !typeSettings.Enabled || typeSettings.Groups == null)
                continue;

            foreach (var group in typeSettings.Groups)
            {
                group.EnsureDefaults();

                if (!group.Enabled || group.SelectedBonusIds.Count > 0 || group.ExcludedBonusIds.Count == 0)
                    continue;

                if (CountMatchingBonuses(tablet, typeSettings.Key, group.ExcludedBonusIds) > 0)
                    return true;
            }
        }

        return false;
    }

    private IEnumerable<TabletTypeSettings> GetMatchingRuleScopes(string tabletTypeKey)
    {
        foreach (var typeSettings in Settings.TabletTypes)
        {
            if (string.Equals(typeSettings.Key, tabletTypeKey, StringComparison.OrdinalIgnoreCase))
                yield return typeSettings;
        }

        foreach (var typeSettings in Settings.TabletTypes)
        {
            if (string.Equals(typeSettings.Key, TabletTypeKeys.Global, StringComparison.OrdinalIgnoreCase))
                yield return typeSettings;
        }
    }

    private static void AddMatchesForRuleScope(TabletItem tablet, TabletTypeSettings typeSettings, List<TabletMatchResult> results)
    {
        if (typeSettings == null || !typeSettings.Enabled || typeSettings.Groups == null)
            return;

        foreach (var group in typeSettings.Groups)
        {
            group.EnsureDefaults();

            if (!group.Enabled || group.SelectedBonusIds.Count == 0)
                continue;

            if (tablet.UsesLeft < group.MinimumUsesLeft)
                continue;

            var matchedBonuses = CountMatchingBonuses(tablet, typeSettings.Key, group.SelectedBonusIds);
            if (matchedBonuses < group.MinimumMatchedBonuses)
                continue;

            // Optional "AND" gate: the tablet must also carry the required bonuses.
            if (group.RequiredBonusIds.Count > 0 &&
                CountMatchingBonuses(tablet, typeSettings.Key, group.RequiredBonusIds) < group.MinimumRequiredBonuses)
                continue;

            // Optional "NOT" gate: skip if the tablet carries any excluded bonus.
            if (group.ExcludedBonusIds.Count > 0 &&
                CountMatchingBonuses(tablet, typeSettings.Key, group.ExcludedBonusIds) > 0)
                continue;

            results.Add(new TabletMatchResult(group, matchedBonuses));
        }
    }

    private static int CountMatchingBonuses(TabletItem tablet, string tabletTypeKey, IReadOnlyList<string> bonusIds)
    {
        if (bonusIds == null || bonusIds.Count == 0)
            return 0;

        var count = 0;
        for (var i = 0; i < bonusIds.Count; i++)
        {
            var bonusId = bonusIds[i];
            if (string.IsNullOrWhiteSpace(bonusId) || HasEarlierBonusId(bonusIds, bonusId, i))
                continue;

            var bonus = TabletBonusCatalog.Find(tabletTypeKey, bonusId);
            if (bonus != null && bonus.Matches(tablet))
                count++;
        }

        return count;
    }

    private static bool HasEarlierBonusId(IReadOnlyList<string> bonusIds, string bonusId, int currentIndex)
    {
        for (var i = 0; i < currentIndex; i++)
        {
            if (string.Equals(bonusIds[i], bonusId, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void PrioritizeHighlightMatches(List<TabletMatchResult> results)
    {
        if (results.Count <= 1)
            return;

        var hasPriority = false;
        var hasNormal = false;

        foreach (var match in results)
        {
            if (match.Group.HighlightPriority)
                hasPriority = true;
            else
                hasNormal = true;

            if (hasPriority && hasNormal)
                break;
        }

        if (!hasPriority || !hasNormal)
            return;

        var ordered = new List<TabletMatchResult>(results.Count);

        foreach (var match in results)
        {
            if (match.Group.HighlightPriority)
                ordered.Add(match);
        }

        foreach (var match in results)
        {
            if (!match.Group.HighlightPriority)
                ordered.Add(match);
        }

        results.Clear();
        results.AddRange(ordered);
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

        var drawList = ImGui.GetForegroundDrawList();
        var font = ImGui.GetFont();
        var fontSize = GetLabelFontSize();
        var lineHeight = GetLabelLineHeight();
        var x = tablet.Rect.Left + 3;
        var y = tablet.Rect.Top + 2;

        for (var i = 0; i < matches.Count; i++)
        {
            var group = matches[i].Group;
            var label = group.Name;
            if (Settings.ShowUsesLeft.Value)
                label += $" ({tablet.UsesLeft})";

            var position = new Vector2(x, y + i * lineHeight);
            var textSize = GetScaledTextSize(label, fontSize);

            if (Settings.LabelBackgroundEnabled.Value)
            {
                const float paddingX = 3f;
                const float paddingY = 1f;
                var alpha = Math.Clamp(Settings.LabelBackgroundAlpha.Value, 0, 255);
                var backgroundColor = ToImGuiColor(Color.FromArgb(alpha, 0, 0, 0));
                drawList.AddRectFilled(
                    new Vector2(position.X - paddingX, position.Y - paddingY),
                    new Vector2(position.X + textSize.X + paddingX, position.Y + textSize.Y + paddingY),
                    backgroundColor,
                    2f);
            }

            var textColor = GetLabelTextColor(group.Color);
            if (Settings.LabelOutlineEnabled.Value)
                DrawOutlinedText(drawList, font, fontSize, position, textColor, label);
            else
                drawList.AddText(font, fontSize, position, textColor, label);
        }
    }

    private void DrawHighlight(TabletItem tablet, IReadOnlyList<TabletMatchResult> matches)
    {
        if (matches.Count == 0)
            return;

        var color = matches[0].Group.Color;
        var thickness = Math.Clamp(Settings.BorderThickness.Value, Settings.BorderThickness.Min, Settings.BorderThickness.Max);

        switch (Settings.HighlightMode)
        {
            case TabletHighlightMode.Cross:
                DrawCrossHighlight(tablet.Rect, color, thickness);
                break;
            case TabletHighlightMode.Star:
                DrawStarHighlight(tablet, matches, color, thickness);
                break;
            default:
                DrawBorderHighlight(tablet.Rect, color, thickness);
                break;
        }
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

    private static void DrawCrossHighlight(RectangleF rect, Color color, int thickness)
    {
        var drawList = ImGui.GetForegroundDrawList();
        var lineColor = ToImGuiColor(color);
        var shadowColor = ToImGuiColor(Color.FromArgb(180, 0, 0, 0));
        var inset = Math.Max(2f, thickness);

        var topLeft = new Vector2(rect.Left + inset, rect.Top + inset);
        var topRight = new Vector2(rect.Left + rect.Width - inset, rect.Top + inset);
        var bottomLeft = new Vector2(rect.Left + inset, rect.Top + rect.Height - inset);
        var bottomRight = new Vector2(rect.Left + rect.Width - inset, rect.Top + rect.Height - inset);
        var shadowOffset = new Vector2(1f, 1f);

        drawList.AddLine(topLeft + shadowOffset, bottomRight + shadowOffset, shadowColor, thickness + 2f);
        drawList.AddLine(topRight + shadowOffset, bottomLeft + shadowOffset, shadowColor, thickness + 2f);
        drawList.AddLine(topLeft, bottomRight, lineColor, thickness);
        drawList.AddLine(topRight, bottomLeft, lineColor, thickness);
    }

    private void DrawStarHighlight(TabletItem tablet, IReadOnlyList<TabletMatchResult> matches, Color color, int thickness)
    {
        var rect = tablet.Rect;
        var labelLines = Settings.ShowGroupLabel.Value && tablet.Location != ItemLocation.QuadStash ? matches.Count : 0;
        var labelOffset = labelLines > 0 ? labelLines * GetLabelLineHeight() + 4f : 0f;
        var outerRadius = Math.Clamp(Math.Min(rect.Width, rect.Height) * 0.18f, 7f, 13f);
        var innerRadius = outerRadius * 0.45f;
        var centerX = rect.Left + outerRadius + 5f;
        var preferredCenterY = rect.Top + outerRadius + 5f + labelOffset;
        var maxCenterY = rect.Top + rect.Height - outerRadius - 4f;
        var centerY = Math.Min(preferredCenterY, maxCenterY);
        var center = new Vector2(centerX, centerY);

        var drawList = ImGui.GetForegroundDrawList();
        var starColor = ToImGuiColor(color);
        var shadowColor = ToImGuiColor(Color.FromArgb(190, 0, 0, 0));
        var shadowOffset = new Vector2(1f, 1f);
        var starThickness = Math.Max(2f, thickness);

        DrawStarLines(drawList, center + shadowOffset, outerRadius, innerRadius, shadowColor, starThickness + 2f);
        DrawStarLines(drawList, center, outerRadius, innerRadius, starColor, starThickness);
    }

    private static void DrawStarLines(ImDrawListPtr drawList, Vector2 center, float outerRadius, float innerRadius, uint color, float thickness)
    {
        var previous = GetStarPoint(center, outerRadius, innerRadius, 9);
        for (var i = 0; i < 10; i++)
        {
            var next = GetStarPoint(center, outerRadius, innerRadius, i);
            drawList.AddLine(previous, next, color, thickness);
            previous = next;
        }
    }

    private static Vector2 GetStarPoint(Vector2 center, float outerRadius, float innerRadius, int index)
    {
        var angle = -MathF.PI / 2f + index * MathF.PI / 5f;
        var radius = index % 2 == 0 ? outerRadius : innerRadius;
        return new Vector2(
            center.X + MathF.Cos(angle) * radius,
            center.Y + MathF.Sin(angle) * radius);
    }

    private float GetLabelFontSize()
    {
        var scale = Math.Clamp(Settings.LabelScale.Value, 0.5f, 2.0f);
        return Math.Max(8f, ImGui.GetFontSize() * scale);
    }

    private float GetLabelLineHeight()
    {
        return Math.Max(10f, GetLabelFontSize() + 2f);
    }

    private uint GetLabelTextColor(Color groupColor)
    {
        if (Settings.LabelTextMode == TabletLabelTextMode.GroupColor)
            return ToImGuiColor(Color.FromArgb(255, groupColor.R, groupColor.G, groupColor.B));

        return ToImGuiColor(Color.White);
    }

    private static Vector2 GetScaledTextSize(string text, float fontSize)
    {
        var baseSize = ImGui.CalcTextSize(text ?? string.Empty);
        var baseFontSize = Math.Max(1f, ImGui.GetFontSize());
        var scale = fontSize / baseFontSize;
        return new Vector2(baseSize.X * scale, baseSize.Y * scale);
    }

    private static void DrawOutlinedText(ImDrawListPtr drawList, ImFontPtr font, float fontSize, Vector2 position, uint color, string text)
    {
        var outlineColor = ToImGuiColor(Color.FromArgb(230, 0, 0, 0));
        var offset = Math.Max(1f, fontSize / 16f);

        drawList.AddText(font, fontSize, new Vector2(position.X - offset, position.Y), outlineColor, text);
        drawList.AddText(font, fontSize, new Vector2(position.X + offset, position.Y), outlineColor, text);
        drawList.AddText(font, fontSize, new Vector2(position.X, position.Y - offset), outlineColor, text);
        drawList.AddText(font, fontSize, new Vector2(position.X, position.Y + offset), outlineColor, text);
        drawList.AddText(font, fontSize, new Vector2(position.X - offset, position.Y - offset), outlineColor, text);
        drawList.AddText(font, fontSize, new Vector2(position.X + offset, position.Y + offset), outlineColor, text);
        drawList.AddText(font, fontSize, position, color, text);
    }

    private static uint ToImGuiColor(Color color)
    {
        return ImGui.ColorConvertFloat4ToU32(new Vector4(
            color.R / 255f,
            color.G / 255f,
            color.B / 255f,
            color.A / 255f));
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

    private static void DrawEnumCombo<TEnum>(string label, TEnum current, Action<TEnum> setValue) where TEnum : struct, Enum
    {
        var preview = GetEnumDisplayName(current);
        if (!ImGui.BeginCombo(label, preview))
            return;

        foreach (var value in Enum.GetValues<TEnum>())
        {
            var selected = EqualityComparer<TEnum>.Default.Equals(value, current);
            if (ImGui.Selectable(GetEnumDisplayName(value), selected))
                setValue(value);

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private static string GetEnumDisplayName<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        return (object)value switch
        {
            TabletHighlightMode.Border => "Border around tablet",
            TabletHighlightMode.Cross => "X across tablet",
            TabletHighlightMode.Star => "Star marker",
            TabletLabelTextMode.White => "White",
            TabletLabelTextMode.GroupColor => "Group color",
            _ => value.ToString()
        };
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

internal enum BonusRole
{
    Match,
    Require,
    Exclude
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
