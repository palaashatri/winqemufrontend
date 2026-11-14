using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using Windows.UI;
using WinQemu.App.ViewModels;
using WinQemu.Core.Configuration;
using WinQemu.Core.Lifecycle;
using WinQemu.Core.Models;
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
    private bool _inputReleasedByHostKey;

    public VmDisplayWindow(VmCardItem card, ManagedVmSession session)
    {
        InitializeComponent();
        _card = card;
        _session = session;

        ConfigureBackdropAndTheme();

        Title = $"{card.Name} — WinQEMU Display";
        TitleText.Text = $"{card.Name} · {card.ArchitectureBadge}";

        var isWhpx = string.Equals(session.ActiveAccelerator, "whpx", StringComparison.OrdinalIgnoreCase);
        AccelBadgeText.Text = isWhpx ? "WHPX ACCELERATED" : "TCG EMULATION";
        AccelBadgeIcon.Glyph = isWhpx ? "\uE945" : "\uE964";
        AccelBadgeBorder.Background = new SolidColorBrush(isWhpx
            ? Color.FromArgb(255, 0x23, 0x86, 0x36)
            : Color.FromArgb(255, 0x1F, 0x6F, 0xEB));
        AccelBadgeBorder.BorderBrush = new SolidColorBrush(isWhpx
            ? Color.FromArgb(255, 0x2E, 0xA0, 0x43)
            : Color.FromArgb(255, 0x38, 0x8B, 0xFD));

        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1140, 780));
        }
        catch
        {
        }

        Closed += OnWindowClosed;
        Activated += OnWindowActivated;
        _ = ConnectVncAsync();
    }

    private void ConfigureBackdropAndTheme()
    {
        try
        {
            if (MicaController.IsSupported())
            {
                SystemBackdrop = new MicaBackdrop();
            }
            else if (DesktopAcrylicController.IsSupported())
            {
                SystemBackdrop = new DesktopAcrylicBackdrop();
            }
        }
        catch
        {
        }

        if (App.CurrentMainWindow?.Content is FrameworkElement mainRoot)
        {
            RootGrid.RequestedTheme = mainRoot.RequestedTheme;
        }
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 960;
        Grid.SetRow(DisplayActionsScroller, narrow ? 1 : 0);
        Grid.SetColumn(DisplayActionsScroller, narrow ? 0 : 1);
        Grid.SetColumnSpan(DisplayActionsScroller, narrow ? 2 : 1);
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
                _inputReleasedByHostKey = false;
                var focused = ViewportFocusSink.Focus(FocusState.Programmatic);
                UpdateViewportCaptureVisualState(focused || ViewportFocusSink.FocusState != FocusState.Unfocused);
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

                var metrics = _session.SampleHostMetrics();
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
                PauseResumeIcon.Glyph = "\uE769";
                PauseResumeText.Text = "Pause";
            }
            else
            {
                await _session.PauseAsync();
                _card.State = VmState.Paused;
                PauseResumeIcon.Glyph = "\uE768";
                PauseResumeText.Text = "Resume";
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
            FullscreenIcon.Glyph = _isFullscreen ? "\uE73F" : "\uE740";
            FullscreenText.Text = _isFullscreen ? "Exit Fullscreen" : "Fullscreen";
        }
        catch
        {
        }
    }

    private async void OnShutDownClick(object sender, RoutedEventArgs e)
    {
        try { await _session.ShutDownGracefullyAsync(); } catch { }
    }

    private async void OnViewportPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        await HandleViewportPreviewKeyAsync(e, isDown: true);
    }

    private async void OnViewportPreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        await HandleViewportPreviewKeyAsync(e, isDown: false);
    }

    private async Task HandleViewportPreviewKeyAsync(KeyRoutedEventArgs e, bool isDown)
    {
        if (!_vncClient.IsConnected)
        {
            return;
        }

        var shiftPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var ctrlPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var menuPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (ctrlPressed && menuPressed && e.Key != VirtualKey.Delete)
        {
            e.Handled = true;
            _inputReleasedByHostKey = true;
            try
            {
                await _vncClient.ReleaseAllPressedKeysAsync();
            }
            catch
            {
            }

            PauseResumeBtn.Focus(FocusState.Programmatic);
            StatusFooterText.Text = "Input released to host (Click guest display to capture keyboard & mouse)";
            return;
        }

        e.Handled = true;
        var keysym = RfbVncClient.MapWindowsVirtualKeyToX11Keysym((int)e.Key, shiftPressed);
        try
        {
            await _vncClient.SendKeyEventAsync(keysym, isDown);
        }
        catch
        {
        }
    }

    private void OnViewportGotFocus(object sender, RoutedEventArgs e)
    {
        _inputReleasedByHostKey = false;
        UpdateViewportCaptureVisualState(hasFocus: true);
    }

    private async void OnViewportLostFocus(object sender, RoutedEventArgs e)
    {
        _pointerButtonMask = 0;
        UpdateViewportCaptureVisualState(hasFocus: false, releasedByHostKey: _inputReleasedByHostKey);
        if (!_vncClient.IsConnected)
        {
            return;
        }

        try
        {
            await _vncClient.ReleaseAllPressedKeysAsync();
        }
        catch
        {
        }
    }

    private void UpdateViewportCaptureVisualState(bool hasFocus, bool releasedByHostKey = false)
    {
        ViewportBorder.BorderBrush = hasFocus
            ? MainWindow.ResolveThemeBrush(ViewportBorder, "AccentFillColorDefaultBrush", Color.FromArgb(255, 0x00, 0x78, 0xD4))
            : MainWindow.ResolveThemeBrush(ViewportBorder, "CardStrokeColorDefaultBrush", Color.FromArgb(255, 0x30, 0x36, 0x3D));

        var vncPort = _session.LaunchSpec.Endpoints.VncPort;
        var endpointPrefix = _vncClient.IsConnected && vncPort is > 0
            ? $"Connected to 127.0.0.1:{vncPort.Value}"
            : "Display active";

        if (hasFocus)
        {
            StatusFooterText.Text = $"{endpointPrefix} · Keyboard & Mouse Captured ({_card.Vm.Input.HostKeyShortcut} to release)";
        }
        else if (releasedByHostKey)
        {
            StatusFooterText.Text = "Input released to host (Click guest display to capture keyboard & mouse)";
        }
        else
        {
            StatusFooterText.Text = $"{endpointPrefix} · Click inside guest display to type";
        }
    }

    private async void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            _pointerButtonMask = 0;
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
        _inputReleasedByHostKey = false;
        ViewportFocusSink.Focus(FocusState.Programmatic);
        UpdateViewportCaptureVisualState(ViewportFocusSink.FocusState != FocusState.Unfocused);
        if (!_vncClient.IsConnected)
        {
            return;
        }

        var pt = e.GetCurrentPoint(ViewportGrid);
        _pointerButtonMask = 0;
        if (pt.Properties.IsLeftButtonPressed) _pointerButtonMask |= 1;
        if (pt.Properties.IsMiddleButtonPressed) _pointerButtonMask |= 2;
        if (pt.Properties.IsRightButtonPressed) _pointerButtonMask |= 4;
        await SendMappedPointerAsync(e, _pointerButtonMask, updateFooterStatus: true);
    }

    private async void OnViewportPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_vncClient.IsConnected)
        {
            return;
        }

        await SendMappedPointerAsync(e, _pointerButtonMask, updateFooterStatus: _pointerButtonMask != 0);
    }

    private async void OnViewportPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_vncClient.IsConnected)
        {
            _pointerButtonMask = 0;
            return;
        }

        var pt = e.GetCurrentPoint(ViewportGrid);
        byte nextMask = 0;
        if (pt.Properties.IsLeftButtonPressed) nextMask |= 1;
        if (pt.Properties.IsMiddleButtonPressed) nextMask |= 2;
        if (pt.Properties.IsRightButtonPressed) nextMask |= 4;
        _pointerButtonMask = nextMask;
        await SendMappedPointerAsync(e, _pointerButtonMask, updateFooterStatus: false);
    }

    private async Task SendMappedPointerAsync(PointerRoutedEventArgs e, byte mask, bool updateFooterStatus = false)
    {
        if (!_vncClient.IsConnected)
        {
            return;
        }

        var mapped = MainWindow.TryMapViewportPointer(
            e,
            ViewportGrid,
            FramebufferImage,
            _vncClient.FramebufferWidth,
            _vncClient.FramebufferHeight,
            VmDisplayScalingMode.FitAspectRatio);
        if (mapped is null)
        {
            return;
        }

        var (gx, gy) = mapped.Value;
        if (updateFooterStatus && _session.LaunchSpec.Endpoints.VncPort is { } port)
        {
            StatusFooterText.Text = $"Connected to 127.0.0.1:{port} · Captured ({gx},{gy}) · {_card.Vm.Input.HostKeyShortcut} to release";
        }

        try
        {
            await _vncClient.SendPointerEventAsync(gx, gy, mask);
        }
        catch
        {
        }
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _vncClient.FrameUpdated -= OnVncFrameUpdated;
        await _vncClient.DisposeAsync();
    }
}
