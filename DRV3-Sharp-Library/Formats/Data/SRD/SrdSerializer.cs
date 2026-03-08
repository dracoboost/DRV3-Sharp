using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DRV3_Sharp_Library.Formats.Data.SRD.Blocks;
using DRV3_Sharp_Library.Formats.Data.SRD.Resources;

namespace DRV3_Sharp_Library.Formats.Data.SRD;

public static class SrdSerializer
{
    public static void Deserialize(Stream inputSrd, Stream? inputSrdi, Stream? inputSrdv, out SrdData outputData)
    {
        List<ISrdBlock> blocks = new();
        while (inputSrd.Position < inputSrd.Length)
        {
            var block = DeserializeBlock(inputSrd, inputSrdi, inputSrdv);
            if (block is not null) blocks.Add(block);
        }
        outputData = new SrdData(DeserializeResources(blocks));
    }

    private static ISrdBlock? DeserializeBlock(Stream inputSrd, Stream? inputSrdi, Stream? inputSrdv)
    {
        using BinaryReader srdReader = new(inputSrd, Encoding.ASCII, true);
        if (srdReader.BaseStream.Position + 16 > srdReader.BaseStream.Length) return null;
        string blockType = Encoding.ASCII.GetString(srdReader.ReadBytes(4));
        int mainLen = BinaryPrimitives.ReverseEndianness(srdReader.ReadInt32());
        int subLen = BinaryPrimitives.ReverseEndianness(srdReader.ReadInt32());
        int unknown = BinaryPrimitives.ReverseEndianness(srdReader.ReadInt32());
        if (blockType == "$CT0") return null;
        MemoryStream mainDataStream = new(srdReader.ReadBytes(mainLen));
        Utils.SkipToNearest(srdReader, 16);
        MemoryStream? subDataStream = subLen > 0 ? new MemoryStream(srdReader.ReadBytes(subLen)) : null;
        if (subDataStream != null) Utils.SkipToNearest(srdReader, 16);

        ISrdBlock? outputBlock = blockType switch
        {
            "$CFH" => new CfhBlock(new(), mainDataStream.ToArray()),
            "$MAT" => BlockSerializer.DeserializeMatBlock(mainDataStream),
            "$MSH" => BlockSerializer.DeserializeMshBlock(mainDataStream),
            "$RSF" => BlockSerializer.DeserializeRsfBlock(mainDataStream),
            "$RSI" => BlockSerializer.DeserializeRsiBlock(mainDataStream, inputSrdi, inputSrdv),
            "$SCN" => BlockSerializer.DeserializeScnBlock(mainDataStream),
            "$TRE" => BlockSerializer.DeserializeTreBlock(mainDataStream),
            "$TXI" => BlockSerializer.DeserializeTxiBlock(mainDataStream),
            "$TXR" => BlockSerializer.DeserializeTxrBlock(mainDataStream),
            "$VTX" => BlockSerializer.DeserializeVtxBlock(mainDataStream),
            _ => BlockSerializer.DeserializeUnknownBlock(blockType, mainDataStream),
        };

        while (subDataStream != null && subDataStream.Position < subLen)
        {
            var subBlock = DeserializeBlock(subDataStream, inputSrdi, inputSrdv);
            if (subBlock != null) outputBlock?.SubBlocks.Add(subBlock);
        }
        subDataStream?.Dispose(); mainDataStream.Dispose();
        return outputBlock;
    }

    private static List<ISrdResource> DeserializeResources(List<ISrdBlock> inputBlocks)
    {
        List<ISrdResource> outputResources = new();
        foreach (var block in inputBlocks)
        {
            outputResources.Add(block switch
            {
                MatBlock mat => ResourceSerializer.DeserializeMaterial(mat),
                MshBlock msh => ResourceSerializer.DeserializeMesh(msh),
                ScnBlock scn => ResourceSerializer.DeserializeScene(scn),
                TreBlock tre => ResourceSerializer.DeserializeTree(tre),
                TxiBlock txi => ResourceSerializer.DeserializeTextureInstance(txi),
                TxrBlock txr => ResourceSerializer.DeserializeTexture(txr),
                VtxBlock vtx => ResourceSerializer.DeserializeVertex(vtx),
                _ => ResourceSerializer.DeserializeUnknown(block)
            });
        }
        return outputResources;
    }

    public static void Serialize(SrdData inputData, Stream outputSrd, Stream outputSrdi, Stream outputSrdv)
    {
        Console.WriteLine($"Starting SRD serialization. Resource count: {inputData.Resources.Count}");
        var blocks = SerializeResources(inputData);
        Console.WriteLine($"Serialized into {blocks.Count} blocks.");
        foreach (var block in blocks) SerializeBlock(block, outputSrd, outputSrdi, outputSrdv);
        outputSrd.Write(BlockSerializer.GetTerminatorBlockBytes());
        Utils.PadToNearest(new BinaryWriter(outputSrd, Encoding.ASCII, true), 16);
        Console.WriteLine($"Final SRD length: {outputSrd.Length} bytes.");
    }

    private static void SerializeBlock(ISrdBlock block, Stream outputSrd, Stream outputSrdi, Stream outputSrdv)
    {
        MemoryStream mainDataStream = new();
        string typeString = "";
        int unknownVal = 0;

        switch (block)
        {
            case CfhBlock cfh: typeString = "$CFH"; unknownVal = 1; mainDataStream.Write(cfh.MainData); break;
            case RsfBlock rsf: typeString = "$RSF"; mainDataStream.Write(rsf.MainData); break;
            case RsiBlock rsi: typeString = "$RSI"; mainDataStream.Write(BlockSerializer.SerializeRsiBlock(rsi, outputSrdi, outputSrdv)); break;
            case TxrBlock txr: typeString = "$TXR"; mainDataStream.Write(BlockSerializer.SerializeTxrBlock(txr)); break;
            case VtxBlock vtx: typeString = "$VTX"; mainDataStream.Write(vtx.MainData); break;
            case MshBlock msh: typeString = "$MSH"; mainDataStream.Write(msh.MainData); break;
            case ScnBlock scn: typeString = "$SCN"; mainDataStream.Write(scn.MainData); break;
            case TreBlock tre: typeString = "$TRE"; mainDataStream.Write(tre.MainData); break;
            case MatBlock mat: typeString = "$MAT"; mainDataStream.Write(mat.MainData); break;
            case TxiBlock txi: typeString = "$TXI"; mainDataStream.Write(txi.MainData); break;
            case UnknownBlock unk: typeString = unk.BlockType; mainDataStream.Write(unk.MainData); break;
        }

        MemoryStream subDataStream = new();
        foreach (var subBlock in block.SubBlocks) SerializeBlock(subBlock, subDataStream, outputSrdi, outputSrdv);
        if (block.SubBlocks.Count > 0) subDataStream.Write(BlockSerializer.GetTerminatorBlockBytes());

        if (string.IsNullOrEmpty(typeString)) return;

        using BinaryWriter srdWriter = new(outputSrd, Encoding.ASCII, true);
        srdWriter.Write(Encoding.ASCII.GetBytes(typeString));
        srdWriter.Write(BinaryPrimitives.ReverseEndianness((int)mainDataStream.Length));
        srdWriter.Write(BinaryPrimitives.ReverseEndianness((int)subDataStream.Length));
        srdWriter.Write(BinaryPrimitives.ReverseEndianness(unknownVal));
        srdWriter.Write(mainDataStream.ToArray());
        Utils.PadToNearest(srdWriter, 16);
        if (subDataStream.Length > 0) { srdWriter.Write(subDataStream.ToArray()); Utils.PadToNearest(srdWriter, 16); }
        
        Console.WriteLine($"Wrote block {typeString} (Main: {mainDataStream.Length}, Sub: {subDataStream.Length})");
        subDataStream.Dispose(); mainDataStream.Dispose();
    }

    private static List<ISrdBlock> SerializeResources(SrdData inputData)
    {
        List<ISrdBlock> outputBlocks = new();
        foreach (var resource in inputData.Resources)
        {
            if (resource is TextureResource texture && texture.UnderlyingBlock == null)
                outputBlocks.Add(ResourceSerializer.SerializeTexture(texture));
            else if (resource.UnderlyingBlock != null)
                outputBlocks.Add(resource.UnderlyingBlock);
        }
        return outputBlocks;
    }
}
