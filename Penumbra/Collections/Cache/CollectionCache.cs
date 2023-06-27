using OtterGui;
using OtterGui.Classes;
using Penumbra.Meta.Manipulations;
using Penumbra.Mods;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Penumbra.Api.Enums;
using Penumbra.String.Classes;
using Penumbra.Mods.Manager;

namespace Penumbra.Collections.Cache;

public record struct ModPath(IMod Mod, FullPath Path);
public record ModConflicts(IMod Mod2, List<object> Conflicts, bool HasPriority, bool Solved);

/// <summary>
/// The Cache contains all required temporary data to use a collection.
/// It will only be setup if a collection gets activated in any way.
/// </summary>
public class CollectionCache : IDisposable
{
    private readonly CollectionCacheManager                           _manager;
    private readonly ModCollection                                    _collection;
    public readonly  CollectionModData                                ModData       = new();
    public readonly  SortedList<string, (SingleArray<IMod>, object?)> _changedItems = new();
    public readonly  Dictionary<Utf8GamePath, ModPath>                ResolvedFiles = new();
    public readonly  MetaCache                                        Meta;
    public readonly  Dictionary<IMod, SingleArray<ModConflicts>>      _conflicts = new();

    public bool Calculating;

    public IEnumerable<SingleArray<ModConflicts>> AllConflicts
        => _conflicts.Values;

    public SingleArray<ModConflicts> Conflicts(IMod mod)
        => _conflicts.TryGetValue(mod, out var c) ? c : new SingleArray<ModConflicts>();

    private int _changedItemsSaveCounter = -1;

    // Obtain currently changed items. Computes them if they haven't been computed before.
    public IReadOnlyDictionary<string, (SingleArray<IMod>, object?)> ChangedItems
    {
        get
        {
            SetChangedItems();
            return _changedItems;
        }
    }

    // The cache reacts through events on its collection changing.
    public CollectionCache(CollectionCacheManager manager, ModCollection collection)
    {
        _manager    = manager;
        _collection = collection;
        Meta        = new MetaCache(manager.MetaFileManager, _collection);
    }

    public void Dispose()
    {
        Meta.Dispose();
    }

    ~CollectionCache()
        => Meta.Dispose();

    // Resolve a given game path according to this collection.
    public FullPath? ResolvePath(Utf8GamePath gameResourcePath)
    {
        if (!ResolvedFiles.TryGetValue(gameResourcePath, out var candidate))
            return null;

        if (candidate.Path.InternalName.Length > Utf8GamePath.MaxGamePathLength
         || candidate.Path.IsRooted && !candidate.Path.Exists)
            return null;

        return candidate.Path;
    }

    // For a given full path, find all game paths that currently use this file.
    public IEnumerable<Utf8GamePath> ReverseResolvePath(FullPath localFilePath)
    {
        var needle = localFilePath.FullName.ToLower();
        if (localFilePath.IsRooted)
            needle = needle.Replace('/', '\\');

        var iterator = ResolvedFiles
            .Where(f => string.Equals(f.Value.Path.FullName, needle, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key);

        // For files that are not rooted, try to add themselves.
        if (!localFilePath.IsRooted && Utf8GamePath.FromString(localFilePath.FullName, out var utf8))
            iterator = iterator.Prepend(utf8);

        return iterator;
    }

    // Reverse resolve multiple paths at once for efficiency.
    public HashSet<Utf8GamePath>[] ReverseResolvePaths(IReadOnlyCollection<string> fullPaths)
    {
        if (fullPaths.Count == 0)
            return Array.Empty<HashSet<Utf8GamePath>>();

        var ret  = new HashSet<Utf8GamePath>[fullPaths.Count];
        var dict = new Dictionary<FullPath, int>(fullPaths.Count);
        foreach (var (path, idx) in fullPaths.WithIndex())
        {
            dict[new FullPath(path)] = idx;
            ret[idx] = !Path.IsPathRooted(path) && Utf8GamePath.FromString(path, out var utf8)
                ? new HashSet<Utf8GamePath> { utf8 }
                : new HashSet<Utf8GamePath>();
        }

        foreach (var (game, full) in ResolvedFiles)
        {
            if (dict.TryGetValue(full.Path, out var idx))
                ret[idx].Add(game);
        }

        return ret;
    }

    /// <summary> Force a file to be resolved to a specific path regardless of conflicts. </summary>
    internal void ForceFile(Utf8GamePath path, FullPath fullPath)
    {
        if (!CheckFullPath(path, fullPath))
            return;

        if (ResolvedFiles.Remove(path, out var modPath))
            ModData.RemovePath(modPath.Mod, path);
        ResolvedFiles.Add(path, new ModPath(Mod.ForcedFiles, fullPath));
        ModData.AddPath(Mod.ForcedFiles, path);
    }

    /// <summary> Force a file resolve to be removed. </summary>
    internal void RemovePath(Utf8GamePath path)
    {
        if (ResolvedFiles.Remove(path, out var modPath))
            ModData.RemovePath(modPath.Mod, path);
    }

    public void ReloadMod(IMod mod, bool addMetaChanges)
    {
        RemoveMod(mod, addMetaChanges);
        AddMod(mod, addMetaChanges);
    }

    public void RemoveMod(IMod mod, bool addMetaChanges)
    {
        var conflicts = Conflicts(mod);
        var (paths, manipulations) = ModData.RemoveMod(mod);

        if (addMetaChanges)
            ++_collection.ChangeCounter;

        foreach (var path in paths)
        {
            if (ResolvedFiles.Remove(path, out var mp) && mp.Mod != mod)
                Penumbra.Log.Warning(
                    $"Invalid mod state, removing {mod.Name} and associated file {path} returned current mod {mp.Mod.Name}.");
        }

        foreach (var manipulation in manipulations)
        {
            if (Meta.RevertMod(manipulation, out var mp) && mp != mod)
                Penumbra.Log.Warning(
                    $"Invalid mod state, removing {mod.Name} and associated manipulation {manipulation} returned current mod {mp.Name}.");
        }

        _conflicts.Remove(mod);
        foreach (var conflict in conflicts)
        {
            if (conflict.HasPriority)
            {
                ReloadMod(conflict.Mod2, false);
            }
            else
            {
                var newConflicts = Conflicts(conflict.Mod2).Remove(c => c.Mod2 == mod);
                if (newConflicts.Count > 0)
                    _conflicts[conflict.Mod2] = newConflicts;
                else
                    _conflicts.Remove(conflict.Mod2);
            }
        }

        if (addMetaChanges)
            _manager.MetaFileManager.ApplyDefaultFiles(_collection);
    }


    // Add all files and possibly manipulations of a given mod according to its settings in this collection.
    public void AddMod(IMod mod, bool addMetaChanges)
    {
        if (mod.Index >= 0)
        {
            var settings = _collection[mod.Index].Settings;
            if (settings is not { Enabled: true })
                return;

            foreach (var (group, groupIndex) in mod.Groups.WithIndex().OrderByDescending(g => g.Item1.Priority))
            {
                if (group.Count == 0)
                    continue;

                var config = settings.Settings[groupIndex];
                switch (group.Type)
                {
                    case GroupType.Single:
                        AddSubMod(group[(int)config], mod);
                        break;
                    case GroupType.Multi:
                    {
                        foreach (var (option, _) in group.WithIndex()
                                     .Where(p => ((1 << p.Item2) & config) != 0)
                                     .OrderByDescending(p => group.OptionPriority(p.Item2)))
                            AddSubMod(option, mod);

                        break;
                    }
                }
            }
        }

        AddSubMod(mod.Default, mod);

        if (addMetaChanges)
        {
            ++_collection.ChangeCounter;
            if (mod.TotalManipulations > 0)
                AddMetaFiles();

            _manager.MetaFileManager.ApplyDefaultFiles(_collection);
        }
    }

    // Add all files and possibly manipulations of a specific submod
    private void AddSubMod(ISubMod subMod, IMod parentMod)
    {
        foreach (var (path, file) in subMod.Files.Concat(subMod.FileSwaps))
            AddFile(path, file, parentMod);

        foreach (var manip in subMod.Manipulations)
            AddManipulation(manip, parentMod);
    }

    // Add a specific file redirection, handling potential conflicts.
    // For different mods, higher mod priority takes precedence before option group priority,
    // which takes precedence before option priority, which takes precedence before ordering.
    // Inside the same mod, conflicts are not recorded.
    private void AddFile(Utf8GamePath path, FullPath file, IMod mod)
    {
        if (!CheckFullPath(path, file))
            return;

        if (ResolvedFiles.TryAdd(path, new ModPath(mod, file)))
        {
            ModData.AddPath(mod, path);
            return;
        }

        var modPath = ResolvedFiles[path];
        // Lower prioritized option in the same mod.
        if (mod == modPath.Mod)
            return;

        if (AddConflict(path, mod, modPath.Mod))
        {
            ModData.RemovePath(modPath.Mod, path);
            ResolvedFiles[path] = new ModPath(mod, file);
            ModData.AddPath(mod, path);
        }
    }


    // Remove all empty conflict sets for a given mod with the given conflicts.
    // If transitive is true, also removes the corresponding version of the other mod.
    private void RemoveEmptyConflicts(IMod mod, SingleArray<ModConflicts> oldConflicts, bool transitive)
    {
        var changedConflicts = oldConflicts.Remove(c =>
        {
            if (c.Conflicts.Count == 0)
            {
                if (transitive)
                    RemoveEmptyConflicts(c.Mod2, Conflicts(c.Mod2), false);

                return true;
            }

            return false;
        });
        if (changedConflicts.Count == 0)
            _conflicts.Remove(mod);
        else
            _conflicts[mod] = changedConflicts;
    }

    // Add a new conflict between the added mod and the existing mod.
    // Update all other existing conflicts between the existing mod and other mods if necessary.
    // Returns if the added mod takes priority before the existing mod.
    private bool AddConflict(object data, IMod addedMod, IMod existingMod)
    {
        var addedPriority    = addedMod.Index >= 0 ? _collection[addedMod.Index].Settings!.Priority : addedMod.Priority;
        var existingPriority = existingMod.Index >= 0 ? _collection[existingMod.Index].Settings!.Priority : existingMod.Priority;

        if (existingPriority < addedPriority)
        {
            var tmpConflicts = Conflicts(existingMod);
            foreach (var conflict in tmpConflicts)
            {
                if (data is Utf8GamePath path && conflict.Conflicts.RemoveAll(p => p is Utf8GamePath x && x.Equals(path)) > 0
                 || data is MetaManipulation meta && conflict.Conflicts.RemoveAll(m => m is MetaManipulation x && x.Equals(meta)) > 0)
                    AddConflict(data, addedMod, conflict.Mod2);
            }

            RemoveEmptyConflicts(existingMod, tmpConflicts, true);
        }

        var addedConflicts    = Conflicts(addedMod);
        var existingConflicts = Conflicts(existingMod);
        if (addedConflicts.FindFirst(c => c.Mod2 == existingMod, out var oldConflicts))
        {
            // Only need to change one list since both conflict lists refer to the same list.
            oldConflicts.Conflicts.Add(data);
        }
        else
        {
            // Add the same conflict list to both conflict directions.
            var conflictList = new List<object> { data };
            _conflicts[addedMod] = addedConflicts.Append(new ModConflicts(existingMod, conflictList, existingPriority < addedPriority,
                existingPriority != addedPriority));
            _conflicts[existingMod] = existingConflicts.Append(new ModConflicts(addedMod, conflictList,
                existingPriority >= addedPriority,
                existingPriority != addedPriority));
        }

        return existingPriority < addedPriority;
    }

    // Add a specific manipulation, handling potential conflicts.
    // For different mods, higher mod priority takes precedence before option group priority,
    // which takes precedence before option priority, which takes precedence before ordering.
    // Inside the same mod, conflicts are not recorded.
    private void AddManipulation(MetaManipulation manip, IMod mod)
    {
        if (!Meta.TryGetValue(manip, out var existingMod))
        {
            Meta.ApplyMod(manip, mod);
            ModData.AddManip(mod, manip);
            return;
        }

        // Lower prioritized option in the same mod.
        if (mod == existingMod)
            return;

        if (AddConflict(manip, mod, existingMod))
        {
            ModData.RemoveManip(existingMod, manip);
            Meta.ApplyMod(manip, mod);
            ModData.AddManip(mod, manip);
        }
    }


    // Add all necessary meta file redirects.
    public void AddMetaFiles()
        => Meta.SetImcFiles();


    // Identify and record all manipulated objects for this entire collection.
    private void SetChangedItems()
    {
        if (_changedItemsSaveCounter == _collection.ChangeCounter)
            return;

        try
        {
            _changedItemsSaveCounter = _collection.ChangeCounter;
            _changedItems.Clear();
            // Skip IMCs because they would result in far too many false-positive items,
            // since they are per set instead of per item-slot/item/variant.
            var identifier = _manager.MetaFileManager.Identifier.AwaitedService;
            var items      = new SortedList<string, object?>(512);

            void AddItems(IMod mod)
            {
                foreach (var (name, obj) in items)
                {
                    if (!_changedItems.TryGetValue(name, out var data))
                        _changedItems.Add(name, (new SingleArray<IMod>(mod), obj));
                    else if (!data.Item1.Contains(mod))
                        _changedItems[name] = (data.Item1.Append(mod), obj is int x && data.Item2 is int y ? x + y : obj);
                    else if (obj is int x && data.Item2 is int y)
                        _changedItems[name] = (data.Item1, x + y);
                }

                items.Clear();
            }

            foreach (var (resolved, modPath) in ResolvedFiles.Where(file => !file.Key.Path.EndsWith("imc"u8)))
            {
                identifier.Identify(items, resolved.ToString());
                AddItems(modPath.Mod);
            }

            foreach (var (manip, mod) in Meta)
            {
                ModCacheManager.ComputeChangedItems(identifier, items, manip);
                AddItems(mod);
            }
        }
        catch (Exception e)
        {
            Penumbra.Log.Error($"Unknown Error:\n{e}");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CheckFullPath(Utf8GamePath path, FullPath fullPath)
    {
        if (fullPath.InternalName.Length < Utf8GamePath.MaxGamePathLength)
            return true;

        Penumbra.Log.Error($"The redirected path is too long to add the redirection\n\t{path}\n\t--> {fullPath}");
        return false;
    }
}
