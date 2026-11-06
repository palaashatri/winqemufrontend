using System.Security;
using System.Security.Cryptography;
using System.Text;
using WinQemu.Core.Models;

namespace WinQemu.Core.Presets;

/// <summary>
/// Generates unattended installation answer files (Windows Autounattend.xml, Cloud-Init user-data/meta-data,
/// Fedora Kickstart, Debian Preseed) without persisting plaintext passwords in application logs (§37).
/// </summary>
public static class UnattendedInstallGenerator
{
    private const string CryptBase64Alphabet = "./0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public static (string FileName, string Content) GenerateCloudInitFiles(
        UnattendedInstallConfig config,
        string? transientPassword = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ValidatePasswordNoControlChars(transientPassword);

        var sb = new StringBuilder();
        sb.AppendLine("#cloud-config");
        sb.AppendLine($"hostname: {SanitizeIdentifier(config.Hostname, "winqemu-vm")}");
        sb.AppendLine("manage_etc_hosts: true");
        sb.AppendLine("users:");
        sb.AppendLine("  - default");
        sb.AppendLine($"  - name: {SanitizeIdentifier(config.Username, "user")}");
        sb.AppendLine($"    gecos: {SanitizeText(config.FullName, "VM User")}");
        sb.AppendLine("    sudo: ALL=(ALL) NOPASSWD:ALL");
        sb.AppendLine("    groups: users, admin, wheel, sudo");
        sb.AppendLine("    shell: /bin/bash");
        sb.AppendLine("    lock_passwd: false");
        if (!string.IsNullOrEmpty(transientPassword))
        {
            var hashedPass = HashPasswordSha512Crypt(transientPassword);
            sb.AppendLine($"    passwd: '{hashedPass}'");
            sb.AppendLine("chpasswd:");
            sb.AppendLine("  expire: false");
        }
        if (config.InstallGuestAgent)
        {
            sb.AppendLine("packages:");
            sb.AppendLine("  - qemu-guest-agent");
            sb.AppendLine("  - spice-vdagent");
            sb.AppendLine("runcmd:");
            sb.AppendLine("  - [ systemctl, enable, --now, qemu-guest-agent ]");
        }

        return ("user-data", sb.ToString());
    }

    public static string GenerateCloudInitMetaData(UnattendedInstallConfig config, string instanceId)
    {
        ArgumentNullException.ThrowIfNull(config);
        return $"""
            instance-id: {SanitizeIdentifier(instanceId, "iid-local01")}
            local-hostname: {SanitizeIdentifier(config.Hostname, "winqemu-vm")}
            """;
    }

    public static string GenerateDebianPreseedCfg(
        UnattendedInstallConfig config,
        string? transientPassword = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ValidatePasswordNoControlChars(transientPassword);

        var username = SanitizeIdentifier(config.Username, "user");
        var fullName = SanitizeText(config.FullName, "Debian User");
        var hostname = SanitizeIdentifier(config.Hostname, "debian-vm");
        const string locale = "en_US.UTF-8";
        const string keymap = "us";
        const string timezone = "UTC";

        var sb = new StringBuilder();
        sb.AppendLine("# Debian Automated Installation Preseed Configuration (§37)");
        sb.AppendLine($"d-i debian-installer/locale string {locale}");
        sb.AppendLine($"d-i keyboard-configuration/xkb-keymap select {keymap}");
        sb.AppendLine($"d-i netcfg/get_hostname string {hostname}");
        sb.AppendLine("d-i netcfg/get_domain string local");
        sb.AppendLine("d-i passwd/root-login boolean false");
        sb.AppendLine($"d-i passwd/user-fullname string {fullName}");
        sb.AppendLine($"d-i passwd/username string {username}");
        if (!string.IsNullOrEmpty(transientPassword))
        {
            var hashedPass = HashPasswordSha512Crypt(transientPassword);
            sb.AppendLine($"d-i passwd/user-password-crypted password {hashedPass}");
        }
        sb.AppendLine($"d-i time/zone string {timezone}");
        sb.AppendLine("d-i clock-setup/utc boolean true");
        sb.AppendLine("d-i partman-auto/method string regular");
        sb.AppendLine("d-i partman-auto/choose_recipe select atomic");
        sb.AppendLine("d-i partman/confirm_write_new_label boolean true");
        sb.AppendLine("d-i partman/choose_partition select finish");
        sb.AppendLine("d-i partman/confirm boolean true");
        sb.AppendLine("d-i partman/confirm_nooverwrite boolean true");
        if (config.InstallGuestAgent)
        {
            sb.AppendLine("d-i pkgsel/include string qemu-guest-agent spice-vdagent sudo");
        }
        sb.AppendLine("d-i grub-installer/only_debian boolean true");
        sb.AppendLine("d-i finish-install/reboot_in_progress note");
        return sb.ToString();
    }

    public static string GenerateWindowsAutounattendXml(
        UnattendedInstallConfig config,
        string? transientPassword = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ValidatePasswordNoControlChars(transientPassword);

        var escapedUser = SecurityElement.Escape(SanitizeIdentifier(config.Username, "User")) ?? "User";
        var escapedFullName = SecurityElement.Escape(SanitizeText(config.FullName, "Windows User")) ?? "Windows User";
        var escapedHost = SecurityElement.Escape(SanitizeIdentifier(config.Hostname, "WINQEMU-PC")) ?? "WINQEMU-PC";
        // Encode password using Windows Setup UTF-16LE "<password>Password" Base64 format with PlainText=false (§37)
        var encodedPassword = Convert.ToBase64String(Encoding.Unicode.GetBytes((transientPassword ?? string.Empty) + "Password"));

        var labConfigBlock = config.BypassWindows11Requirements
            ? """
              <RunSynchronous>
                <RunSynchronousCommand wcm:action="add">
                  <Order>1</Order>
                  <Path>reg add HKLM\SYSTEM\Setup\LabConfig /v BypassTPMCheck /t REG_DWORD /d 1 /f</Path>
                </RunSynchronousCommand>
                <RunSynchronousCommand wcm:action="add">
                  <Order>2</Order>
                  <Path>reg add HKLM\SYSTEM\Setup\LabConfig /v BypassSecureBootCheck /t REG_DWORD /d 1 /f</Path>
                </RunSynchronousCommand>
                <RunSynchronousCommand wcm:action="add">
                  <Order>3</Order>
                  <Path>reg add HKLM\SYSTEM\Setup\LabConfig /v BypassRAMCheck /t REG_DWORD /d 1 /f</Path>
                </RunSynchronousCommand>
              </RunSynchronous>
              """
            : string.Empty;

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <unattend xmlns="urn:schemas-microsoft-com:unattend" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State">
              <settings pass="windowsPE">
                <component name="Microsoft-Windows-International-Core-WinPE" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
                  <SetupUILanguage>
                    <UILanguage>en-US</UILanguage>
                  </SetupUILanguage>
                  <InputLocale>0409:00000409</InputLocale>
                  <SystemLocale>en-US</SystemLocale>
                  <UILanguage>en-US</UILanguage>
                  <UserLocale>en-US</UserLocale>
                </component>
                <component name="Microsoft-Windows-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
                  {labConfigBlock}
                  <UserData>
                    <AcceptEula>true</AcceptEula>
                    <FullName>{escapedFullName}</FullName>
                  </UserData>
                </component>
              </settings>
              <settings pass="specialize">
                <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
                  <ComputerName>{escapedHost}</ComputerName>
                </component>
              </settings>
              <settings pass="oobeSystem">
                <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
                  <OOBE>
                    <HideEULAPage>true</HideEULAPage>
                    <HideOEMRegistrationScreen>true</HideOEMRegistrationScreen>
                    <HideOnlineAccountScreens>true</HideOnlineAccountScreens>
                    <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>
                    <ProtectYourPC>3</ProtectYourPC>
                  </OOBE>
                  <UserAccounts>
                    <LocalAccounts>
                      <LocalAccount wcm:action="add">
                        <Name>{escapedUser}</Name>
                        <DisplayName>{escapedFullName}</DisplayName>
                        <Group>Administrators</Group>
                        <Password>
                          <Value>{encodedPassword}</Value>
                          <PlainText>false</PlainText>
                        </Password>
                      </LocalAccount>
                    </LocalAccounts>
                  </UserAccounts>
                </component>
              </settings>
            </unattend>
            """;
    }

    /// <summary>
    /// Computes a standard Ulrich Drepper SHA-512 crypt ($6$<salt>$<hash>) password digest for Linux/BSD installers.
    /// </summary>
    public static string HashPasswordSha512Crypt(string password, string? salt = null)
    {
        ArgumentNullException.ThrowIfNull(password);
        ValidatePasswordNoControlChars(password);

        if (string.IsNullOrEmpty(salt))
        {
            Span<byte> saltBytes = stackalloc byte[12];
            RandomNumberGenerator.Fill(saltBytes);
            var saltChars = new char[16];
            for (var i = 0; i < 16; i++)
            {
                saltChars[i] = CryptBase64Alphabet[saltBytes[i % 12] & 0x3F];
            }
            salt = new string(saltChars);
        }
        else if (salt.Length > 16)
        {
            salt = salt[..16];
        }

        var keyBytes = Encoding.UTF8.GetBytes(password);
        var saltUtf8 = Encoding.UTF8.GetBytes(salt);

        // Step 4-8: Alternate sum
        using var altSha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        altSha.AppendData(keyBytes);
        altSha.AppendData(saltUtf8);
        altSha.AppendData(keyBytes);
        var altResult = altSha.GetHashAndReset();

        // Step 1-3 & 9-12: Digest A
        using var shaA = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        shaA.AppendData(keyBytes);
        shaA.AppendData(saltUtf8);

        var cnt = keyBytes.Length;
        while (cnt > 64)
        {
            shaA.AppendData(altResult);
            cnt -= 64;
        }
        shaA.AppendData(altResult.AsSpan(0, cnt));

        for (cnt = keyBytes.Length; cnt > 0; cnt >>= 1)
        {
            shaA.AppendData((cnt & 1) != 0 ? altResult : keyBytes);
        }
        var digestA = shaA.GetHashAndReset();

        // Step 13-15: DP (password byte sequence)
        using var shaDp = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        for (var i = 0; i < keyBytes.Length; i++)
        {
            shaDp.AppendData(keyBytes);
        }
        var tempDp = shaDp.GetHashAndReset();
        var pBytes = ProduceSequence(tempDp, keyBytes.Length);

        // Step 17-19: DS (salt byte sequence)
        using var shaDs = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        for (var i = 0; i < 16 + digestA[0]; i++)
        {
            shaDs.AppendData(saltUtf8);
        }
        var tempDs = shaDs.GetHashAndReset();
        var sBytes = ProduceSequence(tempDs, saltUtf8.Length);

        // Step 21: 5000 rounds
        var current = digestA;
        using var roundSha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        for (var r = 0; r < 5000; r++)
        {
            roundSha.AppendData((r & 1) != 0 ? pBytes : current);
            if (r % 3 != 0)
            {
                roundSha.AppendData(sBytes);
            }
            if (r % 7 != 0)
            {
                roundSha.AppendData(pBytes);
            }
            roundSha.AppendData((r & 1) != 0 ? current : pBytes);
            current = roundSha.GetHashAndReset();
        }

        var sb = new StringBuilder(120);
        sb.Append("$6$").Append(salt).Append('$');

        int[][] groups =
        [
            [0, 21, 42], [22, 43, 1], [44, 2, 23], [3, 24, 45], [25, 46, 4],
            [47, 5, 26], [6, 27, 48], [28, 49, 7], [50, 8, 29], [9, 30, 51],
            [31, 52, 10], [53, 11, 32], [12, 33, 54], [34, 55, 13], [56, 14, 35],
            [15, 36, 57], [37, 58, 16], [59, 17, 38], [18, 39, 60], [40, 61, 19],
            [62, 20, 41]
        ];
        foreach (var g in groups)
        {
            B64From24Bit(sb, current[g[0]], current[g[1]], current[g[2]], 4);
        }
        B64From24Bit(sb, 0, 0, current[63], 2);

        return sb.ToString();
    }

    private static byte[] ProduceSequence(byte[] hash64, int length)
    {
        var result = new byte[length];
        var offset = 0;
        while (offset + 64 <= length)
        {
            Buffer.BlockCopy(hash64, 0, result, offset, 64);
            offset += 64;
        }
        if (offset < length)
        {
            Buffer.BlockCopy(hash64, 0, result, offset, length - offset);
        }
        return result;
    }

    private static void B64From24Bit(StringBuilder sb, byte b2, byte b1, byte b0, int n)
    {
        var w = ((uint)b2 << 16) | ((uint)b1 << 8) | b0;
        while (n-- > 0)
        {
            sb.Append(CryptBase64Alphabet[(int)(w & 0x3F)]);
            w >>= 6;
        }
    }

    private static void ValidatePasswordNoControlChars(string? transientPassword)
    {
        if (!string.IsNullOrEmpty(transientPassword) && transientPassword.Any(char.IsControl))
        {
            throw new ArgumentException("Password must not contain control characters.", nameof(transientPassword));
        }
    }

    private static string SanitizeIdentifier(string? input, string fallback)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return fallback;
        }

        var chars = input.Trim().Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray();
        return chars.Length == 0 ? fallback : new string(chars);
    }

    private static string SanitizeText(string? input, string fallback)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return fallback;
        }

        return input.Replace("\r", string.Empty).Replace("\n", " ").Trim();
    }
}
