using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using DRV3_Sharp_Library.Formats.Data.SRD.Blocks;
using Scarlet.Drawing;
using Scarlet.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tga;
using SixLabors.ImageSharp.PixelFormats;
using ScarletImage = Scarlet.Drawing.ImageBinary;   // Alias the Scarlet library's ImageBinary class
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using BCnEncoder.ImageSharp;

namespace DRV3_Sharp_Library.Formats.Data.SRD.Resources;

public static class ResourceSerializer
{
    public static UnknownResource DeserializeUnknown(ISrdBlock block)
    {
        return new UnknownResource(block);
    }
    public static ISrdBlock SerializeUnknown(UnknownResource unknown)
    {
        return unknown.UnderlyingBlock;
    }

    public static MaterialResource DeserializeMaterial(MatBlock mat)
    {
        // The RSI sub-block is critical because it contains the material name and property data.
        if (mat.SubBlocks[0] is not RsiBlock rsi)
            throw new InvalidDataException("A MAT block within the SRD file did not have its expected RSI sub-block.");

        string name = rsi.ResourceStrings[0];
        var materialProperties = rsi.LocalResources;
        var shaderReferences = rsi.ResourceStrings.GetRange(1, rsi.ResourceStrings.Count - 1);

        return new MaterialResource(name, shaderReferences, mat.MapTexturePairs, materialProperties, mat);
    }

    public static MeshResource DeserializeMesh(MshBlock msh)
    {
        // The RSI sub-block is critical because it contains the mesh name and other info.
        if (msh.SubBlocks[0] is not RsiBlock rsi)
            throw new InvalidDataException("A MSH block within the SRD file did not have its expected RSI sub-block.");

        string meshName = rsi.ResourceStrings[0];
        return new MeshResource(meshName, msh.LinkedVertexName, msh.LinkedMaterialName, msh.Strings, msh.MappedNodes, msh);
    }

    public static SceneResource DeserializeScene(ScnBlock scn)
    {
        // The RSI sub-block is critical because it contains the scene name and other data.
        if (scn.SubBlocks[0] is not RsiBlock rsi)
            throw new InvalidDataException("A SCN block within the SRD file did not have its expected RSI sub-block.");

        string name = rsi.ResourceStrings[0];

        return new SceneResource(name, scn.LinkedTreeNames, scn.UnknownStrings, scn);
    }

    public static TreeResource DeserializeTree(TreBlock tre)
    {
        // The RSI sub-block is critical because it contains the tree name and other strings.
        if (tre.SubBlocks[0] is not RsiBlock rsi)
            throw new InvalidDataException("A TRE block within the SRD file did not have its expected RSI sub-block.");

        string name = rsi.ResourceStrings[0];

        return new TreeResource(name, tre.RootNode, tre.UnknownMatrix, rsi.LocalResources, tre);
    }

    public static TextureInstanceResource DeserializeTextureInstance(TxiBlock txi)
    {
        // The RSI sub-block is critical because it contains the material name.
        if (txi.SubBlocks[0] is not RsiBlock rsi)
            throw new InvalidDataException("A TXI block within the SRD file did not have its expected RSI sub-block.");

        string linkedMaterialName = rsi.ResourceStrings[0];

        return new TextureInstanceResource(txi.LinkedTextureName, linkedMaterialName, txi);
    }

    public static TextureResource DeserializeTexture(TxrBlock txr)
    {
        // The RSI sub-block is critical because it contains the raw image data.
        if (txr.SubBlocks[0] is not RsiBlock rsi)
            throw new InvalidDataException("A TXR block within the SRD file did not have its expected RSI sub-block.");

        string outputName = rsi.ResourceStrings[0];
        Console.WriteLine($"Found texture resource {outputName}");

        string textureExtension = outputName.Split('.').Last().ToLowerInvariant();
        Configuration config = textureExtension switch
        {
            "bmp" => new(new BmpConfigurationModule()),
            "tga" => new(new TgaConfigurationModule()),
            "png" => new(new PngConfigurationModule()),
            _ => new(new BmpConfigurationModule())
        };

        // Separate the palette data from the list beforehand if it exists
        byte[]? paletteData = null;
        if (txr.Palette == 1)
        {
            var paletteInfo = rsi.ExternalResources[txr.PaletteID];
            paletteData = paletteInfo.Data;
        }

        // Read image data/mipmaps
        List<Image<Rgba32>> outputImages = new();
        int mipmapStartOffset = (txr.Palette == 1) ? 1 : 0;
        for (var m = mipmapStartOffset; m < rsi.ExternalResources.Count; ++m)
        {
            var imageResourceInfo = rsi.ExternalResources[m];
            var imageRawData = imageResourceInfo.Data;

            int sourceWidth = txr.Width;
            int sourceHeight = txr.Height;
            if (rsi.Unknown02 == 8)
            {
                sourceWidth = Utils.PowerOfTwo(sourceWidth);
                sourceHeight = Utils.PowerOfTwo(sourceHeight);
            }

            int mipIdx = m - mipmapStartOffset;
            int mipWidth = Math.Max(1, sourceWidth >> mipIdx);
            int mipHeight = Math.Max(1, sourceHeight >> mipIdx);

            // Determine the source pixel format.
            var pixelFormat = txr.Format switch
            {
                TextureFormat.ARGB8888 => PixelDataFormat.FormatArgb8888,
                TextureFormat.BGR565 => PixelDataFormat.FormatBgr565,
                TextureFormat.BGRA4444 => PixelDataFormat.FormatBgra4444,
                TextureFormat.DXT1RGB => PixelDataFormat.FormatDXT1Rgb,
                TextureFormat.DXT5 => PixelDataFormat.FormatDXT5,
                TextureFormat.BC5 => PixelDataFormat.FormatRGTC2,
                TextureFormat.BC4 => PixelDataFormat.FormatRGTC1,
                TextureFormat.Indexed8 => PixelDataFormat.FormatIndexed8,
                TextureFormat.BPTC => PixelDataFormat.FormatBPTC,
                _ => PixelDataFormat.Undefined
            };

            // Unswizzle if necessary (Even flags use dynamic Morton swizzling on PC)
            if ((txr.Swizzle & 1) == 0)
            {
                int blockSize = pixelFormat switch
                {
                    PixelDataFormat.FormatDXT1Rgb => 8,
                    PixelDataFormat.FormatRGTC1 => 8,
                    PixelDataFormat.FormatArgb8888 => 64,
                    _ => 16
                };

                if (pixelFormat != PixelDataFormat.FormatArgb8888)
                {
                    // For BC formats, Morton swizzle operates on 4x4 blocks
                    if (mipWidth >= 4 && mipHeight >= 4)
                    {
                        imageRawData = ResourceUtils.PostProcessMortonUnswizzle(imageRawData, mipWidth / 4, mipHeight / 4, blockSize).ToArray();
                    }
                }
                else
                {
                    // ARGB8888 uses per-pixel bytes
                    imageRawData = ResourceUtils.PostProcessMortonUnswizzle(imageRawData, mipWidth, mipHeight, 4).ToArray();
                }
            }

            ScarletImage scarletImageBinary = new(mipWidth, mipHeight, pixelFormat, imageRawData);
            byte[] decompressedData = scarletImageBinary.GetOutputPixelData(0);
            var convertedPixelData = new ReadOnlySpan<byte>(decompressedData);

            int currentDisplayWidth = Math.Max(1, txr.Width >> mipIdx);
            int currentDisplayHeight = Math.Max(1, txr.Height >> mipIdx);

            Image<Rgba32> currentMipmap = new(config, currentDisplayWidth, currentDisplayHeight);
            for (var y = 0; y < currentDisplayHeight; ++y)
            {
                for (var x = 0; x < currentDisplayWidth; ++x)
                {
                    Rgba32 pixelColor = new();
                    if (pixelFormat == PixelDataFormat.FormatIndexed8)
                    {
                        if (paletteData is null)
                            throw new NullReferenceException("Texture was indicated as using a palette, but the palette data was null.");

                        int pixelDataOffset = (y * mipWidth) + x;
                        if (pixelDataOffset < convertedPixelData.Length)
                        {
                            int paletteIndex = convertedPixelData[pixelDataOffset];
                            int paletteDataOffset = paletteIndex * 4;
                            if (paletteDataOffset + 3 < paletteData.Length)
                            {
                                pixelColor.B = paletteData[paletteDataOffset + 0];
                                pixelColor.G = paletteData[paletteDataOffset + 1];
                                pixelColor.R = paletteData[paletteDataOffset + 2];
                                pixelColor.A = paletteData[paletteDataOffset + 3];
                            }
                        }
                    }
                    else
                    {
                        int byteOffset = ((y * mipWidth) + x) * 4;
                        if (byteOffset + 3 < convertedPixelData.Length)
                        {
                            pixelColor.B = convertedPixelData[byteOffset + 0];
                            pixelColor.G = convertedPixelData[byteOffset + 1];
                            pixelColor.R = convertedPixelData[byteOffset + 2];
                            pixelColor.A = convertedPixelData[byteOffset + 3];
                        }

                        if (pixelFormat == PixelDataFormat.FormatRGTC2) { pixelColor.B = 255; pixelColor.A = 255; }
                        else if (pixelFormat == PixelDataFormat.FormatRGTC1) { pixelColor.G = pixelColor.R; pixelColor.B = pixelColor.R; }
                    }
                    currentMipmap[x, y] = pixelColor;
                }
            }
            outputImages.Add(currentMipmap);
        }

        return new TextureResource(outputName, outputImages, txr.Format, txr.Unknown00, txr.Unknown0D, txr.Swizzle, rsi.Unknown00, rsi.Unknown01, rsi.Unknown02, rsi.Unknown06, txr);
    }

    public static TextureResource ImportTexture(string name, Stream inputStream)
    {
        var image = Image.Load<Rgba32>(inputStream);
        return new TextureResource(name, new List<Image<Rgba32>> { image });
    }

    public static TxrBlock SerializeTexture(TextureResource texture)
    {
        List<byte[]> convertedPixelBytes = new();
        CompressionFormat compressionFormat = texture.Format switch
        {
            TextureFormat.DXT1RGB => CompressionFormat.Bc1,
            TextureFormat.DXT5 => CompressionFormat.Bc3,
            TextureFormat.BC4 => CompressionFormat.Bc4,
            TextureFormat.BC5 => CompressionFormat.Bc5,
            TextureFormat.BPTC => CompressionFormat.Bc7,
            _ => CompressionFormat.Unknown
        };

        foreach (var mipmap in texture.ImageMipmaps)
        {
            if (compressionFormat != CompressionFormat.Unknown)
            {
                BcEncoder encoder = new();
                encoder.OutputOptions.Format = compressionFormat;
                encoder.OutputOptions.Quality = CompressionQuality.Balanced;

                using MemoryStream ms = new();
                encoder.EncodeToStream(mipmap, ms);
                convertedPixelBytes.Add(ms.ToArray());
            }
            else
            {
                var numBytes = 4 * mipmap.Height * mipmap.Width;
                var pixelData = new byte[numBytes];
                for (var y = 0; y < mipmap.Height; ++y)
                {
                    for (var x = 0; x < mipmap.Width; ++x)
                    {
                        int byteOffset = 4 * ((y * mipmap.Width) + x);
                        var pixel = mipmap[x, y];
                        pixelData[byteOffset + 0] = pixel.B;
                        pixelData[byteOffset + 1] = pixel.G;
                        pixelData[byteOffset + 2] = pixel.R;
                        pixelData[byteOffset + 3] = pixel.A;
                    }
                }

                ScarletImage scarletImageBinary = new(mipmap.Width, mipmap.Height, PixelDataFormat.FormatArgb8888,
                    Endian.LittleEndian, PixelDataFormat.FormatArgb8888, Endian.LittleEndian, pixelData);

                convertedPixelBytes.Add(scarletImageBinary.GetOutputPixelData(0));
            }

            // Apply swizzling if needed (Even flags use dynamic Morton swizzling on PC)
            if ((texture.Swizzle & 1) == 0)
            {
                var currentData = convertedPixelBytes.Last();
                int blockSize = texture.Format switch
                {
                    TextureFormat.DXT1RGB => 8,
                    TextureFormat.BC4 => 8,
                    TextureFormat.ARGB8888 => 64,
                    _ => 16
                };

                if (texture.Format != TextureFormat.ARGB8888)
                {
                    if (mipmap.Width >= 4 && mipmap.Height >= 4)
                    {
                        convertedPixelBytes[convertedPixelBytes.Count - 1] = ResourceUtils.PostProcessMortonSwizzle(currentData, mipmap.Width / 4, mipmap.Height / 4, blockSize).ToArray();
                    }
                }
                else
                {
                    convertedPixelBytes[convertedPixelBytes.Count - 1] = ResourceUtils.PostProcessMortonSwizzle(currentData, mipmap.Width, mipmap.Height, 4).ToArray();
                }
            }
        }

        var mipmapResources = convertedPixelBytes.Select(mipmapData => new ExternalResource(ResourceDataLocation.Srdv, mipmapData, 0, -1)).ToList();
        RsiBlock rsi = new(texture.RsiUnknown00, texture.RsiUnknown01, texture.RsiUnknown02, texture.RsiUnknown06, new(), mipmapResources, new() { texture.Name }, new(), new(), Array.Empty<byte>());
        
        // Scanline calculation based on observed PC version patterns
        ushort scanline = (ushort)texture.ImageMipmaps[0].Width;
        if (texture.Format == TextureFormat.DXT1RGB || texture.Format == TextureFormat.BC4) 
            scanline *= 2;
        else 
            scanline *= 4; // DXT5, BC5, BPTC, and ARGB8888 all use Width * 4 in the observed PC SRD files

        TxrBlock txr = new(texture.TxrUnknown00, texture.TxrUnknown0D, texture.Swizzle, (ushort)texture.ImageMipmaps[0].Width, (ushort)texture.ImageMipmaps[0].Height,
            scanline, texture.Format, 0, 0, new() { rsi }, Array.Empty<byte>());

        return txr;
    }

    public static VertexResource DeserializeVertex(VtxBlock vtx)
    {
        if (vtx.SubBlocks[0] is not RsiBlock rsi)
            throw new InvalidDataException("A VTX block within the SRD file did not have its expected RSI sub-block.");

        string name = rsi.ResourceStrings[0];
        Console.WriteLine($"Found vertex resource {name}");

        using MemoryStream geometryStream = new(rsi.ExternalResources[0].Data);
        using BinaryReader geometryReader = new(geometryStream);

        List<Vector3> vertices = new();
        List<Vector3> normals = new();
        List<Vector2> textureCoords = new();
        List<float> weights = new();
        for (var sectionNum = 0; sectionNum < vtx.VertexSectionInfo.Count; ++sectionNum)
        {
            var sectionInfo = vtx.VertexSectionInfo[sectionNum];
            geometryStream.Seek(sectionInfo.Start, SeekOrigin.Begin);

            for (var vertexNum = 0; vertexNum < vtx.VertexCount; ++vertexNum)
            {
                var thisVertexDataStart = geometryStream.Position;
                switch (sectionNum)
                {
                    case 0:
                        vertices.Add(new Vector3 { X = geometryReader.ReadSingle() * -1.0f, Y = geometryReader.ReadSingle(), Z = geometryReader.ReadSingle() });
                        normals.Add(new Vector3 { X = geometryReader.ReadSingle() * -1.0f, Y = geometryReader.ReadSingle(), Z = geometryReader.ReadSingle() });
                        if (vtx.VertexSectionInfo.Count == 1) textureCoords.Add(new Vector2 { X = geometryReader.ReadSingle(), Y = geometryReader.ReadSingle() });
                        break;
                    case 1:
                        var weightsPerVertex = (sectionInfo.DataSizePerVertex / sizeof(float));
                        for (var weightNum = 0; weightNum < weightsPerVertex; ++weightNum) weights.Add(geometryReader.ReadSingle());
                        break;
                    case 2:
                        textureCoords.Add(new Vector2 { X = geometryReader.ReadSingle(), Y = geometryReader.ReadSingle() });
                        break;
                }
                var remainingBytes = sectionInfo.DataSizePerVertex - (geometryStream.Position - thisVertexDataStart);
                geometryStream.Seek(remainingBytes, SeekOrigin.Current);
            }
        }

        using MemoryStream indexStream = new(rsi.ExternalResources[1].Data);
        using BinaryReader indexReader = new(indexStream);
        List<Tuple<ushort, ushort, ushort>> indices = new();
        while (indexStream.Position < indexStream.Length)
        {
            ushort index3 = indexReader.ReadUInt16();
            ushort index2 = indexReader.ReadUInt16();
            ushort index1 = indexReader.ReadUInt16();
            indices.Add(new Tuple<ushort, ushort, ushort>(index1, index2, index3));
        }

        return new VertexResource(name, vertices, normals, textureCoords, indices, vtx.BoneList, weights, vtx);
    }
}
