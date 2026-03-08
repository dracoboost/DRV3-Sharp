using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace DRV3_Sharp_Library.Formats.Data.SRD.Blocks;

internal static class BlockSerializer
{
    public static byte[] GetTerminatorBlockBytes()
    {
        // A $CT0 block is a 16-byte header with magic "$CT0" and 0s for lengths/unk
        byte[] terminator = new byte[16];
        Encoding.ASCII.GetBytes("$CT0").CopyTo(terminator, 0);
        return terminator;
    }

    public static UnknownBlock DeserializeUnknownBlock(string blockType, MemoryStream mainStream)
    {
        return new UnknownBlock(blockType, mainStream.ToArray(), new());
    }

    public static byte[] SerializeUnknownBlock(UnknownBlock block)
    {
        return block.MainData;
    }

    public static MatBlock DeserializeMatBlock(MemoryStream mainStream)
    {
        byte[] rawData = mainStream.ToArray();
        using BinaryReader reader = new(mainStream);
        uint unk00 = reader.ReadUInt32();
        float unk04 = reader.ReadSingle();
        float unk08 = reader.ReadSingle();
        float unk0C = reader.ReadSingle();
        ushort unk10 = reader.ReadUInt16();
        ushort unk12 = reader.ReadUInt16();
        ushort strMapStart = reader.ReadUInt16();
        ushort strMapCount = reader.ReadUInt16();
        List<(string, string)> pairs = new();
        mainStream.Seek(strMapStart, SeekOrigin.Begin);
        for (var m = 0; m < strMapCount; ++m)
        {
            ushort texOff = reader.ReadUInt16();
            ushort mapOff = reader.ReadUInt16();
            long ret = mainStream.Position;
            mainStream.Seek(texOff, SeekOrigin.Begin);
            string tex = Utils.ReadNullTerminatedString(reader, Encoding.ASCII);
            mainStream.Seek(mapOff, SeekOrigin.Begin);
            string map = Utils.ReadNullTerminatedString(reader, Encoding.ASCII);
            pairs.Add((map, tex));
            mainStream.Seek(ret, SeekOrigin.Begin);
        }
        return new MatBlock(unk00, unk04, unk08, unk0C, unk10, unk12, pairs, new(), rawData);
    }

    public static MshBlock DeserializeMshBlock(MemoryStream mainStream)
    {
        byte[] rawData = mainStream.ToArray();
        using BinaryReader reader = new(mainStream);
        uint unk00 = reader.ReadUInt32();
        ushort vPtr = reader.ReadUInt16();
        ushort mPtr = reader.ReadUInt16();
        ushort fPtr = reader.ReadUInt16();
        ushort sPtr = reader.ReadUInt16();
        ushort nPtr = reader.ReadUInt16();
        ushort dPtr = reader.ReadUInt16();
        byte sCount = reader.ReadByte();
        byte tCount = reader.ReadByte();
        byte cCount = reader.ReadByte();
        byte unk13 = reader.ReadByte();
        List<string> strings = new();
        for (var i = 0; i < sCount; ++i) { mainStream.Seek(sPtr + (2 * i), SeekOrigin.Begin); ushort off = reader.ReadUInt16(); mainStream.Seek(off, SeekOrigin.Begin); strings.Add(Utils.ReadNullTerminatedString(reader, Encoding.ASCII)); }
        Dictionary<string, List<string>> nodes = new();
        for (var i = 0; i < tCount; ++i) { mainStream.Seek(nPtr + (2 * i), SeekOrigin.Begin); ushort off = reader.ReadUInt16(); mainStream.Seek(off, SeekOrigin.Begin); nodes[Utils.ReadNullTerminatedString(reader, Encoding.ASCII)] = new(); }
        mainStream.Seek(dPtr, SeekOrigin.Begin);
        for (var i = 0; i < cCount; ++i) { ushort vP = reader.ReadUInt16(); ushort kP = reader.ReadUInt16(); var old = mainStream.Position; mainStream.Seek(vP, SeekOrigin.Begin); string v = Utils.ReadNullTerminatedString(reader, Encoding.ASCII); mainStream.Seek(kP, SeekOrigin.Begin); string k = Utils.ReadNullTerminatedString(reader, Encoding.ASCII); nodes[k]?.Add(v); mainStream.Seek(old, SeekOrigin.Begin); }
        mainStream.Seek(vPtr, SeekOrigin.Begin); string vN = Utils.ReadNullTerminatedString(reader, Encoding.ASCII);
        mainStream.Seek(mPtr, SeekOrigin.Begin); string mN = Utils.ReadNullTerminatedString(reader, Encoding.ASCII);
        mainStream.Seek(fPtr, SeekOrigin.Begin); string f = Utils.ReadNullTerminatedString(reader, Encoding.ASCII);
        return new MshBlock(unk00, unk13, f, vN, mN, strings, nodes, new(), rawData);
    }

    public static RsfBlock DeserializeRsfBlock(MemoryStream mainStream)
    {
        byte[] rawData = mainStream.ToArray();
        using BinaryReader reader = new(mainStream);
        return new RsfBlock(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), Utils.ReadNullTerminatedString(reader, Encoding.ASCII), new(), rawData);
    }

    public static byte[] SerializeRsfBlock(RsfBlock block) => block.MainData;

    public static RsiBlock DeserializeRsiBlock(MemoryStream mainStream, Stream? srdi, Stream? srdv)
    {
        byte[] rawData = mainStream.ToArray();
        using BinaryReader reader = new(mainStream);
        byte u00 = reader.ReadByte(); byte u01 = reader.ReadByte(); sbyte u02 = reader.ReadSByte();
        byte eCount = reader.ReadByte(); short lCount = reader.ReadInt16(); short u06 = reader.ReadInt16();
        short lOff = reader.ReadInt16(); short uIntOff = reader.ReadInt16(); int strOff = reader.ReadInt32();
        List<ExternalResource> externalResources = new();
        for (var i = 0; i < eCount; i++)
        {
            int ptr = reader.ReadInt32(); int len = reader.ReadInt32(); int unk1 = reader.ReadInt32(); int unk2 = reader.ReadInt32();
            var resLoc = (ResourceDataLocation)(ptr & 0xF0000000); int addr = ptr & 0x0FFFFFFF;
            Stream s = resLoc == ResourceDataLocation.Srdi ? srdi! : srdv!;
            long old = s.Position; s.Seek(addr, SeekOrigin.Begin);
            externalResources.Add(new ExternalResource(resLoc, new BinaryReader(s, Encoding.ASCII, true).ReadBytes(len), unk1, unk2));
            s.Position = old;
        }
        List<LocalResource> localResources = new();
        for (int i = 0; i < lCount; i++)
        {
            mainStream.Seek(lOff + (i * 16), SeekOrigin.Begin);
            int nP = reader.ReadInt32(); int dP = reader.ReadInt32(); int len = reader.ReadInt32(); int unk = reader.ReadInt32();
            mainStream.Seek(nP, SeekOrigin.Begin); string name = Utils.ReadNullTerminatedString(reader, Encoding.ASCII);
            mainStream.Seek(dP, SeekOrigin.Begin); localResources.Add(new LocalResource(name, reader.ReadBytes(len), unk));
        }
        List<int> uInts = new();
        if (uIntOff > 0) { mainStream.Seek(uIntOff, SeekOrigin.Begin); while (mainStream.Position < strOff) uInts.Add(reader.ReadInt32()); }
        mainStream.Seek(strOff, SeekOrigin.Begin); List<string> strs = new();
        while (mainStream.Position < mainStream.Length) strs.Add(Utils.ReadNullTerminatedString(reader, Encoding.GetEncoding("shift-jis")));
        return new RsiBlock(u00, u01, u02, u06, localResources, externalResources, strs, uInts, new(), rawData);
    }

    public static byte[] SerializeRsiBlock(RsiBlock rsi, Stream outputSrdi, Stream outputSrdv)
    {
        using MemoryStream mainMem = new();
        using BinaryWriter mainWriter = new(mainMem, Encoding.ASCII);
        List<(int ptr, int len)> extResInfo = new();
        foreach (var ext in rsi.ExternalResources)
        {
            Stream target = ext.Location == ResourceDataLocation.Srdi ? outputSrdi : outputSrdv;
            Utils.PadToNearest(new BinaryWriter(target, Encoding.ASCII, true), 16);
            int addr = (int)target.Position | (int)ext.Location;
            target.Write(ext.Data);
            extResInfo.Add((addr, ext.Data.Length));
        }
        using MemoryStream locData = new(); using MemoryStream locName = new();
        using BinaryWriter locDW = new(locData); using BinaryWriter locNW = new(locName);
        List<(int nP, int dP, int len)> locResInfo = new();
        foreach (var loc in rsi.LocalResources)
        {
            locResInfo.Add(((int)locNW.BaseStream.Position, (int)locDW.BaseStream.Position, loc.Data.Length));
            locNW.Write(Encoding.ASCII.GetBytes(loc.Name)); locNW.Write((byte)0);
            locDW.Write(loc.Data);
        }
        mainWriter.Write(rsi.UnknownIntList.Count > 0 ? (byte)4 : (byte)6);
        mainWriter.Write(rsi.Unknown01); mainWriter.Write(rsi.Unknown02);
        mainWriter.Write((byte)rsi.ExternalResources.Count); mainWriter.Write((short)rsi.LocalResources.Count);
        mainWriter.Write(rsi.Unknown06);
        mainWriter.Write((short)0); // placeholder for lOff
        mainWriter.Write((short)0); // placeholder for uIntOff
        mainWriter.Write((int)0); // placeholder for strOff
        for (int i = 0; i < rsi.ExternalResources.Count; i++) { mainWriter.Write(extResInfo[i].ptr); mainWriter.Write(extResInfo[i].len); mainWriter.Write(rsi.ExternalResources[i].UnknownValue1); mainWriter.Write(rsi.ExternalResources[i].UnknownValue2); }
        int lOff = 16 + (rsi.ExternalResources.Count * 16);
        int uIntOff = lOff + (rsi.LocalResources.Count * 16);
        int dOff = uIntOff + (rsi.UnknownIntList.Count * 4);
        int nOff = dOff + (int)locData.Length;
        int sOff = nOff + (int)locName.Length;
        
        long endPos = mainWriter.BaseStream.Position;
        mainWriter.BaseStream.Seek(8, SeekOrigin.Begin); 
        mainWriter.Write((short)lOff); 
        mainWriter.Write(rsi.UnknownIntList.Count > 0 ? (short)uIntOff : (short)0); 
        mainWriter.Write(sOff);
        mainWriter.BaseStream.Seek(endPos, SeekOrigin.Begin);

        for (int i = 0; i < rsi.LocalResources.Count; i++) { mainWriter.Write(locResInfo[i].nP + nOff); mainWriter.Write(locResInfo[i].dP + dOff); mainWriter.Write(locResInfo[i].len); mainWriter.Write(rsi.LocalResources[i].UnknownValue); }
        foreach (int i in rsi.UnknownIntList) mainWriter.Write(i);
        mainWriter.Write(locData.ToArray()); mainWriter.Write(locName.ToArray());
        foreach (string str in rsi.ResourceStrings) { mainWriter.Write(Encoding.GetEncoding("shift-jis").GetBytes(str)); mainWriter.Write((byte)0); }
        return mainMem.ToArray();
    }

    public static ScnBlock DeserializeScnBlock(MemoryStream mainStream)
    {
        byte[] rawData = mainStream.ToArray();
        using BinaryReader reader = new(mainStream);
        uint u00 = reader.ReadUInt32(); ushort rOff = reader.ReadUInt16(); ushort rCount = reader.ReadUInt16(); ushort sOff = reader.ReadUInt16(); ushort sCount = reader.ReadUInt16();
        List<string> rNames = new(); mainStream.Seek(rOff, SeekOrigin.Begin);
        for (int i = 0; i < rCount; i++) { ushort o = reader.ReadUInt16(); long p = mainStream.Position; mainStream.Seek(o, SeekOrigin.Begin); rNames.Add(Utils.ReadNullTerminatedString(reader, Encoding.ASCII)); mainStream.Seek(p, SeekOrigin.Begin); }
        List<string> uStrs = new(); mainStream.Seek(sOff, SeekOrigin.Begin);
        for (int i = 0; i < sCount; i++) { ushort o = reader.ReadUInt16(); long p = mainStream.Position; mainStream.Seek(o, SeekOrigin.Begin); uStrs.Add(Utils.ReadNullTerminatedString(reader, Encoding.ASCII)); mainStream.Seek(p, SeekOrigin.Begin); }
        return new ScnBlock(u00, rNames, uStrs, new(), rawData);
    }

    public static TreBlock DeserializeTreBlock(MemoryStream mainStream)
    {
        byte[] rawData = mainStream.ToArray();
        using BinaryReader reader = new(mainStream);
        uint mD = reader.ReadUInt32(); ushort u04 = reader.ReadUInt16(); ushort bC = reader.ReadUInt16(); ushort u08 = reader.ReadUInt16(); ushort lC = reader.ReadUInt16(); uint mP = reader.ReadUInt32();
        Node root = null!;
        for (int i = 0; i < bC; i++)
        {
            uint nP = reader.ReadUInt32(); uint lP = reader.ReadUInt32(); byte lCount = reader.ReadByte(); byte d = reader.ReadByte(); reader.ReadByte(); reader.ReadByte(); reader.ReadUInt32();
            var p = mainStream.Position; mainStream.Seek(nP, SeekOrigin.Begin); string name = Utils.ReadNullTerminatedString(reader, Encoding.ASCII); mainStream.Seek(p, SeekOrigin.Begin);
            Node node = new Node(name, new List<Node>());
            if (lP != 0) { var p2 = mainStream.Position; mainStream.Seek(lP, SeekOrigin.Begin); for (int l = 0; l < lCount; l++) { uint lN = reader.ReadUInt32(); reader.ReadUInt32(); var p3 = mainStream.Position; mainStream.Seek(lN, SeekOrigin.Begin); node.Children!.Add(new Node(Utils.ReadNullTerminatedString(reader, Encoding.ASCII), null)); mainStream.Seek(p3, SeekOrigin.Begin); } mainStream.Seek(p2, SeekOrigin.Begin); }
            if (i == 0) root = node; else { Node par = root; for (int j = 0; j < d - 1; j++) par = par.Children!.Last(n => n.Children != null); par.Children!.Add(node); }
        }
        mainStream.Seek(mP, SeekOrigin.Begin); Matrix4x4 mat = new Matrix4x4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        return new TreBlock(u04, u08, root, mat, new(), rawData);
    }

    public static TxiBlock DeserializeTxiBlock(MemoryStream mainStream)
    {
        byte[] rawData = mainStream.ToArray();
        using BinaryReader reader = new(mainStream);
        int u00 = reader.ReadInt32(); int u04 = reader.ReadInt32(); int u08 = reader.ReadInt32(); byte u0C = reader.ReadByte(); byte u0D = reader.ReadByte(); byte u0E = reader.ReadByte(); byte u0F = reader.ReadByte(); int u10 = reader.ReadInt32(); string l = Utils.ReadNullTerminatedString(reader, Encoding.GetEncoding("shift-jis"));
        return new TxiBlock(u00, u04, u08, u0C, u0D, u0E, u0F, u10, l, new(), rawData);
    }

    public static TxrBlock DeserializeTxrBlock(MemoryStream mainStream)
    {
        byte[] rawData = mainStream.ToArray();
        using BinaryReader reader = new(mainStream);
        int u00 = reader.ReadInt32(); ushort s = reader.ReadUInt16(); ushort w = reader.ReadUInt16(); ushort h = reader.ReadUInt16(); ushort sl = reader.ReadUInt16(); TextureFormat f = (TextureFormat)reader.ReadByte(); byte u0D = reader.ReadByte(); byte p = reader.ReadByte(); byte pI = reader.ReadByte();
        return new TxrBlock(u00, u0D, s, w, h, sl, f, p, pI, new(), rawData);
    }

    public static byte[] SerializeTxrBlock(TxrBlock txr)
    {
        using MemoryStream mem = new();
        using BinaryWriter writer = new(mem);
        writer.Write(txr.Unknown00); writer.Write(txr.Swizzle); writer.Write(txr.Width); writer.Write(txr.Height); writer.Write(txr.Scanline); writer.Write((byte)txr.Format); writer.Write(txr.Unknown0D); writer.Write(txr.Palette); writer.Write(txr.PaletteID);
        return mem.ToArray();
    }

    public static VtxBlock DeserializeVtxBlock(MemoryStream mainStream)
    {
        byte[] rawData = mainStream.ToArray();
        using BinaryReader reader = new(mainStream);
        int vC = reader.ReadInt32(); short u04 = reader.ReadInt16(); short mT = reader.ReadInt16(); int vtC = reader.ReadInt32(); short u0C = reader.ReadInt16(); byte u0E = reader.ReadByte(); byte vSCount = reader.ReadByte();
        ushort bR = reader.ReadUInt16(); ushort vS = reader.ReadUInt16(); ushort uV = reader.ReadUInt16(); ushort bL = reader.ReadUInt16(); uint u18 = reader.ReadUInt32(); uint u1C = reader.ReadUInt32();
        List<short> uS = new(); mainStream.Seek(0x18, SeekOrigin.Begin);
        List<(uint, uint)> vSi = new(); mainStream.Seek(vS, SeekOrigin.Begin); for (int i = 0; i < vSCount; i++) vSi.Add((reader.ReadUInt32(), reader.ReadUInt32()));
        mainStream.Seek(bR, SeekOrigin.Begin); short rootB = reader.ReadInt16();
        List<string> bones = new(); if (bL != 0) { mainStream.Seek(bL, SeekOrigin.Begin); while (mainStream.Position < uV) { ushort o = reader.ReadUInt16(); if (o == 0) break; long p = mainStream.Position; mainStream.Seek(o, SeekOrigin.Begin); bones.Add(Utils.ReadNullTerminatedString(reader, Encoding.ASCII)); mainStream.Seek(p, SeekOrigin.Begin); } }
        return new VtxBlock(u04, u0C, u0E, u18, u1C, mT, vtC, vSi, rootB, bones, uS, new List<Vector3>(), new List<string>(), new(), rawData);
    }
}
