using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lumina.Data.Parsing;
using Lumina.Excel.GeneratedSheets;
using Penumbra.GameData.Data;
using Penumbra.GameData.Enums;
using Penumbra.GameData.Files;
using Penumbra.GameData.Structs;
using Penumbra.Interop.Structs;
using Penumbra.Meta.Files;
using Penumbra.Meta.Manipulations;
using Penumbra.String.Classes;

namespace Penumbra.Mods.ItemSwap;

public static class EquipmentSwap
{
    public static Item[] CreateItemSwap( List< Swap > swaps, Func< Utf8GamePath, FullPath > redirections, Func< MetaManipulation, MetaManipulation > manips, Item itemFrom,
        Item itemTo )
    {
        // Check actual ids, variants and slots. We only support using the same slot.
        LookupItem( itemFrom, out var slotFrom, out var idFrom, out var variantFrom );
        LookupItem( itemTo, out var slotTo, out var idTo, out var variantTo );
        if( slotFrom != slotTo )
        {
            throw new ItemSwap.InvalidItemTypeException();
        }

        var eqp = CreateEqp( manips, slotFrom, idFrom, idTo );
        if( eqp != null )
        {
            swaps.Add( eqp );
        }

        var gmp = CreateGmp( manips, slotFrom, idFrom, idTo );
        if( gmp != null )
        {
            swaps.Add( gmp );
        }


        var (imcFileFrom, variants, affectedItems) = GetVariants( slotFrom, idFrom, idTo, variantFrom );
        var imcFileTo = new ImcFile( new ImcManipulation( slotFrom, variantTo, idTo.Value, default ) );

        var isAccessory = slotFrom.IsAccessory();
        var estType = slotFrom switch
        {
            EquipSlot.Head => EstManipulation.EstType.Head,
            EquipSlot.Body => EstManipulation.EstType.Body,
            _              => ( EstManipulation.EstType )0,
        };

        var skipFemale    = false;
        var skipMale      = false;
        var mtrlVariantTo = imcFileTo.GetEntry( ImcFile.PartIndex( slotFrom ), variantTo ).MaterialId;
        foreach( var gr in Enum.GetValues< GenderRace >() )
        {
            switch( gr.Split().Item1 )
            {
                case Gender.Male when skipMale:        continue;
                case Gender.Female when skipFemale:    continue;
                case Gender.MaleNpc when skipMale:     continue;
                case Gender.FemaleNpc when skipFemale: continue;
            }

            if( CharacterUtility.EqdpIdx( gr, isAccessory ) < 0 )
            {
                continue;
            }

            var est = ItemSwap.CreateEst( redirections, manips, estType, gr, idFrom, idTo );
            if( est != null )
            {
                swaps.Add( est );
            }

            try
            {
                var eqdp = CreateEqdp( redirections, manips, slotFrom, gr, idFrom, idTo, mtrlVariantTo );
                if( eqdp != null )
                {
                    swaps.Add( eqdp );
                }
            }
            catch( ItemSwap.MissingFileException e )
            {
                switch( gr )
                {
                    case GenderRace.MidlanderMale when e.Type == ResourceType.Mdl:
                        skipMale = true;
                        continue;
                    case GenderRace.MidlanderFemale when e.Type == ResourceType.Mdl:
                        skipFemale = true;
                        continue;
                    default: throw;
                }
            }
        }

        foreach( var variant in variants )
        {
            var imc = CreateImc( redirections, manips, slotFrom, idFrom, idTo, variant, variantTo, imcFileFrom, imcFileTo );
            swaps.Add( imc );
        }

        return affectedItems;
    }

    public static MetaSwap? CreateEqdp( Func< Utf8GamePath, FullPath > redirections, Func< MetaManipulation, MetaManipulation > manips, EquipSlot slot, GenderRace gr, SetId idFrom,
        SetId idTo, byte mtrlTo )
    {
        var (gender, race) = gr.Split();
        var eqdpFrom = new EqdpManipulation( ExpandedEqdpFile.GetDefault( gr, slot.IsAccessory(), idFrom.Value ), slot, gender, race, idFrom.Value );
        var eqdpTo   = new EqdpManipulation( ExpandedEqdpFile.GetDefault( gr, slot.IsAccessory(), idTo.Value ), slot, gender, race, idTo.Value );
        var meta     = new MetaSwap( manips, eqdpFrom, eqdpTo );
        var (ownMtrl, ownMdl) = meta.SwapApplied.Eqdp.Entry.ToBits( slot );
        if( ownMdl )
        {
            var mdl = CreateMdl( redirections, slot, gr, idFrom, idTo, mtrlTo );
            meta.ChildSwaps.Add( mdl );
        }
        else if( !ownMtrl && meta.SwapAppliedIsDefault )
        {
            meta = null;
        }

        return meta;
    }

    public static FileSwap CreateMdl( Func< Utf8GamePath, FullPath > redirections, EquipSlot slot, GenderRace gr, SetId idFrom, SetId idTo, byte mtrlTo )
    {
        var accessory   = slot.IsAccessory();
        var mdlPathFrom = accessory ? GamePaths.Accessory.Mdl.Path( idFrom, gr, slot ) : GamePaths.Equipment.Mdl.Path( idFrom, gr, slot );
        var mdlPathTo   = accessory ? GamePaths.Accessory.Mdl.Path( idTo, gr, slot ) : GamePaths.Equipment.Mdl.Path( idTo, gr, slot );
        var mdl         = FileSwap.CreateSwap( ResourceType.Mdl, redirections, mdlPathFrom, mdlPathTo );

        foreach( ref var fileName in mdl.AsMdl()!.Materials.AsSpan() )
        {
            var mtrl = CreateMtrl( redirections, slot, idFrom, idTo, mtrlTo, ref fileName, ref mdl.DataWasChanged );
            if( mtrl != null )
            {
                mdl.ChildSwaps.Add( mtrl );
            }
        }

        return mdl;
    }

    private static void LookupItem( Item i, out EquipSlot slot, out SetId modelId, out byte variant )
    {
        slot = ( ( EquipSlot )i.EquipSlotCategory.Row ).ToSlot();
        if( !slot.IsEquipmentPiece() )
        {
            throw new ItemSwap.InvalidItemTypeException();
        }

        modelId = ( ( Quad )i.ModelMain ).A;
        variant = ( byte )( ( Quad )i.ModelMain ).B;
    }

    private static (ImcFile, byte[], Item[]) GetVariants( EquipSlot slot, SetId idFrom, SetId idTo, byte variantFrom )
    {
        var    entry = new ImcManipulation( slot, variantFrom, idFrom.Value, default );
        var    imc   = new ImcFile( entry );
        Item[] items;
        byte[] variants;
        if( idFrom.Value == idTo.Value )
        {
            items    = Penumbra.Identifier.Identify( idFrom, variantFrom, slot ).ToArray();
            variants = new[] { variantFrom };
        }
        else
        {
            items = Penumbra.Identifier.Identify( slot.IsEquipment()
                ? GamePaths.Equipment.Mdl.Path( idFrom, GenderRace.MidlanderMale, slot )
                : GamePaths.Accessory.Mdl.Path( idFrom, GenderRace.MidlanderMale, slot ) ).Select( kvp => kvp.Value ).OfType< Item >().ToArray();
            variants = Enumerable.Range( 0, imc.Count + 1 ).Select( i => ( byte )i ).ToArray();
        }

        return ( imc, variants, items );
    }

    public static MetaSwap? CreateGmp( Func< MetaManipulation, MetaManipulation > manips, EquipSlot slot, SetId idFrom, SetId idTo )
    {
        if( slot is not EquipSlot.Head )
        {
            return null;
        }

        var manipFrom = new GmpManipulation( ExpandedGmpFile.GetDefault( idFrom.Value ), idFrom.Value );
        var manipTo   = new GmpManipulation( ExpandedGmpFile.GetDefault( idTo.Value ), idTo.Value );
        return new MetaSwap( manips, manipFrom, manipTo );
    }

    public static MetaSwap CreateImc( Func< Utf8GamePath, FullPath > redirections, Func< MetaManipulation, MetaManipulation > manips, EquipSlot slot, SetId idFrom, SetId idTo,
        byte variantFrom, byte variantTo, ImcFile imcFileFrom, ImcFile imcFileTo )
    {
        var entryFrom        = imcFileFrom.GetEntry( ImcFile.PartIndex( slot ), variantFrom );
        var entryTo          = imcFileTo.GetEntry( ImcFile.PartIndex( slot ), variantTo );
        var manipulationFrom = new ImcManipulation( slot, variantFrom, idFrom.Value, entryFrom );
        var manipulationTo   = new ImcManipulation( slot, variantTo, idTo.Value, entryTo );
        var imc              = new MetaSwap( manips, manipulationFrom, manipulationTo );

        var decal = CreateDecal( redirections, imc.SwapToModded.Imc.Entry.DecalId );
        if( decal != null )
        {
            imc.ChildSwaps.Add( decal );
        }

        var avfx = CreateAvfx( redirections, idFrom, idTo, imc.SwapToModded.Imc.Entry.VfxId );
        if( avfx != null )
        {
            imc.ChildSwaps.Add( avfx );
        }

        // IMC also controls sound, Example: Dodore Doublet, but unknown what it does?
        // IMC also controls some material animation, Example: The Howling Spirit and The Wailing Spirit, but unknown what it does.
        return imc;
    }

    // Example: Crimson Standard Bracelet
    public static FileSwap? CreateDecal( Func< Utf8GamePath, FullPath > redirections, byte decalId )
    {
        if( decalId == 0 )
        {
            return null;
        }

        var decalPath = GamePaths.Equipment.Decal.Path( decalId );
        return FileSwap.CreateSwap( ResourceType.Tex, redirections, decalPath, decalPath );
    }


    // Example: Abyssos Helm / Body
    public static FileSwap? CreateAvfx( Func< Utf8GamePath, FullPath > redirections, SetId idFrom, SetId idTo, byte vfxId )
    {
        if( vfxId == 0 )
        {
            return null;
        }

        var vfxPathFrom = GamePaths.Equipment.Avfx.Path( idFrom.Value, vfxId );
        var vfxPathTo   = GamePaths.Equipment.Avfx.Path( idTo.Value, vfxId );
        var avfx        = FileSwap.CreateSwap( ResourceType.Avfx, redirections, vfxPathFrom, vfxPathTo );

        foreach( ref var filePath in avfx.AsAvfx()!.Textures.AsSpan() )
        {
            var atex = CreateAtex( redirections, ref filePath, ref avfx.DataWasChanged );
            avfx.ChildSwaps.Add( atex );
        }

        return avfx;
    }

    public static MetaSwap? CreateEqp( Func< MetaManipulation, MetaManipulation > manips, EquipSlot slot, SetId idFrom, SetId idTo )
    {
        if( slot.IsAccessory() )
        {
            return null;
        }

        var eqpValueFrom = ExpandedEqpFile.GetDefault( idFrom.Value );
        var eqpValueTo   = ExpandedEqpFile.GetDefault( idTo.Value );
        var eqpFrom      = new EqpManipulation( eqpValueFrom, slot, idFrom.Value );
        var eqpTo        = new EqpManipulation( eqpValueTo, slot, idFrom.Value );
        return new MetaSwap( manips, eqpFrom, eqpTo );
    }

    public static FileSwap? CreateMtrl( Func< Utf8GamePath, FullPath > redirections, EquipSlot slot, SetId idFrom, SetId idTo, byte variantTo, ref string fileName,
        ref bool dataWasChanged )
    {
        var prefix = slot.IsAccessory() ? 'a' : 'e';
        if( !fileName.Contains( $"{prefix}{idTo.Value:D4}" ) )
        {
            return null;
        }

        var folderTo = slot.IsAccessory() ? GamePaths.Accessory.Mtrl.FolderPath( idTo, variantTo ) : GamePaths.Equipment.Mtrl.FolderPath( idTo, variantTo );
        var pathTo   = $"{folderTo}{fileName}";

        var folderFrom  = slot.IsAccessory() ? GamePaths.Accessory.Mtrl.FolderPath( idFrom, variantTo ) : GamePaths.Equipment.Mtrl.FolderPath( idFrom, variantTo );
        var newFileName = ItemSwap.ReplaceId( fileName, prefix, idTo, idFrom );
        var pathFrom    = $"{folderFrom}{newFileName}";

        if( newFileName != fileName )
        {
            fileName       = newFileName;
            dataWasChanged = true;
        }

        var mtrl = FileSwap.CreateSwap( ResourceType.Mtrl, redirections, pathFrom, pathTo );
        var shpk = CreateShader( redirections, ref mtrl.AsMtrl()!.ShaderPackage.Name, ref mtrl.DataWasChanged );
        mtrl.ChildSwaps.Add( shpk );

        foreach( ref var texture in mtrl.AsMtrl()!.Textures.AsSpan() )
        {
            var tex = CreateTex( redirections, prefix, idFrom, idTo, ref texture, ref mtrl.DataWasChanged );
            mtrl.ChildSwaps.Add( tex );
        }

        return mtrl;
    }

    public static FileSwap CreateTex( Func< Utf8GamePath, FullPath > redirections, char prefix, SetId idFrom, SetId idTo, ref MtrlFile.Texture texture, ref bool dataWasChanged )
    {
        var path        = texture.Path;
        var addedDashes = false;
        if( texture.DX11 )
        {
            var fileName = Path.GetFileName( path );
            if( !fileName.StartsWith( "--" ) )
            {
                path        = path.Replace( fileName, $"--{fileName}" );
                addedDashes = true;
            }
        }

        var newPath = ItemSwap.ReplaceAnyId( path, prefix, idFrom );
        newPath = ItemSwap.AddSuffix( newPath, ".tex", $"_{Path.GetFileName( texture.Path ).GetStableHashCode():x8}" );
        if( newPath != path )
        {
            texture.Path   = addedDashes ? newPath.Replace( "--", string.Empty ) : newPath;
            dataWasChanged = true;
        }

        return FileSwap.CreateSwap( ResourceType.Tex, redirections, newPath, path, path );
    }

    public static FileSwap CreateShader( Func< Utf8GamePath, FullPath > redirections, ref string shaderName, ref bool dataWasChanged )
    {
        var path = $"shader/sm5/shpk/{shaderName}";
        return FileSwap.CreateSwap( ResourceType.Shpk, redirections, path, path );
    }

    public static FileSwap CreateAtex( Func< Utf8GamePath, FullPath > redirections, ref string filePath, ref bool dataWasChanged )
    {
        var oldPath = filePath;
        filePath       = ItemSwap.AddSuffix( filePath, ".atex", $"_{Path.GetFileName( filePath ).GetStableHashCode():x8}" );
        dataWasChanged = true;

        return FileSwap.CreateSwap( ResourceType.Atex, redirections, filePath, oldPath, oldPath );
    }
}