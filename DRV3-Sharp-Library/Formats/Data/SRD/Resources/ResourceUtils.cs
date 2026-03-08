using System;

namespace DRV3_Sharp_Library.Formats.Data.SRD.Resources;

internal static class ResourceUtils
{
    // Taken from TGE's GFD Studio
    public static int Morton(int t, int sx, int sy)
    {
        int num1;
        int num2 = num1 = 1;
        int num3 = t;
        int num4 = sx;
        int num5 = sy;
        int num6 = 0;
        int num7 = 0;

        while (num4 > 1 || num5 > 1)
        {
            if (num4 > 1)
            {
                num6 += num2 * (num3 & 1);
                num3 >>= 1;
                num2 *= 2;
                num4 >>= 1;
            }
            if (num5 > 1)
            {
                num7 += num1 * (num3 & 1);
                num3 >>= 1;
                num1 *= 2;
                num5 >>= 1;
            }
        }

        return num7 * sx + num6;
    }

    public static Span<byte> PS4Swizzle(ReadOnlySpan<byte> data, int width, int height, int blockSize)
    {
        return DoSwizzle(data, width, height, blockSize, false);
    }

    public static Span<byte> PS4UnSwizzle(ReadOnlySpan<byte> data, int width, int height, int blockSize)
    {
        return DoSwizzle(data, width, height, blockSize, true);
    }

    private static Span<byte> DoSwizzle(ReadOnlySpan<byte> data, int width, int height, int blockSize, bool unswizzle)
    {
        // This corrects the dimensions in the case of textures whose size isn't a power of two
        // (or more precisely, an even multiple of 4).
        width = Utils.NearestMultipleOf(width, 4);
        height = Utils.NearestMultipleOf(height, 4);

        var processed = new Span<byte>(new byte[data.Length]);
        var heightTexels = height / 4;
        var heightTexelsAligned = (heightTexels + 7) / 8;
        int widthTexels = width / 4;
        var widthTexelsAligned = (widthTexels + 7) / 8;
        var dataIndex = 0;

        for (int y = 0; y < heightTexelsAligned; ++y)
        {
            for (int x = 0; x < widthTexelsAligned; ++x)
            {
                for (int t = 0; t < 64; ++t)
                {
                    int pixelIndex = Morton(t, 8, 8);
                    int num8 = pixelIndex / 8;
                    int num9 = pixelIndex % 8;
                    var yOffset = (y * 8) + num8;
                    var xOffset = (x * 8) + num9;

                    if (xOffset < widthTexels && yOffset < heightTexels)
                    {
                        var destPixelIndex = yOffset * widthTexels + xOffset;
                        int destIndex = blockSize * destPixelIndex;

                        if (unswizzle)
                        {
                            // Swizzled -> Linear
                            data[dataIndex..(dataIndex + blockSize)].CopyTo(processed[destIndex..(destIndex + blockSize)]);
                        }
                        else
                        {
                            // Linear -> Swizzled
                            data[destIndex..(destIndex + blockSize)].CopyTo(processed[dataIndex..(dataIndex + blockSize)]);
                        }
                    }

                    dataIndex += blockSize;
                }
            }
        }

        return processed;
    }

    // --- Dynamic Morton Swizzling (from swizzle.py) ---
    private static uint Compact1By1(uint x)
    {
        x &= 0x55555555;
        x = (x ^ (x >> 1)) & 0x33333333;
        x = (x ^ (x >> 2)) & 0x0f0f0f0f;
        x = (x ^ (x >> 4)) & 0x00ff00ff;
        x = (x ^ (x >> 8)) & 0x0000ffff;
        return x;
    }

    private static uint DecodeMorton2X(uint code) => Compact1By1(code >> 0);
    private static uint DecodeMorton2Y(uint code) => Compact1By1(code >> 1);

    public static Span<byte> PostProcessMortonUnswizzle(ReadOnlySpan<byte> data, int width, int height, int bytespp)
    {
        return DoMortonSwizzle(data, width, height, bytespp, true);
    }

    public static Span<byte> PostProcessMortonSwizzle(ReadOnlySpan<byte> data, int width, int height, int bytespp)
    {
        return DoMortonSwizzle(data, width, height, bytespp, false);
    }

    private static Span<byte> DoMortonSwizzle(ReadOnlySpan<byte> data, int width, int height, int bytespp, bool unswizzle)
    {
        if (width <= 0 || height <= 0) return data.ToArray();

        var processed = new Span<byte>(new byte[data.Length]);
        int min = Math.Min(width, height);
        int k = (int)Math.Log2(min);
        uint mask = (uint)(min - 1);

        for (uint i = 0; i < (uint)(width * height); i++)
        {
            int x, y;
            uint x_bits = DecodeMorton2X(i) & mask;
            uint y_bits = DecodeMorton2Y(i) & mask;
            uint block_idx = i >> (2 * k);

            if (height < width)
            {
                // Tiled in vertical strips
                x = (int)((block_idx * min) + x_bits);
                y = (int)y_bits;
            }
            else
            {
                // Tiled in horizontal strips (standard for 1024x2048)
                x = (int)x_bits;
                y = (int)((block_idx * min) + y_bits);
            }

            int p = ((y * width) + x) * bytespp;
            int srcIdx = (int)i * bytespp;

            if (srcIdx + bytespp > data.Length || p + bytespp > processed.Length) continue;

            if (unswizzle)
            {
                // Swizzled (i) -> Linear (p)
                data[srcIdx..(srcIdx + bytespp)].CopyTo(processed[p..(p + bytespp)]);
            }
            else
            {
                // Linear (p) -> Swizzled (i)
                data[p..(p + bytespp)].CopyTo(processed[srcIdx..(srcIdx + bytespp)]);
            }
        }
        return processed;
    }
}
