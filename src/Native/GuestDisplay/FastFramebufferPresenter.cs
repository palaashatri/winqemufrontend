using System.Buffers.Binary;
using System.IO.Compression;
using WinQemu.Core.Models;
using WinQemu.Display;

namespace WinQemu.Native.GuestDisplay;

public readonly record struct ViewportPlacement(
    double OffsetX,
    double OffsetY,
    double RenderedWidth,
    double RenderedHeight,
    double ScaleX,
    double ScaleY);

/// <summary>
/// High-performance unsafe dirty-rectangle pixel blitter, aspect-ratio viewport scaler,
/// and PNG image encoder for guest framebuffers (§30).
/// </summary>
public static unsafe class FastFramebufferPresenter
{
    public static void BlitDirtyRegions(
        ReadOnlySpan<byte> sourceBgra,
        Span<byte> destinationBgra,
        int width,
        int height,
        IReadOnlyList<DirtyRect> dirtyRects)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var totalBytes = width * height * 4;
        if (sourceBgra.Length < totalBytes || destinationBgra.Length < totalBytes)
        {
            return;
        }

        if (dirtyRects.Count == 0 ||
            (dirtyRects.Count == 1 && dirtyRects[0].X == 0 && dirtyRects[0].Y == 0 &&
             dirtyRects[0].Width == width && dirtyRects[0].Height == height))
        {
            sourceBgra[..totalBytes].CopyTo(destinationBgra[..totalBytes]);
            return;
        }

        var stride = width * 4;
        fixed (byte* srcPtr = sourceBgra)
        fixed (byte* dstPtr = destinationBgra)
        {
            foreach (var rect in dirtyRects)
            {
                var rx = Math.Clamp(rect.X, 0, width);
                var ry = Math.Clamp(rect.Y, 0, height);
                var rw = Math.Clamp(rect.Width, 0, width - rx);
                var rh = Math.Clamp(rect.Height, 0, height - ry);
                if (rw <= 0 || rh <= 0)
                {
                    continue;
                }

                var rowBytes = (nuint)(rw * 4);
                for (var row = 0; row < rh; row++)
                {
                    var offset = (ry + row) * stride + (rx * 4);
                    Buffer.MemoryCopy(srcPtr + offset, dstPtr + offset, rowBytes, rowBytes);
                }
            }
        }
    }

    public static ViewportPlacement ComputeViewportPlacement(
        double containerWidth,
        double containerHeight,
        int guestWidth,
        int guestHeight,
        VmDisplayScalingMode scalingMode)
    {
        if (containerWidth <= 0 || containerHeight <= 0 || guestWidth <= 0 || guestHeight <= 0)
        {
            return new ViewportPlacement(0, 0, Math.Max(1, guestWidth), Math.Max(1, guestHeight), 1.0, 1.0);
        }

        switch (scalingMode)
        {
            case VmDisplayScalingMode.Stretch:
                return new ViewportPlacement(
                    0,
                    0,
                    containerWidth,
                    containerHeight,
                    containerWidth / guestWidth,
                    containerHeight / guestHeight);

            case VmDisplayScalingMode.OneToOne:
                var ox = Math.Max(0, (containerWidth - guestWidth) / 2.0);
                var oy = Math.Max(0, (containerHeight - guestHeight) / 2.0);
                return new ViewportPlacement(ox, oy, guestWidth, guestHeight, 1.0, 1.0);

            case VmDisplayScalingMode.FitAspectRatio:
            default:
                var scale = Math.Min(containerWidth / guestWidth, containerHeight / guestHeight);
                var renderedW = guestWidth * scale;
                var renderedH = guestHeight * scale;
                var offsetX = (containerWidth - renderedW) / 2.0;
                var offsetY = (containerHeight - renderedH) / 2.0;
                return new ViewportPlacement(offsetX, offsetY, renderedW, renderedH, scale, scale);
        }
    }

    public static (int GuestX, int GuestY) MapViewPointToGuestPixel(
        double viewX,
        double viewY,
        ViewportPlacement placement,
        int guestWidth,
        int guestHeight)
    {
        if (placement.RenderedWidth <= 0 || placement.RenderedHeight <= 0)
        {
            return (0, 0);
        }

        var relX = (viewX - placement.OffsetX) / placement.RenderedWidth;
        var relY = (viewY - placement.OffsetY) / placement.RenderedHeight;

        var gx = (int)Math.Clamp(Math.Round(relX * (guestWidth - 1)), 0, Math.Max(0, guestWidth - 1));
        var gy = (int)Math.Clamp(Math.Round(relY * (guestHeight - 1)), 0, Math.Max(0, guestHeight - 1));
        return (gx, gy);
    }

    /// <summary>
    /// Encodes a 32bpp BGRA framebuffer directly to a standard PNG file without external dependencies.
    /// Used for live VM card thumbnails and visual QA proof captures.
    /// </summary>
    public static void SaveBgraAsPng(string filePath, ReadOnlySpan<byte> bgraPixels, int width, int height)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (width <= 0 || height <= 0 || bgraPixels.Length < width * height * 4)
        {
            throw new ArgumentException("Invalid framebuffer dimensions or buffer length.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);

        // PNG Signature
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        fs.Write(signature);

        // IHDR Chunk (width, height, bitDepth=8, colorType=6 RGBA)
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[..4], (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.Slice(4, 4), (uint)height);
        ihdr[8] = 8; // Bit depth
        ihdr[9] = 6; // Color type: Truecolor with alpha (RGBA)
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WritePngChunk(fs, "IHDR"u8, ihdr);

        // IDAT Chunk (zlib stream of filter-0 scanlines converted from BGRA to RGBA)
        using (var idatMs = new MemoryStream())
        {
            using (var zlib = new ZLibStream(idatMs, CompressionLevel.Fastest, leaveOpen: true))
            {
                var rowBuffer = new byte[1 + width * 4];
                for (var y = 0; y < height; y++)
                {
                    rowBuffer[0] = 0; // Filter type 0 (None)
                    var srcRow = bgraPixels.Slice(y * width * 4, width * 4);
                    for (var x = 0; x < width; x++)
                    {
                        var si = x * 4;
                        var di = 1 + si;
                        rowBuffer[di + 0] = srcRow[si + 2]; // R
                        rowBuffer[di + 1] = srcRow[si + 1]; // G
                        rowBuffer[di + 2] = srcRow[si + 0]; // B
                        rowBuffer[di + 3] = 255;            // A
                    }
                    zlib.Write(rowBuffer, 0, rowBuffer.Length);
                }
            }
            WritePngChunk(fs, "IDAT"u8, idatMs.ToArray());
        }

        // IEND Chunk
        WritePngChunk(fs, "IEND"u8, []);
    }

    private static void WritePngChunk(Stream stream, ReadOnlySpan<byte> chunkType, ReadOnlySpan<byte> data)
    {
        Span<byte> lenBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lenBuf, (uint)data.Length);
        stream.Write(lenBuf);
        stream.Write(chunkType);
        if (data.Length > 0)
        {
            stream.Write(data);
        }

        var crc = ComputeCrc32(chunkType, data);
        Span<byte> crcBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBuf, crc);
        stream.Write(crcBuf);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type)
        {
            crc = UpdateCrcByte(crc, b);
        }
        foreach (var b in data)
        {
            crc = UpdateCrcByte(crc, b);
        }
        return ~crc;
    }

    private static uint UpdateCrcByte(uint crc, byte b)
    {
        crc ^= b;
        for (var k = 0; k < 8; k++)
        {
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return crc;
    }
}
