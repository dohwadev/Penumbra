using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Interface;
using Dalamud.Interface.Internal.Notifications;
using Dalamud.Utility;
using ImGuiNET;
using Lumina.Excel.GeneratedSheets;
using OtterGui;
using OtterGui.Raii;
using OtterGui.Widgets;
using Penumbra.Api.Enums;
using Penumbra.Collections;
using Penumbra.GameData.Enums;
using Penumbra.GameData.Structs;
using Penumbra.Mods;
using Penumbra.Mods.ItemSwap;
using Penumbra.Util;

namespace Penumbra.UI.Classes;

public class ItemSwapWindow : IDisposable
{
    private enum SwapType
    {
        Hat,
        Top,
        Gloves,
        Pants,
        Shoes,
        Earrings,
        Necklace,
        Bracelet,
        Ring,
        Hair,
        Face,
        Ears,
        Tail,
        Weapon,
    }

    private class ItemSelector : FilterComboCache< (string, Item) >
    {
        public ItemSelector( FullEquipType type )
            : base( () => Penumbra.ItemData[ type ].Select( i => ( i.Name.ToDalamudString().TextValue, i ) ).ToArray() )
        { }

        protected override string ToString( (string, Item) obj )
            => obj.Item1;
    }

    private class WeaponSelector : FilterComboCache< FullEquipType >
    {
        public WeaponSelector()
            : base( FullEquipTypeExtensions.WeaponTypes.Concat( FullEquipTypeExtensions.ToolTypes ) )
        { }

        protected override string ToString( FullEquipType type )
            => type.ToName();
    }

    public ItemSwapWindow()
    {
        Penumbra.CollectionManager.CollectionChanged         += OnCollectionChange;
        Penumbra.CollectionManager.Current.ModSettingChanged += OnSettingChange;
    }

    public void Dispose()
    {
        Penumbra.CollectionManager.CollectionChanged         += OnCollectionChange;
        Penumbra.CollectionManager.Current.ModSettingChanged -= OnSettingChange;
    }

    private readonly Dictionary< SwapType, (ItemSelector Source, ItemSelector Target, string TextFrom, string TextTo) > _selectors = new()
    {
        [ SwapType.Hat ]      = ( new ItemSelector( FullEquipType.Head ), new ItemSelector( FullEquipType.Head ), "Take this Hat", "and put it on this one" ),
        [ SwapType.Top ]      = ( new ItemSelector( FullEquipType.Body ), new ItemSelector( FullEquipType.Body ), "Take this Top", "and put it on this one" ),
        [ SwapType.Gloves ]   = ( new ItemSelector( FullEquipType.Hands ), new ItemSelector( FullEquipType.Hands ), "Take these Gloves", "and put them on these" ),
        [ SwapType.Pants ]    = ( new ItemSelector( FullEquipType.Legs ), new ItemSelector( FullEquipType.Legs ), "Take these Pants", "and put them on these" ),
        [ SwapType.Shoes ]    = ( new ItemSelector( FullEquipType.Feet ), new ItemSelector( FullEquipType.Feet ), "Take these Shoes", "and put them on these" ),
        [ SwapType.Earrings ] = ( new ItemSelector( FullEquipType.Ears ), new ItemSelector( FullEquipType.Ears ), "Take these Earrings", "and put them on these" ),
        [ SwapType.Necklace ] = ( new ItemSelector( FullEquipType.Neck ), new ItemSelector( FullEquipType.Neck ), "Take this Necklace", "and put it on this one" ),
        [ SwapType.Bracelet ] = ( new ItemSelector( FullEquipType.Wrists ), new ItemSelector( FullEquipType.Wrists ), "Take these Bracelets", "and put them on these" ),
        [ SwapType.Ring ]     = ( new ItemSelector( FullEquipType.Finger ), new ItemSelector( FullEquipType.Finger ), "Take this Ring", "and put it on this one" ),
    };

    private          ItemSelector?     _weaponSource = null;
    private          ItemSelector?     _weaponTarget = null;
    private readonly WeaponSelector    _slotSelector = new();
    private readonly ItemSwapContainer _swapData     = new();

    private Mod?         _mod;
    private ModSettings? _modSettings;
    private bool         _dirty;

    private SwapType   _lastTab       = SwapType.Hair;
    private Gender     _currentGender = Gender.Male;
    private ModelRace  _currentRace   = ModelRace.Midlander;
    private int        _targetId      = 0;
    private int        _sourceId      = 0;
    private Exception? _loadException = null;

    private string     _newModName           = string.Empty;
    private string     _newGroupName         = "Swaps";
    private string     _newOptionName        = string.Empty;
    private IModGroup? _selectedGroup        = null;
    private bool       _subModValid          = false;
    private bool       _useFileSwaps         = true;
    private bool       _useCurrentCollection = false;

    private Item[]? _affectedItems;

    public void UpdateMod( Mod mod, ModSettings? settings )
    {
        if( mod == _mod && settings == _modSettings )
        {
            return;
        }

        var oldDefaultName = $"{_mod?.Name.Text ?? "Unknown"} (Swapped)";
        if( _newModName.Length == 0 || oldDefaultName == _newModName )
        {
            _newModName = $"{mod.Name.Text} (Swapped)";
        }

        _mod         = mod;
        _modSettings = settings;
        _swapData.LoadMod( _mod, _modSettings );
        UpdateOption();
        _dirty = true;
    }

    private void UpdateState()
    {
        if( !_dirty )
        {
            return;
        }

        _swapData.Clear();
        _loadException = null;
        _affectedItems = null;
        try
        {
            switch( _lastTab )
            {
                case SwapType.Hat:
                case SwapType.Top:
                case SwapType.Gloves:
                case SwapType.Pants:
                case SwapType.Shoes:
                case SwapType.Earrings:
                case SwapType.Necklace:
                case SwapType.Bracelet:
                case SwapType.Ring:
                    var values = _selectors[ _lastTab ];
                    if( values.Source.CurrentSelection.Item2 != null && values.Target.CurrentSelection.Item2 != null )
                    {
                        _affectedItems = _swapData.LoadEquipment( values.Target.CurrentSelection.Item2, values.Source.CurrentSelection.Item2,
                            _useCurrentCollection ? Penumbra.CollectionManager.Current : null );
                    }

                    break;
                case SwapType.Hair when _targetId > 0 && _sourceId > 0:
                    _swapData.LoadCustomization( BodySlot.Hair, Names.CombinedRace( _currentGender, _currentRace ), ( SetId )_sourceId, ( SetId )_targetId,
                        _useCurrentCollection ? Penumbra.CollectionManager.Current : null );
                    break;
                case SwapType.Face when _targetId > 0 && _sourceId > 0:
                    _swapData.LoadCustomization( BodySlot.Face, Names.CombinedRace( _currentGender, _currentRace ), ( SetId )_sourceId, ( SetId )_targetId,
                        _useCurrentCollection ? Penumbra.CollectionManager.Current : null );
                    break;
                case SwapType.Ears when _targetId > 0 && _sourceId > 0:
                    _swapData.LoadCustomization( BodySlot.Zear, Names.CombinedRace( _currentGender, ModelRace.Viera ), ( SetId )_sourceId, ( SetId )_targetId,
                        _useCurrentCollection ? Penumbra.CollectionManager.Current : null );
                    break;
                case SwapType.Tail when _targetId > 0 && _sourceId > 0:
                    _swapData.LoadCustomization( BodySlot.Tail, Names.CombinedRace( _currentGender, _currentRace ), ( SetId )_sourceId, ( SetId )_targetId,
                        _useCurrentCollection ? Penumbra.CollectionManager.Current : null );
                    break;
                case SwapType.Weapon: break;
            }
        }
        catch( Exception e )
        {
            Penumbra.Log.Error( $"Could not get Customization Data container for {_lastTab}:\n{e}" );
            _loadException = e;
            _affectedItems = null;
            _swapData.Clear();
        }

        _dirty = false;
    }

    private static string SwapToString( Swap swap )
    {
        return swap switch
        {
            MetaSwap meta => $"{meta.SwapFrom}: {meta.SwapFrom.EntryToString()} -> {meta.SwapApplied.EntryToString()}",
            FileSwap file => $"{file.Type}: {file.SwapFromRequestPath} -> {file.SwapToModded.FullName}{( file.DataWasChanged ? " (EDITED)" : string.Empty )}",
            _             => string.Empty,
        };
    }

    private string CreateDescription()
        => $"Created by swapping {_lastTab} {_sourceId} onto {_lastTab} {_targetId} for {_currentRace.ToName()} {_currentGender.ToName()}s in {_mod!.Name}.";

    private void UpdateOption()
    {
        _selectedGroup = _mod?.Groups.FirstOrDefault( g => g.Name == _newGroupName );
        _subModValid   = _mod != null && _newGroupName.Length > 0 && _newOptionName.Length > 0 && ( _selectedGroup?.All( o => o.Name != _newOptionName ) ?? true );
    }

    private void CreateMod()
    {
        var newDir = Mod.CreateModFolder( Penumbra.ModManager.BasePath, _newModName );
        Mod.CreateMeta( newDir, _newModName, Penumbra.Config.DefaultModAuthor, CreateDescription(), "1.0", string.Empty );
        Mod.CreateDefaultFiles( newDir );
        Penumbra.ModManager.AddMod( newDir );
        if( !_swapData.WriteMod( Penumbra.ModManager.Last(), _useFileSwaps ? ItemSwapContainer.WriteType.UseSwaps : ItemSwapContainer.WriteType.NoSwaps ) )
        {
            Penumbra.ModManager.DeleteMod( Penumbra.ModManager.Count - 1 );
        }
    }

    private void CreateOption()
    {
        if( _mod == null || !_subModValid )
        {
            return;
        }

        var            groupCreated     = false;
        var            dirCreated       = false;
        var            optionCreated    = false;
        DirectoryInfo? optionFolderName = null;
        try
        {
            optionFolderName = Mod.NewSubFolderName( new DirectoryInfo( Path.Combine( _mod.ModPath.FullName, _selectedGroup?.Name ?? _newGroupName ) ), _newOptionName );
            if( optionFolderName?.Exists == true )
            {
                throw new Exception( $"The folder {optionFolderName.FullName} for the option already exists." );
            }

            if( optionFolderName != null )
            {
                if( _selectedGroup == null )
                {
                    Penumbra.ModManager.AddModGroup( _mod, GroupType.Multi, _newGroupName );
                    _selectedGroup = _mod.Groups.Last();
                    groupCreated   = true;
                }

                Penumbra.ModManager.AddOption( _mod, _mod.Groups.IndexOf( _selectedGroup ), _newOptionName );
                optionCreated    = true;
                optionFolderName = Directory.CreateDirectory( optionFolderName.FullName );
                dirCreated       = true;
                if( !_swapData.WriteMod( _mod, _useFileSwaps ? ItemSwapContainer.WriteType.UseSwaps : ItemSwapContainer.WriteType.NoSwaps, optionFolderName,
                       _mod.Groups.IndexOf( _selectedGroup ), _selectedGroup.Count - 1 ) )
                {
                    throw new Exception( "Failure writing files for mod swap." );
                }
            }
        }
        catch( Exception e )
        {
            ChatUtil.NotificationMessage( $"Could not create new Swap Option:\n{e}", "Error", NotificationType.Error );
            try
            {
                if( optionCreated && _selectedGroup != null )
                {
                    Penumbra.ModManager.DeleteOption( _mod, _mod.Groups.IndexOf( _selectedGroup ), _selectedGroup.Count - 1 );
                }

                if( groupCreated )
                {
                    Penumbra.ModManager.DeleteModGroup( _mod, _mod.Groups.IndexOf( _selectedGroup! ) );
                    _selectedGroup = null;
                }

                if( dirCreated && optionFolderName != null )
                {
                    Directory.Delete( optionFolderName.FullName, true );
                }
            }
            catch
            {
                // ignored
            }
        }

        UpdateOption();
    }

    private void DrawHeaderLine( float width )
    {
        var newModAvailable = _loadException == null && _swapData.Loaded;

        ImGui.SetNextItemWidth( width );
        if( ImGui.InputTextWithHint( "##newModName", "New Mod Name...", ref _newModName, 64 ) )
        { }

        ImGui.SameLine();
        var tt = !newModAvailable
            ? "No swap is currently loaded."
            : _newModName.Length == 0
                ? "Please enter a name for your mod."
                : "Create a new mod of the given name containing only the swap.";
        if( ImGuiUtil.DrawDisabledButton( "Create New Mod", new Vector2( width / 2, 0 ), tt, !newModAvailable || _newModName.Length == 0 ) )
        {
            CreateMod();
        }

        ImGui.SameLine();
        ImGui.SetCursorPosX( ImGui.GetCursorPosX() + 20 * ImGuiHelpers.GlobalScale );
        ImGui.Checkbox( "Use File Swaps", ref _useFileSwaps );
        ImGuiUtil.HoverTooltip( "Instead of writing every single non-default file to the newly created mod or option,\n"
          + "even those available from game files, use File Swaps to default game files where possible." );

        ImGui.SetNextItemWidth( ( width - ImGui.GetStyle().ItemSpacing.X ) / 2 );
        if( ImGui.InputTextWithHint( "##groupName", "Group Name...", ref _newGroupName, 32 ) )
        {
            UpdateOption();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth( ( width - ImGui.GetStyle().ItemSpacing.X ) / 2 );
        if( ImGui.InputTextWithHint( "##optionName", "New Option Name...", ref _newOptionName, 32 ) )
        {
            UpdateOption();
        }

        ImGui.SameLine();
        tt = !_subModValid
            ? "An option with that name already exists in that group, or no name is specified."
            : !newModAvailable
                ? "Create a new option inside this mod containing only the swap."
                : "Create a new option (and possibly Multi-Group) inside the currently selected mod containing the swap.";
        if( ImGuiUtil.DrawDisabledButton( "Create New Option", new Vector2( width / 2, 0 ), tt, !newModAvailable || !_subModValid ) )
        {
            CreateOption();
        }

        ImGui.SameLine();
        ImGui.SetCursorPosX( ImGui.GetCursorPosX() + 20 * ImGuiHelpers.GlobalScale );
        _dirty |= ImGui.Checkbox( "Use Entire Collection", ref _useCurrentCollection );
        ImGuiUtil.HoverTooltip( "Use all applied mods from the Selected Collection with their current settings and respecting the enabled state of mods and inheritance,\n"
          + "instead of using only the selected mod with its current settings in the Selected collection or the default settings, ignoring the enabled state and inheritance." );
    }

    private void DrawSwapBar()
    {
        using var bar = ImRaii.TabBar( "##swapBar", ImGuiTabBarFlags.None );

        DrawEquipmentSwap( SwapType.Hat );
        DrawEquipmentSwap( SwapType.Top );
        DrawEquipmentSwap( SwapType.Gloves );
        DrawEquipmentSwap( SwapType.Pants );
        DrawEquipmentSwap( SwapType.Shoes );
        DrawEquipmentSwap( SwapType.Earrings );
        DrawEquipmentSwap( SwapType.Necklace );
        DrawEquipmentSwap( SwapType.Bracelet );
        DrawEquipmentSwap( SwapType.Ring );
        DrawHairSwap();
        DrawFaceSwap();
        DrawEarSwap();
        DrawTailSwap();
        DrawWeaponSwap();
    }

    private ImRaii.IEndObject DrawTab( SwapType newTab )
    {
        using var tab = ImRaii.TabItem( newTab.ToString() );
        if( tab )
        {
            _dirty   |= _lastTab != newTab;
            _lastTab =  newTab;
        }

        UpdateState();

        return tab;
    }

    private void DrawEquipmentSwap( SwapType type )
    {
        using var tab = DrawTab( type );
        if( !tab )
        {
            return;
        }

        var (sourceSelector, targetSelector, text1, text2) = _selectors[ type ];
        using var table = ImRaii.Table( "##settings", 2, ImGuiTableFlags.SizingFixedFit );
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted( text1 );
        ImGui.TableNextColumn();
        _dirty |= sourceSelector.Draw( "##itemSource", sourceSelector.CurrentSelection.Item1 ?? string.Empty, InputWidth * 2, ImGui.GetTextLineHeightWithSpacing() );

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted( text2 );
        ImGui.TableNextColumn();
        _dirty |= targetSelector.Draw( "##itemTarget", targetSelector.CurrentSelection.Item1 ?? string.Empty, InputWidth * 2, ImGui.GetTextLineHeightWithSpacing() );

        if( _affectedItems is { Length: > 1 } )
        {
            ImGui.SameLine();
            ImGuiUtil.DrawTextButton( $"which will also affect {_affectedItems.Length} other Items.", Vector2.Zero, Colors.PressEnterWarningBg );
            if( ImGui.IsItemHovered() )
            {
                ImGui.SetTooltip( string.Join( '\n', _affectedItems.Where( i => !ReferenceEquals( i, targetSelector.CurrentSelection.Item2 ) )
                   .Select( i => i.Name.ToDalamudString().TextValue ) ) );
            }
        }
    }

    private void DrawHairSwap()
    {
        using var tab = DrawTab( SwapType.Hair );
        if( !tab )
        {
            return;
        }

        using var table = ImRaii.Table( "##settings", 2, ImGuiTableFlags.SizingFixedFit );
        DrawTargetIdInput( "Take this Hairstyle" );
        DrawSourceIdInput();
        DrawGenderInput();
    }

    private void DrawFaceSwap()
    {
        using var disabled = ImRaii.Disabled();
        using var tab      = DrawTab( SwapType.Face );
        if( !tab )
        {
            return;
        }

        using var table = ImRaii.Table( "##settings", 2, ImGuiTableFlags.SizingFixedFit );
        DrawTargetIdInput( "Take this Face Type" );
        DrawSourceIdInput();
        DrawGenderInput();
    }

    private void DrawTailSwap()
    {
        using var tab = DrawTab( SwapType.Tail );
        if( !tab )
        {
            return;
        }

        using var table = ImRaii.Table( "##settings", 2, ImGuiTableFlags.SizingFixedFit );
        DrawTargetIdInput( "Take this Tail Type" );
        DrawSourceIdInput();
        DrawGenderInput( "for all", 2 );
    }


    private void DrawEarSwap()
    {
        using var tab = DrawTab( SwapType.Ears );
        if( !tab )
        {
            return;
        }

        using var table = ImRaii.Table( "##settings", 2, ImGuiTableFlags.SizingFixedFit );
        DrawTargetIdInput( "Take this Ear Type" );
        DrawSourceIdInput();
        DrawGenderInput( "for all Viera", 0 );
    }


    private void DrawWeaponSwap()
    {
        using var disabled = ImRaii.Disabled();
        using var tab      = DrawTab( SwapType.Weapon );
        if( !tab )
        {
            return;
        }

        using var table = ImRaii.Table( "##settings", 2, ImGuiTableFlags.SizingFixedFit );
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted( "Select the weapon or tool you want" );
        ImGui.TableNextColumn();
        if( _slotSelector.Draw( "##weaponSlot", _slotSelector.CurrentSelection.ToName(), InputWidth * 2, ImGui.GetTextLineHeightWithSpacing() ) )
        {
            _dirty        = true;
            _weaponSource = new ItemSelector( _slotSelector.CurrentSelection );
            _weaponTarget = new ItemSelector( _slotSelector.CurrentSelection );
        }
        else
        {
            _dirty        =   _weaponSource == null || _weaponTarget == null;
            _weaponSource ??= new ItemSelector( _slotSelector.CurrentSelection );
            _weaponTarget ??= new ItemSelector( _slotSelector.CurrentSelection );
        }

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted( "and put this variant of it" );
        ImGui.TableNextColumn();
        _dirty |= _weaponSource.Draw( "##weaponSource", _weaponSource.CurrentSelection.Item1 ?? string.Empty, InputWidth * 2, ImGui.GetTextLineHeightWithSpacing() );

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted( "onto this one" );
        ImGui.TableNextColumn();
        _dirty |= _weaponTarget.Draw( "##weaponTarget", _weaponTarget.CurrentSelection.Item1 ?? string.Empty, InputWidth * 2, ImGui.GetTextLineHeightWithSpacing() );
    }

    private const float InputWidth = 120;

    private void DrawTargetIdInput( string text = "Take this ID" )
    {
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted( text );

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth( InputWidth * ImGuiHelpers.GlobalScale );
        if( ImGui.InputInt( "##targetId", ref _targetId, 0, 0 ) )
        {
            _targetId = Math.Clamp( _targetId, 0, byte.MaxValue );
        }

        _dirty |= ImGui.IsItemDeactivatedAfterEdit();
    }

    private void DrawSourceIdInput( string text = "and put it on this one" )
    {
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted( text );

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth( InputWidth * ImGuiHelpers.GlobalScale );
        if( ImGui.InputInt( "##sourceId", ref _sourceId, 0, 0 ) )
        {
            _sourceId = Math.Clamp( _sourceId, 0, byte.MaxValue );
        }

        _dirty |= ImGui.IsItemDeactivatedAfterEdit();
    }

    private void DrawGenderInput( string text = "for all", int drawRace = 1 )
    {
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted( text );

        ImGui.TableNextColumn();
        _dirty |= Combos.Gender( "##Gender", InputWidth, _currentGender, out _currentGender );
        if( drawRace == 1 )
        {
            ImGui.SameLine();
            _dirty |= Combos.Race( "##Race", InputWidth, _currentRace, out _currentRace );
        }
        else if( drawRace == 2 )
        {
            ImGui.SameLine();
            if( _currentRace is not ModelRace.Miqote and not ModelRace.AuRa and not ModelRace.Hrothgar )
            {
                _currentRace = ModelRace.Miqote;
            }

            _dirty |= ImGuiUtil.GenericEnumCombo( "##Race", InputWidth, _currentRace, out _currentRace, new[] { ModelRace.Miqote, ModelRace.AuRa, ModelRace.Hrothgar },
                RaceEnumExtensions.ToName );
        }
    }

    private string NonExistentText()
        => _lastTab switch
        {
            SwapType.Hat      => "One of the selected hats does not seem to exist.",
            SwapType.Top      => "One of the selected tops does not seem to exist.",
            SwapType.Gloves   => "One of the selected pairs of gloves does not seem to exist.",
            SwapType.Pants    => "One of the selected pants does not seem to exist.",
            SwapType.Shoes    => "One of the selected pairs of shoes does not seem to exist.",
            SwapType.Earrings => "One of the selected earrings does not seem to exist.",
            SwapType.Necklace => "One of the selected necklaces does not seem to exist.",
            SwapType.Bracelet => "One of the selected bracelets does not seem to exist.",
            SwapType.Ring     => "One of the selected rings does not seem to exist.",
            SwapType.Hair     => "One of the selected hairstyles does not seem to exist for this gender and race combo.",
            SwapType.Face     => "One of the selected faces does not seem to exist for this gender and race combo.",
            SwapType.Ears     => "One of the selected ear types does not seem to exist for this gender and race combo.",
            SwapType.Tail     => "One of the selected tails does not seem to exist for this gender and race combo.",
            SwapType.Weapon   => "One of the selected weapons or tools does not seem to exist.",
            _                 => string.Empty,
        };


    public void DrawItemSwapPanel()
    {
        using var tab = ImRaii.TabItem( "Item Swap (WIP)" );
        if( !tab )
        {
            return;
        }

        ImGui.NewLine();
        DrawHeaderLine( 300 * ImGuiHelpers.GlobalScale );
        ImGui.NewLine();

        DrawSwapBar();

        using var table = ImRaii.ListBox( "##swaps", -Vector2.One );
        if( _loadException != null )
        {
            ImGuiUtil.TextWrapped( $"Could not load Customization Swap:\n{_loadException}" );
        }
        else if( _swapData.Loaded )
        {
            foreach( var swap in _swapData.Swaps )
            {
                DrawSwap( swap );
            }
        }
        else
        {
            ImGui.TextUnformatted( NonExistentText() );
        }
    }

    private static void DrawSwap( Swap swap )
    {
        var       flags = swap.ChildSwaps.Count == 0 ? ImGuiTreeNodeFlags.Bullet | ImGuiTreeNodeFlags.Leaf : ImGuiTreeNodeFlags.DefaultOpen;
        using var tree  = ImRaii.TreeNode( SwapToString( swap ), flags );
        if( !tree )
        {
            return;
        }

        foreach( var child in swap.ChildSwaps )
        {
            DrawSwap( child );
        }
    }

    private void OnCollectionChange( CollectionType collectionType, ModCollection? oldCollection,
        ModCollection? newCollection, string _ )
    {
        if( collectionType != CollectionType.Current || _mod == null || newCollection == null )
        {
            return;
        }

        UpdateMod( _mod, _mod.Index < newCollection.Settings.Count ? newCollection.Settings[ _mod.Index ] : null  );
        newCollection.ModSettingChanged += OnSettingChange;
        if( oldCollection != null )
        {
            oldCollection.ModSettingChanged -= OnSettingChange;
        }
    }

    private void OnSettingChange( ModSettingChange type, int modIdx, int oldValue, int groupIdx, bool inherited )
    {
        if( modIdx == _mod?.Index )
        {
            _swapData.LoadMod( _mod, _modSettings );
            _dirty = true;
        }
    }
}