using System;
using ImGuiNET;
using OtterGui.Widgets;
using Penumbra.Api.Enums;

namespace Penumbra.UI.Tabs;

public class ConfigTabBar
{
    public readonly SettingsTab     Settings;
    public readonly ModsTab         Mods;
    public readonly CollectionsTab  Collections;
    public readonly ChangedItemsTab ChangedItems;
    public readonly EffectiveTab    Effective;
    public readonly DebugTab        Debug;
    public readonly ResourceTab     Resource;
    public readonly ResourceWatcher Watcher;
    public readonly OnScreenTab     OnScreenTab;

    public readonly ITab[] Tabs;

    /// <summary> The tab to select on the next Draw call, if any. </summary>
    public TabType SelectTab = TabType.None;

    public ConfigTabBar(SettingsTab settings, ModsTab mods, CollectionsTab collections, ChangedItemsTab changedItems, EffectiveTab effective,
        DebugTab debug, ResourceTab resource, ResourceWatcher watcher, OnScreenTab onScreenTab)
    {
        Settings     = settings;
        Mods         = mods;
        Collections  = collections;
        ChangedItems = changedItems;
        Effective    = effective;
        Debug        = debug;
        Resource     = resource;
        Watcher      = watcher;
        OnScreenTab  = onScreenTab;
        Tabs = new ITab[]
        {
            Settings,
            Collections,
            Mods,
            ChangedItems,
            Effective,
            OnScreenTab,
            Debug,
            Resource,
            Watcher,
        };
    }

    public TabType Draw()
    {
        if (TabBar.Draw(string.Empty, ImGuiTabBarFlags.NoTooltip, ToLabel(SelectTab), out var currentLabel, () => { }, Tabs))
            SelectTab = TabType.None;

        return FromLabel(currentLabel);
    }

    private ReadOnlySpan<byte> ToLabel(TabType type)
        => type switch
        {
            TabType.Settings         => Settings.Label,
            TabType.Mods             => Mods.Label,
            TabType.Collections      => Collections.Label,
            TabType.ChangedItems     => ChangedItems.Label,
            TabType.EffectiveChanges => Effective.Label,
            TabType.OnScreen         => OnScreenTab.Label,
            TabType.ResourceWatcher  => Watcher.Label,
            TabType.Debug            => Debug.Label,
            TabType.ResourceManager  => Resource.Label,
            _                        => ReadOnlySpan<byte>.Empty,
        };

    private TabType FromLabel(ReadOnlySpan<byte> label)
    {
        // @formatter:off
        if (label == Mods.Label)         return TabType.Mods;
        if (label == Collections.Label)  return TabType.Collections;
        if (label == Settings.Label)     return TabType.Settings;
        if (label == ChangedItems.Label) return TabType.ChangedItems;
        if (label == Effective.Label)    return TabType.EffectiveChanges;
        if (label == OnScreenTab.Label)  return TabType.OnScreen;
        if (label == Watcher.Label)      return TabType.ResourceWatcher;
        if (label == Debug.Label)        return TabType.Debug;
        if (label == Resource.Label)     return TabType.ResourceManager;
        // @formatter:on
        return TabType.None;
    }
}
