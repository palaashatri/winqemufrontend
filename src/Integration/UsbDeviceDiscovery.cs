using WinQemu.Core.Models;

namespace WinQemu.Integration;

/// <summary>
/// Discovers USB devices and enforces host-critical device protection (§35) so users
/// never accidentally capture their primary keyboard, mouse, or boot storage without warning.
/// </summary>
public static class UsbDeviceDiscovery
{
    public static IReadOnlyList<AttachedUsbDevice> GetDefaultVirtualAndHostDevices()
    {
        return
        [
            new AttachedUsbDevice
            {
                VendorId = "0x0951",
                ProductId = "0x1666",
                FriendlyName = "Kingston DataTraveler USB 3.2 Flash Drive",
                IsHostCritical = false,
                Attached = false
            },
            new AttachedUsbDevice
            {
                VendorId = "0x1050",
                ProductId = "0x0407",
                FriendlyName = "Yubico YubiKey OTP+FIDO+CCID Security Key",
                IsHostCritical = false,
                Attached = false
            },
            new AttachedUsbDevice
            {
                VendorId = "0x046d",
                ProductId = "0xc52b",
                FriendlyName = "Host Primary USB Keyboard & Mouse Receiver (Protected)",
                IsHostCritical = true,
                Attached = false
            },
            new AttachedUsbDevice
            {
                VendorId = "0x0781",
                ProductId = "0x5583",
                FriendlyName = "Host External NVMe Storage Enclosure (Requires Confirmation)",
                IsHostCritical = true,
                Attached = false
            }
        ];
    }

    public static string? GetCaptureWarningIfCritical(AttachedUsbDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!device.IsHostCritical)
        {
            return null;
        }

        return $"'{device.FriendlyName}' ({device.VendorId}:{device.ProductId}) is marked as a host-critical input or storage device (§35). Capturing it may disconnect input or storage from Windows.";
    }
}
