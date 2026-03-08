using System;
using System.Collections.Generic;
using System.Numerics;
using DRV3_Sharp_Library.Formats.Data.SRD.Blocks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DRV3_Sharp_Library.Formats.Data.SRD.Resources;

public sealed record UnknownResource(
        ISrdBlock UnderlyingBlock)
    : ISrdResource;

public sealed record MaterialResource(
        string Name,
        List<string> ShaderReferences,
        List<(string MapName, string TextureName)> MapTexturePairs,
        List<LocalResource> MaterialProperties,
        ISrdBlock UnderlyingBlock)
    : ISrdResource;

public sealed record MeshResource(
        string Name, string LinkedVertexName, string LinkedMaterialName,
        List<string> UnknownStrings,
        Dictionary<string, List<string>> MappedNodes,
        ISrdBlock UnderlyingBlock)
    : ISrdResource;

public sealed record SceneResource(
        string Name,
        List<string> LinkedTreeNames,
        List<string> UnknownStrings,
        ISrdBlock UnderlyingBlock)
    : ISrdResource;

public sealed record TextureInstanceResource(
        string LinkedTextureName, string LinkedMaterialName,
        ISrdBlock UnderlyingBlock)
    : ISrdResource;

public sealed record TextureResource(
        string Name,
        List<Image<Rgba32>> ImageMipmaps,
        TextureFormat Format = TextureFormat.ARGB8888,
        int TxrUnknown00 = 1,
        byte TxrUnknown0D = 1,
        ushort Swizzle = 1,

        byte RsiUnknown00 = 6,
        byte RsiUnknown01 = 5,
        sbyte RsiUnknown02 = 4,
        short RsiUnknown06 = -1,
        ISrdBlock? UnderlyingBlock = null)
    : ISrdResource;

public sealed record TreeResource(
        string Name,
        Node RootNode,
        Matrix4x4 UnknownMatrix,
        List<LocalResource> ExtraProperties,
        ISrdBlock UnderlyingBlock)
    : ISrdResource;

public sealed record VertexResource(
        string Name,
        List<Vector3> Vertices,
        List<Vector3> Normals,
        List<Vector2> TextureCoords,
        List<Tuple<ushort, ushort, ushort>> Indices,
        List<string> Bones,
        List<float> Weights,
        ISrdBlock UnderlyingBlock)
    : ISrdResource;

public interface ISrdResource
{
    public ISrdBlock? UnderlyingBlock { get; }
}