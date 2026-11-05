using System.Buffers.Binary;
using System.Text;

namespace WinQemu.Storage;

public sealed record IsoFileEntry(string FileName, byte[] Content);

/// <summary>
/// Pure C# ISO 9660 + El Torito bootable image builder.
/// Used both to package Cloud-Init (cidata) / Windows Autounattend (OEMDRV) seed ISOs (§37)
/// and to construct bootable El Torito CD-ROM images without external tools.
/// </summary>
public static class Iso9660Builder
{
    private const int SectorSize = 2048;

    public static void CreateDataIso(
        string outputIsoPath,
        string volumeIdentifier,
        IReadOnlyList<IsoFileEntry> files,
        byte[]? elToritoBootImage = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputIsoPath);
        ArgumentNullException.ThrowIfNull(files);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputIsoPath))!);

        // Layout:
        // Sectors 0..15: System Area (zeros)
        // Sector 16: Primary Volume Descriptor (PVD)
        // Sector 17: El Torito Boot Record Volume Descriptor (optional, if elToritoBootImage != null)
        // Sector 18 (or 17): Volume Descriptor Set Terminator
        // Next sector: Path Table (L-Table)
        // Next sector: Path Table (M-Table)
        // Next sector: Root Directory Extent
        // Next sector (optional): El Torito Boot Catalog + El Torito Boot Image
        // Subsequent sectors: File extents

        var hasElTorito = elToritoBootImage is { Length: > 0 };
        var terminatorSector = hasElTorito ? 18 : 17;
        var lPathTableSector = terminatorSector + 1;
        var mPathTableSector = lPathTableSector + 1;
        var rootDirSector = mPathTableSector + 1;
        var nextFreeSector = rootDirSector + 1;

        var bootCatalogSector = 0;
        var bootImageSector = 0;
        var bootImageSectorCount = 0;

        if (hasElTorito)
        {
            bootCatalogSector = nextFreeSector++;
            bootImageSector = nextFreeSector;
            bootImageSectorCount = (elToritoBootImage!.Length + SectorSize - 1) / SectorSize;
            nextFreeSector += Math.Max(1, bootImageSectorCount);
        }

        var fileLocations = new List<(IsoFileEntry Entry, int StartSector)>();
        foreach (var file in files)
        {
            var start = nextFreeSector;
            var sectors = Math.Max(1, (file.Content.Length + SectorSize - 1) / SectorSize);
            fileLocations.Add((file, start));
            nextFreeSector += sectors;
        }

        var totalSectors = nextFreeSector;
        var image = new byte[totalSectors * SectorSize];

        // 1. Write Root Directory Extent at rootDirSector
        var rootDirOffset = rootDirSector * SectorSize;
        var dirCursor = rootDirOffset;
        dirCursor += WriteDirectoryRecord(image.AsSpan(dirCursor), "\x00", rootDirSector, SectorSize, isDirectory: true);
        dirCursor += WriteDirectoryRecord(image.AsSpan(dirCursor), "\x01", rootDirSector, SectorSize, isDirectory: true);

        foreach (var (entry, startSector) in fileLocations)
        {
            var isoName = FormatIsoFileName(entry.FileName);
            dirCursor += WriteDirectoryRecord(
                image.AsSpan(dirCursor),
                isoName,
                startSector,
                entry.Content.Length,
                isDirectory: false);
        }

        // 2. Write Path Tables
        WritePathTable(image.AsSpan(lPathTableSector * SectorSize, SectorSize), rootDirSector, bigEndian: false);
        WritePathTable(image.AsSpan(mPathTableSector * SectorSize, SectorSize), rootDirSector, bigEndian: true);

        // 3. Write Primary Volume Descriptor at Sector 16
        var pvd = image.AsSpan(16 * SectorSize, SectorSize);
        pvd[0] = 0x01; // Type: Primary Volume Descriptor
        Encoding.ASCII.GetBytes("CD001").CopyTo(pvd[1..6]);
        pvd[6] = 0x01; // Version 1
        WritePaddedAscii(pvd.Slice(8, 32), "WINQEMU");
        WritePaddedAscii(pvd.Slice(40, 32), volumeIdentifier.ToUpperInvariant());
        WriteBothEndianInt32(pvd.Slice(80, 8), totalSectors); // Volume Space Size
        WriteBothEndianInt16(pvd.Slice(120, 4), 1); // Volume Set Size
        WriteBothEndianInt16(pvd.Slice(124, 4), 1); // Volume Sequence Number
        WriteBothEndianInt16(pvd.Slice(128, 4), (short)SectorSize); // Logical Block Size
        WriteBothEndianInt32(pvd.Slice(132, 8), 10); // Path Table Size (10 bytes for root)
        BinaryPrimitives.WriteInt32LittleEndian(pvd.Slice(140, 4), lPathTableSector);
        BinaryPrimitives.WriteInt32BigEndian(pvd.Slice(148, 4), mPathTableSector);
        // Root Directory Record at offset 156 (34 bytes)
        WriteDirectoryRecord(pvd.Slice(156, 34), "\x00", rootDirSector, SectorSize, isDirectory: true);
        WritePaddedAscii(pvd.Slice(190, 128), volumeIdentifier.ToUpperInvariant());
        WritePaddedAscii(pvd.Slice(318, 128), "WINQEMU FRONTEND");
        WriteDecDateTime(pvd.Slice(813, 17));
        WriteDecDateTime(pvd.Slice(830, 17));
        pvd[881] = 0x01; // File Structure Version

        // 4. Write El Torito Boot Record + Catalog + Image if present
        if (hasElTorito)
        {
            var brvd = image.AsSpan(17 * SectorSize, SectorSize);
            brvd[0] = 0x00; // Boot Record
            Encoding.ASCII.GetBytes("CD001").CopyTo(brvd[1..6]);
            brvd[6] = 0x01;
            WritePaddedAscii(brvd.Slice(7, 32), "EL TORITO SPECIFICATION", padChar: '\0');
            BinaryPrimitives.WriteInt32LittleEndian(brvd.Slice(71, 4), bootCatalogSector);

            var cat = image.AsSpan(bootCatalogSector * SectorSize, SectorSize);
            // Validation Entry (32 bytes)
            cat[0] = 0x01; // Header ID
            cat[1] = 0x00; // Platform ID: 80x86
            cat[30] = 0x55;
            cat[31] = 0xAA;
            // Compute 16-bit checksum so sum of all 16-bit words in validation entry == 0
            ushort sum = 0;
            for (var i = 0; i < 32; i += 2)
            {
                sum += BinaryPrimitives.ReadUInt16LittleEndian(cat.Slice(i, 2));
            }
            var checksum = (ushort)(0 - sum);
            BinaryPrimitives.WriteUInt16LittleEndian(cat.Slice(28, 2), checksum);

            // Initial/Default Entry (32 bytes at offset 32)
            var initEntry = cat.Slice(32, 32);
            initEntry[0] = 0x88; // Bootable
            initEntry[1] = 0x00; // No emulation
            BinaryPrimitives.WriteUInt16LittleEndian(initEntry.Slice(2, 2), 0x0000); // Load segment (default 0x07C0)
            var virtualSectors = (ushort)Math.Clamp((elToritoBootImage!.Length + 511) / 512, 1, 64);
            BinaryPrimitives.WriteUInt16LittleEndian(initEntry.Slice(6, 2), virtualSectors);
            BinaryPrimitives.WriteInt32LittleEndian(initEntry.Slice(8, 4), bootImageSector);

            elToritoBootImage.CopyTo(image.AsSpan(bootImageSector * SectorSize));
        }

        // 5. Write Volume Descriptor Set Terminator
        var term = image.AsSpan(terminatorSector * SectorSize, SectorSize);
        term[0] = 0xFF;
        Encoding.ASCII.GetBytes("CD001").CopyTo(term[1..6]);
        term[6] = 0x01;

        // 6. Write File Extents
        foreach (var (entry, startSector) in fileLocations)
        {
            entry.Content.CopyTo(image.AsSpan(startSector * SectorSize));
        }

        File.WriteAllBytes(outputIsoPath, image);
    }

    private static int WriteDirectoryRecord(
        Span<byte> target,
        string identifier,
        int extentSector,
        int dataLength,
        bool isDirectory)
    {
        var idBytes = Encoding.ASCII.GetBytes(identifier);
        var recLen = 33 + idBytes.Length;
        if ((recLen & 1) != 0)
        {
            recLen++;
        }

        var rec = target[..recLen];
        rec.Clear();
        rec[0] = (byte)recLen;
        WriteBothEndianInt32(rec.Slice(2, 8), extentSector);
        WriteBothEndianInt32(rec.Slice(10, 8), dataLength);

        var now = DateTime.UtcNow;
        rec[18] = (byte)(now.Year - 1900);
        rec[19] = (byte)now.Month;
        rec[20] = (byte)now.Day;
        rec[21] = (byte)now.Hour;
        rec[22] = (byte)now.Minute;
        rec[23] = (byte)now.Second;
        rec[25] = isDirectory ? (byte)0x02 : (byte)0x00;

        WriteBothEndianInt16(rec.Slice(28, 4), 1); // Volume Sequence Number
        rec[32] = (byte)idBytes.Length;
        idBytes.CopyTo(rec.Slice(33, idBytes.Length));
        return recLen;
    }

    private static void WritePathTable(Span<byte> sector, int rootExtentSector, bool bigEndian)
    {
        sector.Clear();
        sector[0] = 1; // Length of Directory Identifier
        sector[1] = 0; // Extended Attribute Record Length
        if (bigEndian)
        {
            BinaryPrimitives.WriteInt32BigEndian(sector.Slice(2, 4), rootExtentSector);
            BinaryPrimitives.WriteInt16BigEndian(sector.Slice(6, 2), 1);
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(sector.Slice(2, 4), rootExtentSector);
            BinaryPrimitives.WriteInt16LittleEndian(sector.Slice(6, 2), 1);
        }
        sector[8] = 0; // Root identifier
        sector[9] = 0; // Padding
    }

    private static string FormatIsoFileName(string name)
    {
        var clean = Path.GetFileName(name).Trim();
        return clean + ";1";
    }

    private static void WritePaddedAscii(Span<byte> target, string text, char padChar = ' ')
    {
        target.Fill((byte)padChar);
        var bytes = Encoding.ASCII.GetBytes(text);
        bytes.AsSpan(0, Math.Min(bytes.Length, target.Length)).CopyTo(target);
    }

    private static void WriteBothEndianInt32(Span<byte> target, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(target[..4], value);
        BinaryPrimitives.WriteInt32BigEndian(target.Slice(4, 4), value);
    }

    private static void WriteBothEndianInt16(Span<byte> target, short value)
    {
        BinaryPrimitives.WriteInt16LittleEndian(target[..2], value);
        BinaryPrimitives.WriteInt16BigEndian(target.Slice(2, 2), value);
    }

    private static void WriteDecDateTime(Span<byte> target)
    {
        var str = DateTime.UtcNow.ToString("yyyyMMddHHmmss00");
        Encoding.ASCII.GetBytes(str).CopyTo(target[..16]);
        target[16] = 0;
    }
}
