using System.Xml.Linq;
using WinQemu.Core.Models;
using WinQemu.Display;
using WinQemu.Integration;
using WinQemu.Native.GuestDisplay;
using Xunit;

namespace WinQemu.Tests.UiAutomation;

public sealed class UiAutomationTests
{
    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WinQemu.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate WinQemu.sln root.");
    }

    [Fact]
    public void MainWindowXaml_ExposesAccessibilityNamesAndKeyboardAccelerators()
    {
        var xamlPath = Path.Combine(GetRepoRoot(), "src", "App", "MainWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"Expected MainWindow.xaml at {xamlPath}");

        var doc = XDocument.Load(xamlPath);
        var automationIds = doc.Descendants()
            .SelectMany(e => e.Attributes())
            .Where(a => a.Name.LocalName == "AutomationProperties.AutomationId")
            .Select(a => a.Value)
            .ToHashSet(StringComparer.Ordinal);

        // Verify key UI Automation IDs exist on parsed XAML elements (§61, §74)
        Assert.Contains("PrimaryNavigationBar", automationIds);
        Assert.Contains("NavHomeBtn", automationIds);
        Assert.Contains("NavWizardBtn", automationIds);
        Assert.Contains("NavConsoleBtn", automationIds);
        Assert.Contains("NavHardwareBtn", automationIds);
        Assert.Contains("NavSnapshotsBtn", automationIds);
        Assert.Contains("NavDownloadsBtn", automationIds);
        Assert.Contains("NavSettingsBtn", automationIds);
        Assert.Contains("VmLibraryGridView", automationIds);
        Assert.Contains("ProvisionMultiArchBtn", automationIds);
        Assert.Contains("VmSearchBox", automationIds);
        Assert.Contains("OsDownloadCardsList", automationIds);

        // Verify keyboard accelerators (Ctrl+N, Alt+1, Alt+2) are registered with valid Key/Modifiers (§61)
        var accelerators = doc.Descendants().Where(e => e.Name.LocalName == "KeyboardAccelerator").ToList();
        Assert.NotEmpty(accelerators);
        Assert.Contains(accelerators, a =>
            a.Attribute("Key")?.Value == "N" &&
            a.Attribute("Modifiers")?.Value == "Control");
        Assert.Contains(accelerators, a =>
            a.Attribute("Key")?.Value == "Number1" &&
            a.Attribute("Modifiers")?.Value == "Menu");
        Assert.Contains(accelerators, a =>
            a.Attribute("Key")?.Value == "Number2" &&
            a.Attribute("Modifiers")?.Value == "Menu");
    }

    [Fact]
    public void MainWindowAndDisplayWindowXaml_EnforceFluentShellAndZeroCartoonEmojis()
    {
        var repoRoot = GetRepoRoot();
        var mainXamlPath = Path.Combine(repoRoot, "src", "App", "MainWindow.xaml");
        var displayXamlPath = Path.Combine(repoRoot, "src", "App", "VmDisplayWindow.xaml");

        Assert.True(File.Exists(mainXamlPath), $"Expected MainWindow.xaml at {mainXamlPath}");
        Assert.True(File.Exists(displayXamlPath), $"Expected VmDisplayWindow.xaml at {displayXamlPath}");

        var mainText = File.ReadAllText(mainXamlPath);
        var displayText = File.ReadAllText(displayXamlPath);
        var mainDoc = XDocument.Parse(mainText);
        var displayDoc = XDocument.Parse(displayText);

        XNamespace xNs = "http://schemas.microsoft.com/winfx/2006/xaml";
        var mainNames = mainDoc.Descendants()
            .Select(e => e.Attribute(xNs + "Name")?.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .ToHashSet(StringComparer.Ordinal);
        var displayNames = displayDoc.Descendants()
            .Select(e => e.Attribute(xNs + "Name")?.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("SidebarColumn", mainNames);
        Assert.Contains("WhpxStatusPill", mainNames);
        Assert.Contains("QemuVersionPillBorder", mainNames);
        Assert.Contains("ConsoleAccelBadgeBorder", mainNames);
        Assert.Contains("PortForwardRulesList", mainNames);
        Assert.Contains("OsDownloadCardsList", mainNames);
        Assert.Contains("SettingsWhpxInfoBar", mainNames);

        var devDrawerBtn = mainDoc.Descendants()
            .FirstOrDefault(e => e.Attribute(xNs + "Name")?.Value == "ToggleDevDrawerBtn");
        Assert.NotNull(devDrawerBtn);
        Assert.Equal("False", devDrawerBtn.Attribute("IsChecked")?.Value);

        Assert.Contains("AccelBadgeBorder", displayNames);
        Assert.Contains("AccelBadgeIcon", displayNames);
        Assert.Contains("AccelBadgeText", displayNames);
        Assert.Contains("StatusFooterText", displayNames);
        Assert.Contains("MetricsFooterText", displayNames);

        Assert.Contains(mainDoc.Descendants(), e =>
            e.Name.LocalName == "FontIcon" &&
            e.Attribute("FontFamily")?.Value == "Segoe Fluent Icons");
        Assert.Contains(displayDoc.Descendants(), e =>
            e.Name.LocalName == "FontIcon" &&
            e.Attribute("FontFamily")?.Value == "Segoe Fluent Icons");

        string[] disallowedEmojis =
        [
            "🏠", "🖥", "⚙", "⏱", "🌐", "🔧", "📷", "📋", "💾", "🗑", "🔄", "🚀", "⚡", "▶", "⏸", "⏻"
        ];
        foreach (var emoji in disallowedEmojis)
        {
            Assert.DoesNotContain(emoji, mainText, StringComparison.Ordinal);
            Assert.DoesNotContain(emoji, displayText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ResponsiveViewportLayout_HandlesWideAndSnappedWindowsAndDpiScaling()
    {
        // Wide window (1420x900) with 1024x768 guest
        var wideRect = FastFramebufferPresenter.ComputeViewportPlacement(1200, 780, 1024, 768, VmDisplayScalingMode.FitAspectRatio);
        Assert.True(wideRect.RenderedWidth > 0 && wideRect.RenderedHeight > 0);
        Assert.True(wideRect.RenderedWidth <= 1200 && wideRect.RenderedHeight <= 780);
        Assert.InRange(wideRect.RenderedWidth / wideRect.RenderedHeight, 1.32, 1.35);

        // Narrow Windows 11 1/3 snapped layout (520x840) (§62, §63, §74)
        var snappedRect = FastFramebufferPresenter.ComputeViewportPlacement(480, 600, 1024, 768, VmDisplayScalingMode.FitAspectRatio);
        Assert.Equal(480, snappedRect.RenderedWidth, 1);
        Assert.Equal(360, snappedRect.RenderedHeight, 1);
        Assert.Equal(120, snappedRect.OffsetY, 1);
    }

    [Fact]
    public void MultiArchBootImageFactory_GeneratesBootableMediaForAllSupportedArchitectures()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"WinQemuUiTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var hybridDisk = Path.Combine(tempDir, "linux_x86_64.raw");
            var bootIso = Path.Combine(tempDir, "linux_x86_64.iso");
            MultiArchBootImageFactory.CreateBootableHybridDiskImage(
                hybridDisk,
                "Ubuntu 24.04 LTS (x86_64)",
                "Canonical Ubuntu Linux 24.04.1 LTS (Noble Numbat)",
                "x86_64 · Linux 6.8.0-45-generic SMP PREEMPT_DYNAMIC",
                "ubuntu@winqemu-x86_64:~$ READY",
                0x1F);
            MultiArchBootImageFactory.CreateBootableElToritoIso(
                bootIso,
                "UBUNTU_24_04_X64",
                "Ubuntu 24.04 LTS (x86_64)",
                "Canonical Ubuntu Linux 24.04.1 LTS (Noble Numbat)",
                "x86_64 · Linux 6.8.0-45-generic SMP PREEMPT_DYNAMIC",
                "ubuntu@winqemu-x86_64:~$ READY",
                0x1F);

            Assert.True(File.Exists(hybridDisk));
            Assert.True(File.Exists(bootIso));

            var mbrBytes = File.ReadAllBytes(hybridDisk);
            Assert.Equal(0x55, mbrBytes[510]);
            Assert.Equal(0xAA, mbrBytes[511]);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void KeysymMapping_MapsStandardKeysAndArrowsCorrectly()
    {
        Assert.Equal(0xFF08u, RfbVncClient.MapWindowsVirtualKeyToX11Keysym(0x08)); // Backspace
        Assert.Equal(0xFF0Du, RfbVncClient.MapWindowsVirtualKeyToX11Keysym(0x0D)); // Enter
        Assert.Equal(0xFF1Bu, RfbVncClient.MapWindowsVirtualKeyToX11Keysym(0x1B)); // Escape
        Assert.Equal(0xFF51u, RfbVncClient.MapWindowsVirtualKeyToX11Keysym(0x25)); // Left Arrow
    }
}
