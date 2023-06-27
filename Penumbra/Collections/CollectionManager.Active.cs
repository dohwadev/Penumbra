using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OtterGui;
using Penumbra.Mods;
using Penumbra.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Interface.Internal.Notifications;
using Penumbra.GameData.Actors;
using Penumbra.Util;

namespace Penumbra.Collections;

public partial class ModCollection
{
    public sealed partial class Manager
    {
        public const int Version = 1;

        // Is invoked after the collections actually changed.
        public event CollectionChangeDelegate CollectionChanged;

        // The collection currently selected for changing settings.
        public ModCollection Current { get; private set; } = Empty;

        // The collection currently selected is in use either as an active collection or through inheritance.
        public bool CurrentCollectionInUse { get; private set; }

        // The collection used for general file redirections and all characters not specifically named.
        public ModCollection Default { get; private set; } = Empty;

        // The collection used for all files categorized as UI files.
        public ModCollection Interface { get; private set; } = Empty;

        // A single collection that can not be deleted as a fallback for the current collection.
        private ModCollection DefaultName { get; set; } = Empty;

        // The list of character collections.
        public readonly IndividualCollections Individuals = new(Penumbra.Actors);

        public ModCollection Individual( ActorIdentifier identifier )
            => Individuals.TryGetCollection( identifier, out var c ) ? c : Default;

        // Special Collections
        private readonly ModCollection?[] _specialCollections = new ModCollection?[Enum.GetValues< CollectionType >().Length - 4];

        // Return the configured collection for the given type or null.
        // Does not handle Inactive, use ByName instead.
        public ModCollection? ByType( CollectionType type )
            => ByType( type, ActorIdentifier.Invalid );

        public ModCollection? ByType( CollectionType type, ActorIdentifier identifier )
        {
            if( type.IsSpecial() )
            {
                return _specialCollections[ ( int )type ];
            }

            return type switch
            {
                CollectionType.Default    => Default,
                CollectionType.Interface  => Interface,
                CollectionType.Current    => Current,
                CollectionType.Individual => identifier.IsValid && Individuals.Individuals.TryGetValue( identifier, out var c ) ? c : null,
                _                         => null,
            };
        }

        // Set a active collection, can be used to set Default, Current, Interface, Special, or Individual collections.
        private void SetCollection( int newIdx, CollectionType collectionType, int individualIndex = -1 )
        {
            var oldCollectionIdx = collectionType switch
            {
                CollectionType.Default            => Default.Index,
                CollectionType.Interface          => Interface.Index,
                CollectionType.Current            => Current.Index,
                CollectionType.Individual         => individualIndex < 0 || individualIndex >= Individuals.Count ? -1 : Individuals[ individualIndex ].Collection.Index,
                _ when collectionType.IsSpecial() => _specialCollections[ ( int )collectionType ]?.Index ?? Default.Index,
                _                                 => -1,
            };

            if( oldCollectionIdx == -1 || newIdx == oldCollectionIdx )
            {
                return;
            }

            var newCollection = this[ newIdx ];
            if( newIdx > Empty.Index )
            {
                newCollection.CreateCache();
            }

            switch( collectionType )
            {
                case CollectionType.Default:
                    Default = newCollection;
                    if( Penumbra.CharacterUtility.Ready && Penumbra.Config.EnableMods )
                    {
                        Penumbra.ResidentResources.Reload();
                        Default.SetFiles();
                    }

                    break;
                case CollectionType.Interface:
                    Interface = newCollection;
                    break;
                case CollectionType.Current:
                    Current = newCollection;
                    break;
                case CollectionType.Individual:
                    if( !Individuals.ChangeCollection( individualIndex, newCollection ) )
                    {
                        RemoveCache( newIdx );
                        return;
                    }

                    break;
                default:
                    _specialCollections[ ( int )collectionType ] = newCollection;
                    break;
            }

            RemoveCache( oldCollectionIdx );

            UpdateCurrentCollectionInUse();
            CollectionChanged.Invoke( collectionType, this[ oldCollectionIdx ], newCollection, collectionType == CollectionType.Individual ? Individuals[ individualIndex ].DisplayName : string.Empty );
        }

        private void UpdateCurrentCollectionInUse()
            => CurrentCollectionInUse = _specialCollections
               .OfType< ModCollection >()
               .Prepend( Interface )
               .Prepend( Default )
               .Concat( Individuals.Assignments.Select( kvp => kvp.Collection ) )
               .SelectMany( c => c.GetFlattenedInheritance() ).Contains( Current );

        public void SetCollection( ModCollection collection, CollectionType collectionType, int individualIndex = -1 )
            => SetCollection( collection.Index, collectionType, individualIndex );

        // Create a special collection if it does not exist and set it to Empty.
        public bool CreateSpecialCollection( CollectionType collectionType )
        {
            if( !collectionType.IsSpecial() || _specialCollections[ ( int )collectionType ] != null )
            {
                return false;
            }

            _specialCollections[ ( int )collectionType ] = Default;
            CollectionChanged.Invoke( collectionType, null, Default );
            return true;
        }

        // Remove a special collection if it exists
        public void RemoveSpecialCollection( CollectionType collectionType )
        {
            if( !collectionType.IsSpecial() )
            {
                return;
            }

            var old = _specialCollections[ ( int )collectionType ];
            if( old != null )
            {
                _specialCollections[ ( int )collectionType ] = null;
                CollectionChanged.Invoke( collectionType, old, null );
            }
        }

        // Wrappers around Individual Collection handling.
        public void CreateIndividualCollection( params ActorIdentifier[] identifiers )
        {
            if( Individuals.Add( identifiers, Default ) )
            {
                CollectionChanged.Invoke( CollectionType.Individual, null, Default, Individuals.Last().DisplayName );
            }
        }

        public void RemoveIndividualCollection( int individualIndex )
        {
            if( individualIndex < 0 || individualIndex >= Individuals.Count )
            {
                return;
            }

            var (name, old) = Individuals[ individualIndex ];
            if( Individuals.Delete( individualIndex ) )
            {
                CollectionChanged.Invoke( CollectionType.Individual, old, null, name );
            }
        }

        public void MoveIndividualCollection( int from, int to )
        {
            if( Individuals.Move( from, to ) )
            {
                SaveActiveCollections();
            }
        }

        // Obtain the index of a collection by name.
        private int GetIndexForCollectionName( string name )
            => name.Length == 0 ? Empty.Index : _collections.IndexOf( c => c.Name == name );

        public static string ActiveCollectionFile
            => Path.Combine( Dalamud.PluginInterface.ConfigDirectory.FullName, "active_collections.json" );

        // Load default, current, special, and character collections from config.
        // Then create caches. If a collection does not exist anymore, reset it to an appropriate default.
        private void LoadCollections()
        {
            var configChanged = !ReadActiveCollections( out var jObject );

            // Load the default collection.
            var defaultName = jObject[ nameof( Default ) ]?.ToObject< string >() ?? ( configChanged ? DefaultCollection : Empty.Name );
            var defaultIdx  = GetIndexForCollectionName( defaultName );
            if( defaultIdx < 0 )
            {
                ChatUtil.NotificationMessage( $"Last choice of {ConfigWindow.DefaultCollection} {defaultName} is not available, reset to {Empty.Name}.", "Load Failure",
                    NotificationType.Warning );
                Default       = Empty;
                configChanged = true;
            }
            else
            {
                Default = this[ defaultIdx ];
            }

            // Load the interface collection.
            var interfaceName = jObject[ nameof( Interface ) ]?.ToObject< string >() ?? Default.Name;
            var interfaceIdx  = GetIndexForCollectionName( interfaceName );
            if( interfaceIdx < 0 )
            {
                ChatUtil.NotificationMessage(
                    $"Last choice of {ConfigWindow.InterfaceCollection} {interfaceName} is not available, reset to {Empty.Name}.", "Load Failure", NotificationType.Warning );
                Interface     = Empty;
                configChanged = true;
            }
            else
            {
                Interface = this[ interfaceIdx ];
            }

            // Load the current collection.
            var currentName = jObject[ nameof( Current ) ]?.ToObject< string >() ?? DefaultCollection;
            var currentIdx  = GetIndexForCollectionName( currentName );
            if( currentIdx < 0 )
            {
                ChatUtil.NotificationMessage(
                    $"Last choice of {ConfigWindow.SelectedCollection} {currentName} is not available, reset to {DefaultCollection}.", "Load Failure", NotificationType.Warning );
                Current       = DefaultName;
                configChanged = true;
            }
            else
            {
                Current = this[ currentIdx ];
            }

            // Load special collections.
            foreach( var (type, name, _) in CollectionTypeExtensions.Special )
            {
                var typeName = jObject[ type.ToString() ]?.ToObject< string >();
                if( typeName != null )
                {
                    var idx = GetIndexForCollectionName( typeName );
                    if( idx < 0 )
                    {
                        ChatUtil.NotificationMessage( $"Last choice of {name} Collection {typeName} is not available, removed.", "Load Failure", NotificationType.Warning );
                        configChanged = true;
                    }
                    else
                    {
                        _specialCollections[ ( int )type ] = this[ idx ];
                    }
                }
            }

            configChanged |= MigrateIndividualCollections( jObject );
            configChanged |= Individuals.ReadJObject( jObject[ nameof( Individuals ) ] as JArray, this );

            // Save any changes and create all required caches.
            if( configChanged )
            {
                SaveActiveCollections();
            }
        }

        // Migrate ungendered collections to Male and Female for 0.5.9.0.
        public static void MigrateUngenderedCollections()
        {
            if( !ReadActiveCollections( out var jObject ) )
            {
                return;
            }

            foreach( var (type, _, _) in CollectionTypeExtensions.Special.Where( t => t.Item2.StartsWith( "Male " ) ) )
            {
                var oldName = type.ToString()[ 4.. ];
                var value   = jObject[ oldName ];
                if( value == null )
                {
                    continue;
                }

                jObject.Remove( oldName );
                jObject.Add( "Male"   + oldName, value );
                jObject.Add( "Female" + oldName, value );
            }

            using var stream = File.Open( ActiveCollectionFile, FileMode.Truncate );
            using var writer = new StreamWriter( stream );
            using var j      = new JsonTextWriter( writer );
            j.Formatting = Formatting.Indented;
            jObject.WriteTo( j );
        }

        // Migrate individual collections to Identifiers for 0.6.0.
        private bool MigrateIndividualCollections( JObject jObject )
        {
            var version = jObject[ nameof( Version ) ]?.Value< int >() ?? 0;
            if( version > 0 )
            {
                return false;
            }

            // Load character collections. If a player name comes up multiple times, the last one is applied.
            var characters = jObject[ "Characters" ]?.ToObject< Dictionary< string, string > >() ?? new Dictionary< string, string >();
            var dict       = new Dictionary< string, ModCollection >( characters.Count );
            foreach( var (player, collectionName) in characters )
            {
                var idx = GetIndexForCollectionName( collectionName );
                if( idx < 0 )
                {
                    ChatUtil.NotificationMessage( $"Last choice of <{player}>'s Collection {collectionName} is not available, reset to {Empty.Name}.", "Load Failure",
                        NotificationType.Warning );
                    dict.Add( player, Empty );
                }
                else
                {
                    dict.Add( player, this[ idx ] );
                }
            }

            Individuals.Migrate0To1( dict );
            return true;
        }

        public void SaveActiveCollections()
        {
            Penumbra.Framework.RegisterDelayed( nameof( SaveActiveCollections ),
                SaveActiveCollectionsInternal );
        }

        internal void SaveActiveCollectionsInternal()
        {
            var file = ActiveCollectionFile;
            try
            {
                var jObj = new JObject
                {
                    { nameof( Version ), Version },
                    { nameof( Default ), Default.Name },
                    { nameof( Interface ), Interface.Name },
                    { nameof( Current ), Current.Name },
                };
                foreach( var (type, collection) in _specialCollections.WithIndex().Where( p => p.Value != null ).Select( p => ( ( CollectionType )p.Index, p.Value! ) ) )
                {
                    jObj.Add( type.ToString(), collection.Name );
                }

                jObj.Add( nameof( Individuals ), Individuals.ToJObject() );
                using var stream = File.Open( file, File.Exists( file ) ? FileMode.Truncate : FileMode.CreateNew );
                using var writer = new StreamWriter( stream );
                using var j = new JsonTextWriter( writer )
                    { Formatting = Formatting.Indented };
                jObj.WriteTo( j );
                Penumbra.Log.Verbose( "Active Collections saved." );
            }
            catch( Exception e )
            {
                Penumbra.Log.Error( $"Could not save active collections to file {file}:\n{e}" );
            }
        }

        // Read the active collection file into a jObject.
        // Returns true if this is successful, false if the file does not exist or it is unsuccessful.
        private static bool ReadActiveCollections( out JObject ret )
        {
            var file = ActiveCollectionFile;
            if( File.Exists( file ) )
            {
                try
                {
                    ret = JObject.Parse( File.ReadAllText( file ) );
                    return true;
                }
                catch( Exception e )
                {
                    Penumbra.Log.Error( $"Could not read active collections from file {file}:\n{e}" );
                }
            }

            ret = new JObject();
            return false;
        }

        // Save if any of the active collections is changed.
        private void SaveOnChange( CollectionType collectionType, ModCollection? _1, ModCollection? _2, string _3 )
        {
            if( collectionType != CollectionType.Inactive )
            {
                SaveActiveCollections();
            }
        }

        // Cache handling. Usually recreate caches on the next framework tick,
        // but at launch create all of them at once.
        public void CreateNecessaryCaches()
        {
            var tasks = _specialCollections.OfType< ModCollection >()
               .Concat( Individuals.Select( p => p.Collection ) )
               .Prepend( Current )
               .Prepend( Default )
               .Prepend( Interface )
               .Distinct()
               .Select( c => Task.Run( c.CalculateEffectiveFileListInternal ) )
               .ToArray();

            Task.WaitAll( tasks );
        }

        private void RemoveCache( int idx )
        {
            if( idx != Empty.Index
            && idx  != Default.Index
            && idx  != Interface.Index
            && idx  != Current.Index
            && _specialCollections.All( c => c == null || c.Index != idx )
            && Individuals.Select( p => p.Collection ).All( c => c.Index != idx ) )
            {
                _collections[ idx ].ClearCache();
            }
        }

        // Recalculate effective files for active collections on events.
        private void OnModAddedActive( Mod mod )
        {
            foreach( var collection in this.Where( c => c.HasCache && c[ mod.Index ].Settings?.Enabled == true ) )
            {
                collection._cache!.AddMod( mod, true );
            }
        }

        private void OnModRemovedActive( Mod mod )
        {
            foreach( var collection in this.Where( c => c.HasCache && c[ mod.Index ].Settings?.Enabled == true ) )
            {
                collection._cache!.RemoveMod( mod, true );
            }
        }

        private void OnModMovedActive( Mod mod )
        {
            foreach( var collection in this.Where( c => c.HasCache && c[ mod.Index ].Settings?.Enabled == true ) )
            {
                collection._cache!.ReloadMod( mod, true );
            }
        }
    }
}