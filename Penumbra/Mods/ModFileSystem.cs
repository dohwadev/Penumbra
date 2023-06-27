using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using OtterGui.Filesystem;

namespace Penumbra.Mods;

public sealed class ModFileSystem : FileSystem< Mod >, IDisposable
{
    public static string ModFileSystemFile
        => Path.Combine( Dalamud.PluginInterface.GetPluginConfigDirectory(), "sort_order.json" );

    // Save the current sort order.
    // Does not save or copy the backup in the current mod directory,
    // as this is done on mod directory changes only.
    private void SaveFilesystem()
    {
        SaveToFile( new FileInfo( ModFileSystemFile ), SaveMod, true );
        Penumbra.Log.Verbose( "Saved mod filesystem." );
    }

    private void Save()
        => Penumbra.Framework.RegisterDelayed( nameof( SaveFilesystem ), SaveFilesystem );

    // Create a new ModFileSystem from the currently loaded mods and the current sort order file.
    public static ModFileSystem Load()
    {
        var ret = new ModFileSystem();
        ret.Reload();

        ret.Changed                              += ret.OnChange;
        Penumbra.ModManager.ModDiscoveryFinished += ret.Reload;
        Penumbra.ModManager.ModDataChanged       += ret.OnDataChange;
        Penumbra.ModManager.ModPathChanged       += ret.OnModPathChange;

        return ret;
    }

    public void Dispose()
    {
        Penumbra.ModManager.ModPathChanged       -= OnModPathChange;
        Penumbra.ModManager.ModDiscoveryFinished -= Reload;
        Penumbra.ModManager.ModDataChanged       -= OnDataChange;
    }

    public struct ImportDate : ISortMode< Mod >
    {
        public string Name
            => "Import Date (Older First)";

        public string Description
            => "In each folder, sort all subfolders lexicographically, then sort all leaves using their import date.";

        public IEnumerable< IPath > GetChildren( Folder f )
            => f.GetSubFolders().Cast< IPath >().Concat( f.GetLeaves().OrderBy( l => l.Value.ImportDate ) );
    }

    public struct InverseImportDate : ISortMode< Mod >
    {
        public string Name
            => "Import Date (Newer First)";

        public string Description
            => "In each folder, sort all subfolders lexicographically, then sort all leaves using their inverse import date.";

        public IEnumerable< IPath > GetChildren( Folder f )
            => f.GetSubFolders().Cast< IPath >().Concat( f.GetLeaves().OrderByDescending( l => l.Value.ImportDate ) );
    }

    // Reload the whole filesystem from currently loaded mods and the current sort order file.
    // Used on construction and on mod rediscoveries.
    private void Reload()
    {
        if( Load( new FileInfo( ModFileSystemFile ), Penumbra.ModManager, ModToIdentifier, ModToName ) )
        {
            Save();
        }

        Penumbra.Log.Debug( "Reloaded mod filesystem." );
    }

    // Save the filesystem on every filesystem change except full reloading.
    private void OnChange( FileSystemChangeType type, IPath _1, IPath? _2, IPath? _3 )
    {
        if( type != FileSystemChangeType.Reload )
        {
            Save();
        }
    }

    // Update sort order when defaulted mod names change.
    private void OnDataChange( ModDataChangeType type, Mod mod, string? oldName )
    {
        if( type.HasFlag( ModDataChangeType.Name ) && oldName != null )
        {
            var old = oldName.FixName();
            if( Find( old, out var child ) && child is not Folder )
            {
                Rename( child, mod.Name.Text );
            }
        }
    }

    // Update the filesystem if a mod has been added or removed.
    // Save it, if the mod directory has been moved, since this will change the save format.
    private void OnModPathChange( ModPathChangeType type, Mod mod, DirectoryInfo? oldPath, DirectoryInfo? newPath )
    {
        switch( type )
        {
            case ModPathChangeType.Added:
                var originalName = mod.Name.Text.FixName();
                var name         = originalName;
                var counter      = 1;
                while( Find( name, out _ ) )
                {
                    name = $"{originalName} ({++counter})";
                }

                CreateLeaf( Root, name, mod );
                break;
            case ModPathChangeType.Deleted:
                if( FindLeaf( mod, out var leaf ) )
                {
                    Delete( leaf );
                }

                break;
            case ModPathChangeType.Moved:
                Save();
                break;
            case ModPathChangeType.Reloaded:
                // Nothing
                break;
        }
    }

    // Search the entire filesystem for the leaf corresponding to a mod.
    public bool FindLeaf( Mod mod, [NotNullWhen( true )] out Leaf? leaf )
    {
        leaf = Root.GetAllDescendants( ISortMode< Mod >.Lexicographical )
           .OfType< Leaf >()
           .FirstOrDefault( l => l.Value == mod );
        return leaf != null;
    }


    // Used for saving and loading.
    private static string ModToIdentifier( Mod mod )
        => mod.ModPath.Name;

    private static string ModToName( Mod mod )
        => mod.Name.Text.FixName();

    // Return whether a mod has a custom path or is just a numbered default path.
    public static bool ModHasDefaultPath( Mod mod, string fullPath )
    {
        var regex = new Regex( $@"^{Regex.Escape( ModToName( mod ) )}( \(\d+\))?$" );
        return regex.IsMatch( fullPath );
    }

    private static (string, bool) SaveMod( Mod mod, string fullPath )
        // Only save pairs with non-default paths.
        => ModHasDefaultPath( mod, fullPath )
            ? ( string.Empty, false )
            : ( ModToIdentifier( mod ), true );
}