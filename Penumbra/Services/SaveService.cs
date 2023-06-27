using System;
using System.IO;
using System.Text;
using Dalamud.Game;
using OtterGui.Classes;
using OtterGui.Log;
using Penumbra.Mods;

namespace Penumbra.Services;

/// <summary>
/// Any file type that we want to save via SaveService.
/// </summary>
public interface ISavable
{
    /// <summary> The full file name of a given object. </summary>
    public string ToFilename(FilenameService fileNames);

    /// <summary> Write the objects data to the given stream writer. </summary>
    public void Save(StreamWriter writer);

    /// <summary> An arbitrary message printed to Debug before saving. </summary>
    public string LogName(string fileName)
        => fileName;

    public string TypeName
        => GetType().Name;
}

public class SaveService
{
#if DEBUG
    private static readonly TimeSpan StandardDelay = TimeSpan.FromSeconds(2);
#else
    private static readonly TimeSpan StandardDelay = TimeSpan.FromSeconds(10);
#endif

    private readonly Logger           _log;
    private readonly FrameworkManager _framework;

    public readonly FilenameService FileNames;
    public readonly Framework       DalamudFramework;

    public SaveService(Logger log, FrameworkManager framework, FilenameService fileNames, Framework dalamudFramework)
    {
        _log             = log;
        _framework       = framework;
        FileNames        = fileNames;
        DalamudFramework = dalamudFramework;
    }

    /// <summary> Queue a save for the next framework tick. </summary>
    public void QueueSave(ISavable value)
    {
        var file = value.ToFilename(FileNames);
        _framework.RegisterOnTick($"{value.GetType().Name} ## {file}", () => { ImmediateSave(value); });
    }

    /// <summary> Queue a delayed save with the standard delay for after the delay is over. </summary>
    public void DelaySave(ISavable value)
        => DelaySave(value, StandardDelay);

    /// <summary> Queue a delayed save for after the delay is over. </summary>
    public void DelaySave(ISavable value, TimeSpan delay)
    {
        var file = value.ToFilename(FileNames);
        _framework.RegisterDelayed($"{value.GetType().Name} ## {file}", () => { ImmediateSave(value); }, delay);
    }

    /// <summary> Immediately trigger a save. </summary>
    public void ImmediateSave(ISavable value)
    {
        var name = value.ToFilename(FileNames);
        try
        {
            if (name.Length == 0)
                throw new Exception("Invalid object returned empty filename.");

            _log.Debug($"Saving {value.TypeName} {value.LogName(name)}...");
            var file = new FileInfo(name);
            file.Directory?.Create();
            using var s = file.Exists ? file.Open(FileMode.Truncate) : file.Open(FileMode.CreateNew);
            using var w = new StreamWriter(s, Encoding.UTF8);
            value.Save(w);
        }
        catch (Exception ex)
        {
            _log.Error($"Could not save {value.GetType().Name} {value.LogName(name)}:\n{ex}");
        }
    }

    public void ImmediateDelete(ISavable value)
    {
        var name = value.ToFilename(FileNames);
        try
        {
            if (name.Length == 0)
                throw new Exception("Invalid object returned empty filename.");

            if (!File.Exists(name))
                return;

            _log.Information($"Deleting {value.GetType().Name} {value.LogName(name)}...");
            File.Delete(name);
        }
        catch (Exception ex)
        {
            _log.Error($"Could not delete {value.GetType().Name} {value.LogName(name)}:\n{ex}");
        }
    }

    /// <summary> Immediately delete all existing option group files for a mod and save them anew. </summary>
    public void SaveAllOptionGroups(Mod mod)
    {
        foreach (var file in FileNames.GetOptionGroupFiles(mod))
        {
            try
            {
                if (file.Exists)
                    file.Delete();
            }
            catch (Exception e)
            {
                Penumbra.Log.Error($"Could not delete outdated group file {file}:\n{e}");
            }
        }

        for (var i = 0; i < mod.Groups.Count; ++i)
            ImmediateSave(new ModSaveGroup(mod, i));
    }
}
