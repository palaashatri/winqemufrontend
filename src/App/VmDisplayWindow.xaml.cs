using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using WinQemu.App.ViewModels;
using WinQemu.Core.Configuration;
using WinQemu.Core.Lifecycle;
using WinQemu.Display;
using WinQemu.Native.GuestDisplay;
using WinQemu.Qemu.Processes;

namespace WinQemu.App;

public sealed partial class VmDisplayWindow : Window
{
    private readonly VmCardItem _card;
    private readonly ManagedVmSession _session;
    private readonly RfbVncClient _vncClient = new();
    private WriteableBitmap? _bitmap;
    private bool _isFullscreen;
    private byte _pointerButtonMask;

    public VmDisplayWindow(VmCardItem card, ManagedVmSession session)
    {
        InitializeComponent();
        _card = card;
        _session = session;

        Title = $"{card.Name} — WinQEMU Display";
        TitleText.Text = $"{card.Name} ({card.ArchitectureBadge})";
        AccelBadgeText.Text = session.ActiveAccelerator.ToUpperInvariant();

        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1120, 760));
        }
        catch
        {
        }

        Closed += OnWindowClosed;
        Activated += OnWindowActivated;
        _ = ConnectVncAsync();
    }

    private async Task ConnectVncAsync()
    {
        if (_session.LaunchSpec.Endpoints.VncPort is not > 0)
        {
            OverlayStatusText.Text = "Headless or external display mode.";
            return;
        }

        _vncClient.FrameUpdated += OnVncFrameUpdated;
        try
        {
            await _vncClient.ConnectLocalhostAsync(_session.LaunchSpec.Endpoints.VncPort.Value);
            DispatcherQueue.TryEnqueue(() =>
            {
                OverlayStatusText.Visibility = Visibility.Collapsed;
                StatusFooterText.Text = $"Connected to 127.0.0.1:{_session.LaunchSpec.Endpoints.VncPort.Value} · Host key: {_card.Vm.Input.HostKeyShortcut}";
            });
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                OverlayStatusText.Text = $"Display connection error: {ex.Message}";
            });
        }
    }

    private void OnVncFrameUpdated(object? sender, FramebufferUpdateEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (_bitmap is null || _bitmap.PixelWidth != e.Width || _bitmap.PixelHeight != e.Height)
                {
                    _bitmap = new WriteableBitmap(e.Width, e.Height);
                    FramebufferImage.Source = _bitmap;
                }

                using var stream = _bitmap.PixelBuffer.AsStream();
                stream.Position = 0;
                stream.Write(e.BgraPixels, 0, Math.Min(e.BgraPixels.Length, e.Width * e.Height * 4));
                _bitmap.Invalidate();

                var metrics = _session.LastSampledMetrics ?? _session.SampleHostMetrics();
                MetricsFooterText.Text = $"{e.Width}×{e.Height} · Frame #{_vncClient.FramesReceived} · Host CPU {metrics.HostCpuUsagePercent:0.0}% · Working Set {metrics.HostWorkingSetBytes / (1024 * 1024)} MiB";
            }
            catch
            {
            }
        });
    }

    private async void OnPauseResumeClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_session.StateMachine.State == VmState.Paused)
            {
                await _session.ResumeAsync();
                _card.State = VmState.Running;
                PauseResumeBtn.Content = "⏸ Pause";
            }
            else
            {
                await _session.PauseAsync();
                _card.State = VmState.Paused;
                PauseResumeBtn.Content = "▶ Resume";
            }
        }
        catch
        {
        }
    }

    private async void OnRestartClick(object sender, RoutedEventArgs e)
    {
        try { await _session.RestartAsync(); } catch { }
    }

    private async void OnCtrlAltDelClick(object sender, RoutedEventArgs e)
    {
        try { await _vncClient.SendCtrlAltDeleteAsync(); } catch { }
    }

    private void OnScreenshotClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var pixels = _vncClient.SnapshotCurrentFramebuffer(out var w, out var h);
            if (w > 0 && h > 0 && !string.IsNullOrWhiteSpace(_card.Vm.DirectoryPath))
            {
                var shotPath = Path.Combine(
                    VmDirectoryLayout.GetScreenshotsDirectory(_card.Vm.DirectoryPath),
                    $"capture-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.png");
                FastFramebufferPresenter.SaveBgraAsPng(shotPath, pixels, w, h);
                _card.ScreenshotPath = shotPath;
                StatusFooterText.Text = $"Saved screenshot: {Path.GetFileName(shotPath)}";
            }
        }
        catch
        {
        }
    }

    private void OnFullscreenClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _isFullscreen = !_isFullscreen;
            AppWindow.SetPresenter(_isFullscreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
            FullscreenBtn.Content = _isFullscreen ? "❐ Exit Fullscreen" : "⛶ Fullscreen";
        }
        catch
        {
        }
    }

    private async void OnShutDownClick(object sender, RoutedEventArgs e)
    {
        try { await _session.ShutDownGracefullyAsync(); } catch { }
    }

    private async void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var keysym = RfbVncClient.MapWindowsVirtualKeyToX11Keysym((int)e.Key);
        await _vncClient.SendKeyEventAsync(keysym, isDown: true);
    }

    private async void OnRootKeyUp(object sender, KeyRoutedEventArgs e)
    {
        var keysym = RfbVncClient.MapWindowsVirtualKeyToX11Keysym((int)e.Key);
        await _vncClient.SendKeyEventAsync(keysym, isDown: false);
    }

    private async void OnRootLostFocus(object sender, RoutedEventArgs e)
    {
        try
        {
            await _vncClient.ReleaseAllPressedKeysAsync();
        }
        catch
        {
        }
    }

    private async void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            try
            {
                await _vncClient.ReleaseAllPressedKeysAsync();
            }
            catch
            {
            }
        }
    }

    private async void OnViewportPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        RootGrid.Focus(FocusState.Programmatic);
        var pt = e.GetCurrentPoint(FramebufferImage);
        _pointerButtonMask = 0;
        if (pt.Properties.IsLeftButtonPressed) _pointerButtonMask |= 1;
        if (pt.Properties.IsMiddleButtonPressed) _pointerButtonMask |= 2;
        if (pt.Properties.IsRightButtonPressed) _pointerButtonMask |= 4;
        await SendMappedPointerAsync(pt.Position.X, pt.Position.Y, _pointerButtonMask);
    }

    private async void OnViewportPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(FramebufferImage);
        await SendMappedPointerAsync(pt.Position.X, pt.Position.Y, _pointerButtonMask);
    }

    private async void OnViewportPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(FramebufferImage);
        _pointerButtonMask = 0;
        await SendMappedPointerAsync(pt.Position.X, pt.Position.Y, 0);
    }

    private async Task SendMappedPointerAsync(double viewX, double viewY, byte mask)
    {
        if (FramebufferImage.ActualWidth <= 0 || FramebufferImage.ActualHeight <= 0 ||
            _vncClient.FramebufferWidth <= 0 || _vncClient.FramebufferHeight <= 0)
        {
            return;
        }

        var gx = (int)Math.Clamp((viewX / FramebufferImage.ActualWidth) * _vncClient.FramebufferWidth, 0, _vncClient.FramebufferWidth - 1);
        var gy = (int)Math.Clamp((viewY / FramebufferImage.ActualHeight) * _vncClient.FramebufferHeight, 0, _vncClient.FramebufferHeight - 1);
        await _vncClient.SendPointerEventAsync(gx, gy, mask);
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        await _vncClient.DisposeAsync();
    }
}
