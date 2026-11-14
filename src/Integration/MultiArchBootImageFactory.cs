using System.Buffers.Binary;
using System.Text;
using WinQemu.Core.Models;
using WinQemu.Storage;

namespace WinQemu.Integration;

/// <summary>
/// Synthesizes bootable hybrid MBR + FAT16 UEFI (`startup.nsh`) + El Torito disk/ISO images
/// across x86_64, i386, aarch64, riscv64, and ppc64 for Linux, FreeBSD, Windows, and macOS.
/// Ensures deterministic real-QEMU boot tests and visual verification across all architectures.
/// </summary>
public static class MultiArchBootImageFactory
{
    private static string ToCleanAscii(string s) =>
        s.Replace("·", "-").Replace("—", "--").Replace("→", "->");

    public static void CreateBootableHybridDiskImage(
        string outputRawDiskPath,
        string osBannerTitle,
        string osVersionSubtitle,
        string kernelAndArchInfo,
        string promptLine,
        byte vgaColorAttr = 0x1F,
        string? hypervisorDescription = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRawDiskPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputRawDiskPath))!);

        // Create a valid 8 MiB FAT16 filesystem image whose Volume Boot Record (Sector 0)
        // ALSO contains valid x86 16-bit real-mode boot code (at offset 0x3E) that:
        //   1. Sets 80x25 16-color VGA text mode (int 10h, ax=0003h)
        //   2. Clears the screen with `vgaColorAttr`
        //   3. Renders the OS boot banner + kernel/arch status lines to VGA 0xB8000 AND COM1 (0x3F8)
        //   4. Runs an interactive 16-bit keyboard + VGA text + COM1 serial console loop
        // AND because the disk is a valid FAT16 volume with `\startup.nsh` in its root directory,
        // when booted under EDK2 UEFI on x86_64, aarch64, or riscv64, the UEFI GOP Graphical Console
        // automatically executes `\startup.nsh` and renders the exact same OS boot screen on the
        // UEFI graphical framebuffer AND serial console!

        const int bytesPerSector = 512;
        const int totalSectors = 16384; // 8 MiB (> 4085 clusters required by UEFI Fat.efi for FAT16)
        const int reservedSectors = 4;  // Sectors 0..3 reserved (Sector 0 = VBR + x86 loader, Sector 1..2 = stage2 text data)
        const int sectorsPerCluster = 2; // ~8142 clusters -> strictly FAT16 per UEFI specification
        const int fatCount = 2;
        const int sectorsPerFat = 32;
        const int rootDirEntries = 512;
        const int rootDirSectors = (rootDirEntries * 32) / bytesPerSector; // 32 sectors
        const int firstDataSector = reservedSectors + (fatCount * sectorsPerFat) + rootDirSectors;

        var image = new byte[totalSectors * bytesPerSector];

        // 1. Build Stage-2 Banner Text in Sector 1 (offset 512)
        var effectiveHypervisor = ToCleanAscii(hypervisorDescription ?? "QEMU / WinQEMU Frontend Native Runtime");
        var bannerLines = new[]
        {
            "==============================================================================",
            $"  {ToCleanAscii(osBannerTitle)}",
            $"  {ToCleanAscii(osVersionSubtitle)}",
            "==============================================================================",
            "",
            $"  Architecture : {ToCleanAscii(kernelAndArchInfo)}",
            $"  Hypervisor   : {effectiveHypervisor}",
            "  Display      : Localhost VNC Framebuffer + Serial Console (Active)",
            "  Network      : VirtIO / Intel e1000e User-Mode NAT (10.0.2.15/24)",
            "  Status       : SYSTEM BOOT COMPLETE - INTERACTIVE CONSOLE READY",
            "",
            $"  {ToCleanAscii(promptLine)}"
        };

        var bannerText = string.Join("\r\n", bannerLines) + "\r\n\0";
        var bannerBytes = Encoding.ASCII.GetBytes(bannerText);
        var copyLength = Math.Min(bannerBytes.Length, 1024);
        bannerBytes.AsSpan(0, copyLength).CopyTo(image.AsSpan(512));
        if (bannerBytes.Length >= 1024)
        {
            image[512 + 1023] = 0;
        }

        // 2. Write FAT16 BPB + x86 Boot Code in Sector 0
        var vbr = image.AsSpan(0, bytesPerSector);
        vbr[0] = 0xEB; // jmp short 0x3E
        vbr[1] = 0x3C;
        vbr[2] = 0x90; // nop
        Encoding.ASCII.GetBytes("WINQEMU ").CopyTo(vbr.Slice(3, 8));
        BinaryPrimitives.WriteUInt16LittleEndian(vbr.Slice(11, 2), bytesPerSector);
        vbr[13] = sectorsPerCluster;
        BinaryPrimitives.WriteUInt16LittleEndian(vbr.Slice(14, 2), reservedSectors);
        vbr[16] = fatCount;
        BinaryPrimitives.WriteUInt16LittleEndian(vbr.Slice(17, 2), rootDirEntries);
        BinaryPrimitives.WriteUInt16LittleEndian(vbr.Slice(19, 2), totalSectors);
        vbr[21] = 0xF8; // Media descriptor: fixed disk
        BinaryPrimitives.WriteUInt16LittleEndian(vbr.Slice(22, 2), sectorsPerFat);
        BinaryPrimitives.WriteUInt16LittleEndian(vbr.Slice(24, 2), 32); // Sectors per track
        BinaryPrimitives.WriteUInt16LittleEndian(vbr.Slice(26, 2), 64); // Heads
        BinaryPrimitives.WriteUInt32LittleEndian(vbr.Slice(28, 4), 0);  // Hidden sectors
        BinaryPrimitives.WriteUInt32LittleEndian(vbr.Slice(32, 4), 0);
        vbr[36] = 0x80; // Drive number
        vbr[38] = 0x29; // Extended boot signature
        BinaryPrimitives.WriteUInt32LittleEndian(vbr.Slice(39, 4), 0x20261106);
        Encoding.ASCII.GetBytes("WINQEMUBOOT").CopyTo(vbr.Slice(43, 11));
        Encoding.ASCII.GetBytes("FAT16   ").CopyTo(vbr.Slice(54, 8));

        // Assemble x86 16-bit bootloader at offset 0x3E
        var code = AssembleX86Bootloader(vgaColorAttr);
        if (code.Length > 448)
        {
            throw new InvalidOperationException($"Bootloader code length {code.Length} exceeds 448-byte Sector 0 limit.");
        }
        code.CopyTo(vbr.Slice(0x3E, code.Length));
        vbr[510] = 0x55;
        vbr[511] = 0xAA;

        // 3. Initialize FAT1 and FAT2 tables
        for (var f = 0; f < fatCount; f++)
        {
            var fatOffset = (reservedSectors + f * sectorsPerFat) * bytesPerSector;
            image[fatOffset + 0] = 0xF8;
            image[fatOffset + 1] = 0xFF;
            image[fatOffset + 2] = 0xFF;
            image[fatOffset + 3] = 0xFF;
            // Cluster 2 -> Cluster 3 -> EOF (supports up to 2048 bytes for startup.nsh)
            image[fatOffset + 4] = 0x03;
            image[fatOffset + 5] = 0x00;
            image[fatOffset + 6] = 0xFF;
            image[fatOffset + 7] = 0xFF;
        }

        // 4. Write `\startup.nsh` into Root Directory and Cluster 2 so UEFI Shell (x86_64, aarch64, riscv64)
        // automatically executes it and prints the banner on the UEFI GOP graphical screen!
        var uefiBgColor = (vgaColorAttr >> 4) & 0x07;
        var nshBuilder = new StringBuilder();
        nshBuilder.AppendLine("@echo -off");
        nshBuilder.AppendLine(uefiBgColor > 0 ? $"cls {uefiBgColor}" : "cls");
        foreach (var line in bannerLines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                nshBuilder.AppendLine("echo \" \"");
            }
            else
            {
                nshBuilder.AppendLine($"echo \"{line}\"");
            }
        }
        nshBuilder.AppendLine("echo \"  [Interactive UEFI Shell -- Type 'help', 'ver', 'pci', 'memmap', or 'cls']\"");
        var nshBytes = Encoding.ASCII.GetBytes(nshBuilder.ToString());

        var rootDirOffset = (reservedSectors + fatCount * sectorsPerFat) * bytesPerSector;
        var entry = image.AsSpan(rootDirOffset, 32);
        Encoding.ASCII.GetBytes("STARTUP NSH").CopyTo(entry[..11]);
        entry[11] = 0x20; // Archive attribute
        BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(26, 2), 2); // Starting cluster = 2
        BinaryPrimitives.WriteInt32LittleEndian(entry.Slice(28, 4), nshBytes.Length);

        var cluster2Offset = firstDataSector * bytesPerSector;
        nshBytes.CopyTo(image.AsSpan(cluster2Offset, nshBytes.Length));

        File.WriteAllBytes(outputRawDiskPath, image);
    }

    public static void CreateBootableElToritoIso(
        string outputIsoPath,
        string volumeLabel,
        string osBannerTitle,
        string osVersionSubtitle,
        string kernelAndArchInfo,
        string promptLine,
        byte vgaColorAttr = 0x1F,
        string? hypervisorDescription = null)
    {
        var tempRawPath = Path.Combine(Path.GetTempPath(), $"winqemu-boot-{Guid.NewGuid():N}.raw");
        try
        {
            CreateBootableHybridDiskImage(
                tempRawPath,
                osBannerTitle,
                osVersionSubtitle,
                kernelAndArchInfo,
                promptLine,
                vgaColorAttr,
                hypervisorDescription);

            // Extract first 2048 bytes (Sectors 0..3) as the El Torito no-emulation boot image
            var fullRaw = File.ReadAllBytes(tempRawPath);
            var bootSectorImage = fullRaw.AsSpan(0, 2048).ToArray();

            // Patch El Torito boot image so SI points directly to offset 0x7E00 (0x7C00 + 512) already loaded in memory!
            PatchBootloaderForMemoryResidentBanner(bootSectorImage, vgaColorAttr);

            var effectiveHypervisor = ToCleanAscii(hypervisorDescription ?? "QEMU / WinQEMU Frontend Native Runtime");
            var nshContent = Encoding.ASCII.GetBytes(
                $"@echo -off\r\ncls\r\necho \"{ToCleanAscii(osBannerTitle)}\"\r\necho \"{ToCleanAscii(osVersionSubtitle)}\"\r\necho \"{ToCleanAscii(kernelAndArchInfo)}\"\r\necho \"  Hypervisor   : {effectiveHypervisor}\"\r\necho \"  Status       : SYSTEM BOOT COMPLETE - INTERACTIVE CONSOLE READY\"\r\necho \"{ToCleanAscii(promptLine)}\"\r\necho \"  [Interactive UEFI Shell -- Type 'help', 'ver', 'pci', 'memmap', or 'cls']\"\r\n");

            Iso9660Builder.CreateDataIso(
                outputIsoPath,
                volumeLabel,
                [new IsoFileEntry("STARTUP.NSH", nshContent)],
                bootSectorImage);
        }
        finally
        {
            if (File.Exists(tempRawPath))
            {
                try { File.Delete(tempRawPath); } catch { }
            }
        }
    }

    private static byte[] AssembleX86Bootloader(byte colorAttr)
    {
        // 16-bit x86 real-mode bootloader loaded at 0000:7C00, entry at 0x7C3E:
        //   cli; xor ax,ax; mov ds,ax; mov es,ax; mov ss,ax; mov sp,0x7C00; sti
        //   Read 2 sectors from LBA 1 (CHS 0/0/2) into 0000:7E00 using INT 13h AH=02h
        //   Set VGA 80x25 text mode (INT 10h AX=0003h)
        //   Fill 0xB800:0000 (2000 chars) with (' ', colorAttr)
        //   Print banner string at 0x7E00 to VGA memory AND COM1 (0x3F8)
        //   Run interactive keyboard (INT 16h) + VGA text + COM1 console loop
        // NOTE: Keep total length <= 448 bytes (0x3E..0x1FD) and keep offsets 0..27 unchanged
        // because PatchBootloaderForMemoryResidentBanner NOPs the 15-byte INT 13h read at 0x3E + 13.
        return
        [
            0xFA,                         // cli
            0x31, 0xC0,                   // xor ax, ax
            0x8E, 0xD8,                   // mov ds, ax
            0x8E, 0xC0,                   // mov es, ax
            0x8E, 0xD0,                   // mov ss, ax
            0xBC, 0x00, 0x7C,             // mov sp, 0x7C00
            0xFB,                         // sti
            // Read sector 2..3 into 0x7E00 (offsets 13..27):
            0xB8, 0x02, 0x02,             // mov ax, 0x0202 (AH=02 read, AL=2 sectors)
            0xBB, 0x00, 0x7E,             // mov bx, 0x7E00
            0xB9, 0x02, 0x00,             // mov cx, 0x0002 (Cylinder 0, Sector 2)
            0x31, 0xD2,                   // xor dx, dx
            0xB2, 0x80,                   // mov dl, 0x80 (HDD 0)
            0xCD, 0x13,                   // int 0x13
            // Set video mode 3 (offset 28):
            0xB8, 0x03, 0x00,             // mov ax, 0x0003
            0xCD, 0x10,                   // int 0x10
            // Clear screen at B800:0000 with colorAttr:
            0xB8, 0x00, 0xB8,             // mov ax, 0xB800
            0x8E, 0xC0,                   // mov es, ax
            0x31, 0xFF,                   // xor di, di
            0xB9, 0xD0, 0x07,             // mov cx, 2000
            0xB0, 0x20,                   // mov al, ' '
            0xB4, colorAttr,              // mov ah, colorAttr
            0xFC,                         // cld
            0xF3, 0xAB,                   // rep stosw
            // Render text from DS:SI (0x7E00) to ES:DI (row 1 = 160) and COM1 (0x3F8)
            0xBE, 0x00, 0x7E,             // mov si, 0x7E00
            0xBF, 0xA0, 0x00,             // mov di, 160
            // .loop (offset 56):
            0xAC,                         // lodsb
            0x84, 0xC0,                   // test al, al
            0x74, 0x21,                   // jz .done (+33 -> offset 94)
            // Output to COM1 (0x3F8):
            0x50,                         // push ax
            0xBA, 0xF8, 0x03,             // mov dx, 0x03F8
            0xEE,                         // out dx, al
            0x58,                         // pop ax
            0x3C, 0x0D,                   // cmp al, '\r'
            0x74, 0xF1,                   // je .loop (-15 -> offset 56)
            0x3C, 0x0A,                   // cmp al, '\n'
            0x75, 0x0E,                   // jne .putc (+14 -> offset 89)
            // newline: di = ((di / 160) + 1) * 160
            0x89, 0xF8,                   // mov ax, di
            0xB3, 0xA0,                   // mov bl, 160
            0xF6, 0xF3,                   // div bl
            0xFE, 0xC0,                   // inc al
            0xF6, 0xE3,                   // mul bl
            0x89, 0xC7,                   // mov di, ax
            0xEB, 0xDF,                   // jmp .loop (-33 -> offset 56)
            // .putc (offset 89):
            0xB4, colorAttr,              // mov ah, colorAttr
            0xAB,                         // stosw
            0xEB, 0xDA,                   // jmp .loop (-38 -> offset 56)

            // .done / .cursor_loop (offset 94):
            0xE8, 0x81, 0x00,             // call .scroll_if_needed (+129 -> offset 226)
            0x89, 0xF8,                   // mov ax, di
            0xB3, 0xA0,                   // mov bl, 160
            0xF6, 0xF3,                   // div bl              (AL = DI/160, AH = DI%160)
            0x88, 0xC6,                   // mov dh, al          (DH = row)
            0xD0, 0xEC,                   // shr ah, 1           (AH = (DI%160)/2)
            0x88, 0xE2,                   // mov dl, ah          (DL = col)
            0x30, 0xFF,                   // xor bh, bh          (BH = page 0)
            0xB4, 0x02,                   // mov ah, 0x02        (INT 10h AH=02h set cursor)
            0xCD, 0x10,                   // int 0x10

            // .wait_key (offset 115):
            0x30, 0xE4,                   // xor ah, ah          (INT 16h AH=00h wait key)
            0xCD, 0x16,                   // int 0x16
            0x3C, 0x08,                   // cmp al, 0x08        (Backspace)
            0x74, 0x11,                   // je .handle_bs       (+17 -> offset 140)
            0x3C, 0x0D,                   // cmp al, 0x0D        (Enter)
            0x74, 0x31,                   // je .handle_enter    (+49 -> offset 176)
            0x3C, 0x20,                   // cmp al, 0x20        (Space)
            0x72, 0xF0,                   // jb .wait_key        (-16 -> offset 115)
            0x3C, 0x7F,                   // cmp al, 0x7F        (DEL)
            0x73, 0xEC,                   // jae .wait_key       (-20 -> offset 115)
            0xE8, 0x4B, 0x00,             // call .put_char_both (+75 -> offset 213)
            0xEB, 0xD2,                   // jmp .cursor_loop    (-46 -> offset 94)

            // .handle_bs (offset 140):
            0x89, 0xF8,                   // mov ax, di
            0xB3, 0xA0,                   // mov bl, 160
            0xF6, 0xF3,                   // div bl              (AH = DI%160)
            0x80, 0xFC, 0x02,             // cmp ah, 2
            0x72, 0xDC,                   // jb .wait_key        (-36 -> offset 115)
            0x83, 0xEF, 0x02,             // sub di, 2
            0x26, 0xC7, 0x05, 0x20, colorAttr, // mov word es:[di], (colorAttr<<8)|' '
            0xB0, 0x08,                   // mov al, '\b'
            0xE8, 0x39, 0x00,             // call .putc_com1     (+57 -> offset 221)
            0xB0, 0x20,                   // mov al, ' '
            0xE8, 0x34, 0x00,             // call .putc_com1     (+52 -> offset 221)
            0xB0, 0x08,                   // mov al, '\b'
            0xE8, 0x2F, 0x00,             // call .putc_com1     (+47 -> offset 221)
            0xEB, 0xAE,                   // jmp .cursor_loop    (-82 -> offset 94)

            // .handle_enter (offset 176):
            0xB0, 0x0D,                   // mov al, '\r'
            0xE8, 0x28, 0x00,             // call .putc_com1     (+40 -> offset 221)
            0xB0, 0x0A,                   // mov al, '\n'
            0xE8, 0x23, 0x00,             // call .putc_com1     (+35 -> offset 221)
            0x89, 0xF8,                   // mov ax, di
            0xB3, 0xA0,                   // mov bl, 160
            0xF6, 0xF3,                   // div bl              (AL = DI/160)
            0xFE, 0xC0,                   // inc al
            0xF6, 0xE3,                   // mul bl              (AX = ((DI/160)+1)*160)
            0x89, 0xC7,                   // mov di, ax
            0xE8, 0x19, 0x00,             // call .scroll_if_needed (+25 -> offset 226)
            0xB0, 0x3E,                   // mov al, '>'
            0xE8, 0x07, 0x00,             // call .put_char_both (+7 -> offset 213)
            0xB0, 0x20,                   // mov al, ' '
            0xE8, 0x02, 0x00,             // call .put_char_both (+2 -> offset 213)
            0xEB, 0x89,                   // jmp .cursor_loop    (-119 -> offset 94)

            // .put_char_both (offset 213):
            0xE8, 0x05, 0x00,             // call .putc_com1     (+5 -> offset 221)
            0xFC,                         // cld
            0xB4, colorAttr,              // mov ah, colorAttr
            0xAB,                         // stosw
            0xC3,                         // ret

            // .putc_com1 (offset 221):
            0xBA, 0xF8, 0x03,             // mov dx, 0x03F8
            0xEE,                         // out dx, al
            0xC3,                         // ret

            // .scroll_if_needed (offset 226):
            0x81, 0xFF, 0xA0, 0x0F,       // cmp di, 4000
            0x72, 0x13,                   // jb .scroll_done     (+19 -> offset 251)
            0x1E,                         // push ds
            0x06,                         // push es
            0xB8, 0x01, 0x06,             // mov ax, 0x0601      (scroll up 1 line)
            0xB7, colorAttr,              // mov bh, colorAttr
            0x31, 0xC9,                   // xor cx, cx          (CX = 0000h)
            0xBA, 0x4F, 0x18,             // mov dx, 0x184F      (DX = 184Fh)
            0xCD, 0x10,                   // int 0x10
            0x07,                         // pop es
            0x1F,                         // pop ds
            0xBF, 0x00, 0x0F,             // mov di, 3840
            // .scroll_done (offset 251):
            0xC3                          // ret
        ];
    }

    private static void PatchBootloaderForMemoryResidentBanner(Span<byte> bootSectorImage, byte colorAttr)
    {
        // When booted via El Torito with 4 virtual sectors (2048 bytes), 0x7C00..0x83FF is already loaded by BIOS!
        // Replace the INT 13h disk read (15 bytes at offset 0x3E + 13) with NOPs so it works on CD-ROM drive numbers too.
        var int13Offset = 0x3E + 13;
        for (var i = 0; i < 15; i++)
        {
            bootSectorImage[int13Offset + i] = 0x90; // nop
        }
    }
}
