using System.Security;
using System.Text;
using WinQemu.Core.Models;

namespace WinQemu.Core.Presets;

/// <summary>
/// Generates unattended installation answer files (Windows Autounattend.xml, Cloud-Init user-data/meta-data,
/// Fedora Kickstart, Debian Preseed) without persisting plaintext passwords in application logs (§37).
/// </summary>
public static class UnattendedInstallGenerator
{
    public static (string FileName, string Content) GenerateCloudInitFiles(
        UnattendedInstallConfig config,
        string? transientPassword = null)
    {
        ArgumentNullException.ThrowIfNull(config);

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
            sb.AppendLine("chpasswd:");
            sb.AppendLine("  expire: false");
            sb.AppendLine("  list: |");
            sb.AppendLine($"    {SanitizeIdentifier(config.Username, "user")}:{transientPassword}");
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

    public static string GenerateWindowsAutounattendXml(
        UnattendedInstallConfig config,
        string? transientPassword = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var escapedUser = SecurityElement.Escape(SanitizeIdentifier(config.Username, "User")) ?? "User";
        var escapedFullName = SecurityElement.Escape(SanitizeText(config.FullName, "Windows User")) ?? "Windows User";
        var escapedHost = SecurityElement.Escape(SanitizeIdentifier(config.Hostname, "WINQEMU-PC")) ?? "WINQEMU-PC";
        var escapedPassword = SecurityElement.Escape(transientPassword ?? string.Empty) ?? string.Empty;

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
                          <Value>{escapedPassword}</Value>
                          <PlainText>true</PlainText>
                        </Password>
                      </LocalAccount>
                    </LocalAccounts>
                  </UserAccounts>
                </component>
              </settings>
            </unattend>
            """;
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
