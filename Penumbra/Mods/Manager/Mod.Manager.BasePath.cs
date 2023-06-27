using System;
using System.IO;
using System.Linq;

namespace Penumbra.Mods;

public partial class Mod
{
    public partial class Manager
    {
        public delegate void ModPathChangeDelegate( ModPathChangeType type, Mod mod, DirectoryInfo? oldDirectory,
            DirectoryInfo? newDirectory );

        public event ModPathChangeDelegate ModPathChanged;

        // Rename/Move a mod directory.
        // Updates all collection settings and sort order settings.
        public void MoveModDirectory( int idx, string newName )
        {
            var mod          = this[ idx ];
            var oldName      = mod.Name;
            var oldDirectory = mod.ModPath;

            switch( NewDirectoryValid( oldDirectory.Name, newName, out var dir ) )
            {
                case NewDirectoryState.NonExisting:
                    // Nothing to do
                    break;
                case NewDirectoryState.ExistsEmpty:
                    try
                    {
                        Directory.Delete( dir!.FullName );
                    }
                    catch( Exception e )
                    {
                        Penumbra.Log.Error( $"Could not delete empty directory {dir!.FullName} to move {mod.Name} to it:\n{e}" );
                        return;
                    }

                    break;
                // Should be caught beforehand.
                case NewDirectoryState.ExistsNonEmpty:
                case NewDirectoryState.ExistsAsFile:
                case NewDirectoryState.ContainsInvalidSymbols:
                // Nothing to do at all.
                case NewDirectoryState.Identical:
                default:
                    return;
            }

            try
            {
                Directory.Move( oldDirectory.FullName, dir!.FullName );
            }
            catch( Exception e )
            {
                Penumbra.Log.Error( $"Could not move {mod.Name} from {oldDirectory.Name} to {dir!.Name}:\n{e}" );
                return;
            }

            MoveDataFile( oldDirectory, dir );
            new ModBackup( mod ).Move( null, dir.Name );

            dir.Refresh();
            mod.ModPath = dir;
            if( !mod.Reload( false, out var metaChange ) )
            {
                Penumbra.Log.Error( $"Error reloading moved mod {mod.Name}." );
                return;
            }

            ModPathChanged.Invoke( ModPathChangeType.Moved, mod, oldDirectory, dir );
            if( metaChange != ModDataChangeType.None )
            {
                ModDataChanged?.Invoke( metaChange, mod, oldName );
            }
        }

        // Reload a mod without changing its base directory.
        // If the base directory does not exist anymore, the mod will be deleted.
        public void ReloadMod( int idx )
        {
            var mod     = this[ idx ];
            var oldName = mod.Name;

            ModPathChanged.Invoke( ModPathChangeType.StartingReload, mod, mod.ModPath, mod.ModPath );
            if( !mod.Reload( true, out var metaChange ) )
            {
                Penumbra.Log.Warning( mod.Name.Length == 0
                    ? $"Reloading mod {oldName} has failed, new name is empty. Deleting instead."
                    : $"Reloading mod {oldName} failed, {mod.ModPath.FullName} does not exist anymore or it ha. Deleting instead." );

                DeleteMod( idx );
                return;
            }

            ModPathChanged.Invoke( ModPathChangeType.Reloaded, mod, mod.ModPath, mod.ModPath );
            if( metaChange != ModDataChangeType.None )
            {
                ModDataChanged?.Invoke( metaChange, mod, oldName );
            }
        }

        // Delete a mod by its index. The event is invoked before the mod is removed from the list.
        // Deletes from filesystem as well as from internal data.
        // Updates indices of later mods.
        public void DeleteMod( int idx )
        {
            var mod = this[ idx ];
            if( Directory.Exists( mod.ModPath.FullName ) )
            {
                try
                {
                    Directory.Delete( mod.ModPath.FullName, true );
                    Penumbra.Log.Debug( $"Deleted directory {mod.ModPath.FullName} for {mod.Name}." );
                }
                catch( Exception e )
                {
                    Penumbra.Log.Error( $"Could not delete the mod {mod.ModPath.Name}:\n{e}" );
                }
            }

            ModPathChanged.Invoke( ModPathChangeType.Deleted, mod, mod.ModPath, null );
            _mods.RemoveAt( idx );
            foreach( var remainingMod in _mods.Skip( idx ) )
            {
                --remainingMod.Index;
            }

            Penumbra.Log.Debug( $"Deleted mod {mod.Name}." );
        }

        // Load a new mod and add it to the manager if successful.
        public void AddMod( DirectoryInfo modFolder )
        {
            if( _mods.Any( m => m.ModPath.Name == modFolder.Name ) )
            {
                return;
            }

            var mod = LoadMod( modFolder, true );
            if( mod == null )
            {
                return;
            }

            mod.Index = _mods.Count;
            _mods.Add( mod );
            ModPathChanged.Invoke( ModPathChangeType.Added, mod, null, mod.ModPath );
            Penumbra.Log.Debug( $"Added new mod {mod.Name} from {modFolder.FullName}." );
        }

        public enum NewDirectoryState
        {
            NonExisting,
            ExistsEmpty,
            ExistsNonEmpty,
            ExistsAsFile,
            ContainsInvalidSymbols,
            Identical,
            Empty,
        }

        // Return the state of the new potential name of a directory.
        public static NewDirectoryState NewDirectoryValid( string oldName, string newName, out DirectoryInfo? directory )
        {
            directory = null;
            if( newName.Length == 0 )
            {
                return NewDirectoryState.Empty;
            }

            if( oldName == newName )
            {
                return NewDirectoryState.Identical;
            }

            var fixedNewName = ReplaceBadXivSymbols( newName );
            if( fixedNewName != newName )
            {
                return NewDirectoryState.ContainsInvalidSymbols;
            }

            directory = new DirectoryInfo( Path.Combine( Penumbra.ModManager.BasePath.FullName, fixedNewName ) );
            if( File.Exists( directory.FullName ) )
            {
                return NewDirectoryState.ExistsAsFile;
            }

            if( !Directory.Exists( directory.FullName ) )
            {
                return NewDirectoryState.NonExisting;
            }

            if( directory.EnumerateFileSystemInfos().Any() )
            {
                return NewDirectoryState.ExistsNonEmpty;
            }

            return NewDirectoryState.ExistsEmpty;
        }


        // Add new mods to NewMods and remove deleted mods from NewMods.
        private void OnModPathChange( ModPathChangeType type, Mod mod, DirectoryInfo? oldDirectory,
            DirectoryInfo? newDirectory )
        {
            switch( type )
            {
                case ModPathChangeType.Added:
                    NewMods.Add( mod );
                    break;
                case ModPathChangeType.Deleted:
                    NewMods.Remove( mod );
                    break;
                case ModPathChangeType.Moved:
                    if( oldDirectory != null && newDirectory != null )
                    {
                        MoveDataFile( oldDirectory, newDirectory );
                    }

                    break;
            }
        }
    }
}