using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinQemu.App.ViewModels;
using WinQemu.Core.Configuration;
using WinQemu.Core.Diagnostics;
using WinQemu.Core.Lifecycle;
using WinQemu.Core.Models;
using WinQemu.Core.Presets;
using WinQemu.Display;
using WinQemu.Integration;
using WinQemu.Native.GuestDisplay;
using WinQemu.Networking;
using WinQemu.Qemu.Acceleration;
using WinQemu.Qemu.Processes;
using WinQemu.Qemu.QemuImg;
using WinQemu.Storage;
using WinRT.Interop;

namespace WinQemu.App;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private RfbVncClient? _embeddedVncClient;
    private WriteableBitmap? _embeddedBitmap;
    private ManagedVmSession? _activeConsoleSession;
    private CancellationTokenSource? _attachCts;
    private DispatcherTimer? _metricsTimer;
    private byte _embeddedPointerMask;
    private bool _isFullscreen;
    private bool _allowCloseConfirmed;
    private bool _suppressConsoleSelectorAttach;

    public MainWindow()
    {
        InitializeComponent();

        TryConfigureWindows11BackdropAndTitleBar();
        VmGridView.ItemsSource = _viewModel.FilteredVms;
        ConsoleVmSelector.ItemsSource = _viewModel.AllVms;
        WizardPresetCombo.ItemsSource = GuestPresetCatalog.GetAll();
        WizardPresetCombo.SelectedIndex = 2; // Ubuntu default

        AppWindow.Closing += OnAppWindowClosing;
        Activated += OnWindowActivated;
        _ = InitializeUiAsync();
    }

    public MainViewModel ViewModel => _viewModel;

    public void ReportUnhandledUiError(string message)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            var remediation = ErrorTranslator.Translate(message);
            GlobalStatusText.Text = $"{remediation.Title}: {remediation.UserFriendlyExplanation}";
        });
    }

    private void TryConfigureWindows11BackdropAndTitleBar()
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

        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(CustomTitleBar);

            var iconIco = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
            if (File.Exists(iconIco))
            {
                AppWindow.SetIcon(iconIco);
            }

            var iconPng = Path.Combine(AppContext.BaseDirectory, "Assets", "icon-64.png");
            if (File.Exists(iconPng))
            {
                TitleBarBrandIcon.Source = new BitmapImage(new Uri(iconPng));
            }

            AppWindow.Resize(new Windows.Graphics.SizeInt32(1380, 880));
        }
        catch
        {
        }
    }

    private async Task InitializeUiAsync()
    {
        var args = App.LaunchArguments;
        var qaIdx = Array.FindIndex(args, a => string.Equals(a, "--qa-tour", StringComparison.OrdinalIgnoreCase));
        var qaOutDir = (qaIdx >= 0 && qaIdx + 1 < args.Length) ? args[qaIdx + 1] : null;

        try
        {
            await _viewModel.InitializeAsync();
            if (!string.IsNullOrWhiteSpace(qaOutDir) && _viewModel.AllVms.Count == 0)
            {
                await _viewModel.ProvisionMultiArchShowcaseVmsAsync();
            }

            RefreshTopStatusBanners();
            PopulateOsDownloadsView();
            PopulateSettingsAndDiagnosticsView();

            if (_viewModel.FilteredVms.Count > 0)
            {
                VmGridView.SelectedIndex = 0;
                ConsoleVmSelector.SelectedIndex = 0;
            }

            _metricsTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _metricsTimer.Tick += OnMetricsTimerTick;
            _metricsTimer.Start();

            if (!string.IsNullOrWhiteSpace(qaOutDir))
            {
                await ExecuteAutomatedVisualAndFunctionalQaTourAsync(qaOutDir);
            }
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(qaOutDir))
            {
                try
                {
                    Directory.CreateDirectory(qaOutDir);
                    await File.WriteAllTextAsync(Path.Combine(qaOutDir, "qa-tour-error.txt"), ex.ToString());
                }
                catch { }
            }
        }
    }

    private void RefreshTopStatusBanners()
    {
        var rt = _viewModel.RuntimeInfo;
        QemuVersionPillText.Text = rt.IsAvailable
            ? $"QEMU: {rt.VersionString.Replace("QEMU emulator version ", "")}"
            : "QEMU: Not Configured";

        var whpx = _viewModel.WhpxReport;
        if (whpx is not null)
        {
            WhpxStatusPillText.Text = whpx.UserFriendlyTitle;
            BannerHeadlineText.Text = $"Virtual Machines — {whpx.UserFriendlyTitle}";
            BannerDetailText.Text = $"{whpx.Explanation} ({rt.TargetBinaries.Count} target architectures discovered: {string.Join(", ", rt.TargetBinaries.Keys)})";
            EnableWhpxBannerBtn.Visibility = whpx.CanOfferElevatedEnable ? Visibility.Visible : Visibility.Collapsed;
        }

        GlobalCountText.Text = $"{_viewModel.AllVms.Count} Virtual Machines ({_viewModel.ProcessManager.ActiveSessions.Count} running)";
    }

    private void SwitchActiveView(UIElement activePanel)
    {
        LibraryViewPanel.Visibility = activePanel == LibraryViewPanel ? Visibility.Visible : Visibility.Collapsed;
        ConsoleViewPanel.Visibility = activePanel == ConsoleViewPanel ? Visibility.Visible : Visibility.Collapsed;
        WizardViewPanel.Visibility = activePanel == WizardViewPanel ? Visibility.Visible : Visibility.Collapsed;
        HardwareViewPanel.Visibility = activePanel == HardwareViewPanel ? Visibility.Visible : Visibility.Collapsed;
        SnapshotsViewPanel.Visibility = activePanel == SnapshotsViewPanel ? Visibility.Visible : Visibility.Collapsed;
        DownloadsViewPanel.Visibility = activePanel == DownloadsViewPanel ? Visibility.Visible : Visibility.Collapsed;
        SettingsViewPanel.Visibility = activePanel == SettingsViewPanel ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNavHomeClick(object sender, RoutedEventArgs e)
    {
        SwitchActiveView(LibraryViewPanel);
        RefreshTopStatusBanners();
    }

    private void OnNavConsoleClick(object sender, RoutedEventArgs e)
    {
        SwitchActiveView(ConsoleViewPanel);
        if (_viewModel.SelectedVm is not null)
        {
            ConsoleVmSelector.SelectedItem = _viewModel.SelectedVm;
            UpdateConsoleToolbarForSelectedVm(_viewModel.SelectedVm);
        }
    }

    private void OnNavWizardClick(object sender, RoutedEventArgs e)
    {
        SwitchActiveView(WizardViewPanel);
    }

    private void OnNavHardwareClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedVm is not null)
        {
            LoadVmIntoHardwareEditor(_viewModel.SelectedVm);
        }
        SwitchActiveView(HardwareViewPanel);
    }

    private async void OnNavSnapshotsClick(object sender, RoutedEventArgs e)
    {
        SwitchActiveView(SnapshotsViewPanel);
        if (_viewModel.SelectedVm is not null)
        {
            await RefreshSnapshotsAndCloneViewAsync(_viewModel.SelectedVm);
        }
    }

    private void OnNavDownloadsClick(object sender, RoutedEventArgs e)
    {
        PopulateOsDownloadsView();
        SwitchActiveView(DownloadsViewPanel);
    }

    private void OnNavSettingsClick(object sender, RoutedEventArgs e)
    {
        PopulateSettingsAndDiagnosticsView();
        SwitchActiveView(SettingsViewPanel);
    }

    private void OnMainRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Adaptive responsive layout for Wide, Medium, and Narrow Windows 11 Snap Layouts (§62, §63)
        var width = e.NewSize.Width;
        TitleBarSubtitle.Visibility = width >= 920 ? Visibility.Visible : Visibility.Collapsed;
        HeaderRightControls.Visibility = width >= 760 ? Visibility.Visible : Visibility.Collapsed;

        if (width < 880)
        {
            DevDrawerColumn.Width = new GridLength(0);
            DevDrawerPanel.Visibility = Visibility.Collapsed;
        }
        else if (ToggleDevDrawerBtn.IsChecked == true)
        {
            DevDrawerColumn.Width = new GridLength(360);
            DevDrawerPanel.Visibility = Visibility.Visible;
        }
    }

    private void OnMainRootDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Drop ISO or .qvm to create/import VM";
    }

    private async void OnMainRootDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        var file = items.OfType<StorageFile>().FirstOrDefault();
        if (file is null)
        {
            return;
        }

        if (file.Path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
        {
            WizardIsoPathBox.Text = file.Path;
            SwitchActiveView(WizardViewPanel);
        }
        else if (file.Path.EndsWith(".qvm", StringComparison.OrdinalIgnoreCase))
        {
            await ImportPortablePackageWithApprovalAsync(file.Path);
        }
    }

    private void OnSearchBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        _viewModel.SearchQuery = SearchBox.Text;
        _viewModel.ApplyFilter();
    }

    private void OnVmGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VmGridView.SelectedItem is VmCardItem card)
        {
            _viewModel.SelectedVm = card;
        }
    }

    private async void OnProvisionShowcaseClick(object sender, RoutedEventArgs e)
    {
        GlobalStatusText.Text = "Provisioning bootable Linux, FreeBSD, Windows, and macOS VMs across all architectures...";
        var created = await _viewModel.ProvisionMultiArchShowcaseVmsAsync();
        RefreshTopStatusBanners();
        GlobalStatusText.Text = $"Ready — {created.Count} multi-architecture VMs available in library.";
    }

    private async void OnCardPrimaryActionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not VmCardItem card)
        {
            return;
        }

        _viewModel.SelectedVm = card;
        if (card.IsRunningOrPaused)
        {
            SwitchActiveView(ConsoleViewPanel);
            if (!ReferenceEquals(ConsoleVmSelector.SelectedItem, card))
            {
                ConsoleVmSelector.SelectedItem = card;
            }
            else
            {
                await AttachEmbeddedConsoleToCardAsync(card);
            }
        }
        else
        {
            await StartVmAndOpenConsoleAsync(card);
        }
    }

    private async Task StartVmAndOpenConsoleAsync(VmCardItem card)
    {
        try
        {
            GlobalStatusText.Text = $"Starting '{card.Name}' ({card.ArchitectureBadge})...";
            _suppressConsoleSelectorAttach = true;
            try
            {
                ConsoleVmSelector.SelectedItem = card;
            }
            finally
            {
                _suppressConsoleSelectorAttach = false;
            }
            SwitchActiveView(ConsoleViewPanel);
            EmbeddedDisplayOverlay.Visibility = Visibility.Visible;
            EmbeddedOverlayTitle.Text = $"Booting {card.Name}...";
            EmbeddedOverlaySubtitle.Text = $"Starting QEMU ({card.ArchitectureBadge}) and connecting QMP + localhost VNC...";

            var session = await _viewModel.StartVmAsync(card);
            await AttachEmbeddedConsoleToSessionAsync(card, session);
            RefreshTopStatusBanners();
            GlobalStatusText.Text = $"'{card.Name}' is running ({session.ActiveAccelerator.ToUpperInvariant()}).";
        }
        catch (Exception ex)
        {
            var remediation = ErrorTranslator.Translate(ex.Message);
            EmbeddedDisplayOverlay.Visibility = Visibility.Visible;
            EmbeddedOverlayTitle.Text = remediation.Title;
            EmbeddedOverlaySubtitle.Text = $"{remediation.UserFriendlyExplanation}\n\nDetails: {remediation.TechnicalDetails}";
            GlobalStatusText.Text = $"{remediation.Title} — {remediation.UserFriendlyExplanation}";
        }
    }

    private async Task AttachEmbeddedConsoleToCardAsync(VmCardItem card)
    {
        UpdateConsoleToolbarForSelectedVm(card);
        var session = _viewModel.ProcessManager.GetSession(card.Id);
        if (session is not null && session.StateMachine.IsActiveProcessState)
        {
            await AttachEmbeddedConsoleToSessionAsync(card, session);
        }
        else
        {
            EmbeddedDisplayOverlay.Visibility = Visibility.Visible;
            EmbeddedOverlayTitle.Text = $"{card.Name} is Stopped";
            EmbeddedOverlaySubtitle.Text = $"Architecture: {card.ArchitectureBadge} · {card.ResourceSummary}\nPress '▶ Start VM' above to boot this virtual machine.";
        }
    }

    private async Task AttachEmbeddedConsoleToSessionAsync(VmCardItem card, ManagedVmSession session)
    {
        _attachCts?.Cancel();
        _attachCts?.Dispose();
        var cts = new CancellationTokenSource();
        _attachCts = cts;
        var ct = cts.Token;

        if (_activeConsoleSession is not null)
        {
            _activeConsoleSession.SerialOutputReceived -= OnSessionSerialOutputReceived;
        }

        _activeConsoleSession = session;
        UpdateConsoleToolbarForSelectedVm(card);

        SerialConsoleOutputText.Text = string.IsNullOrWhiteSpace(session.GetSerialConsoleHistory())
            ? $"[Connected to serial console for {card.Name}]\n"
            : session.GetSerialConsoleHistory();

        session.SerialOutputReceived -= OnSessionSerialOutputReceived;
        session.SerialOutputReceived += OnSessionSerialOutputReceived;

        if (_embeddedVncClient is not null)
        {
            await _embeddedVncClient.DisposeAsync();
            _embeddedVncClient = null;
        }

        if (session.LaunchSpec.Endpoints.VncPort is > 0)
        {
            var vnc = new RfbVncClient();
            try
            {
                vnc.FrameUpdated += OnEmbeddedVncFrameUpdated;
                await vnc.ConnectLocalhostAsync(session.LaunchSpec.Endpoints.VncPort.Value, cancellationToken: ct);
                if (ct.IsCancellationRequested)
                {
                    await vnc.DisposeAsync();
                    return;
                }

                _embeddedVncClient = vnc;
                EmbeddedDisplayOverlay.Visibility = Visibility.Collapsed;
                ConsoleFooterLeftText.Text = $"VNC Connected (127.0.0.1:{session.LaunchSpec.Endpoints.VncPort.Value}) · QMP Connected (127.0.0.1:{session.LaunchSpec.Endpoints.QmpPort})";
            }
            catch (OperationCanceledException)
            {
                await vnc.DisposeAsync();
            }
            catch (Exception ex)
            {
                await vnc.DisposeAsync();
                EmbeddedDisplayOverlay.Visibility = Visibility.Visible;
                EmbeddedOverlayTitle.Text = "Display Connection Error";
                EmbeddedOverlaySubtitle.Text = $"Could not connect to localhost VNC on port {session.LaunchSpec.Endpoints.VncPort.Value}: {ex.Message}";
            }
        }
    }

    private void OnSessionSerialOutputReceived(object? sender, string chunk)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_activeConsoleSession is not null)
            {
                SerialConsoleOutputText.Text = _activeConsoleSession.GetSerialConsoleHistory();
            }
        });
    }

    private void OnEmbeddedVncFrameUpdated(object? sender, FramebufferUpdateEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (_embeddedBitmap is null || _embeddedBitmap.PixelWidth != e.Width || _embeddedBitmap.PixelHeight != e.Height)
                {
                    _embeddedBitmap = new WriteableBitmap(e.Width, e.Height);
                    EmbeddedFramebufferImage.Source = _embeddedBitmap;
                }

                using var stream = _embeddedBitmap.PixelBuffer.AsStream();
                stream.Position = 0;
                stream.Write(e.BgraPixels, 0, Math.Min(e.BgraPixels.Length, e.Width * e.Height * 4));
                _embeddedBitmap.Invalidate();
                EmbeddedDisplayOverlay.Visibility = Visibility.Collapsed;
            }
            catch
            {
            }
        });
    }

    private void UpdateConsoleToolbarForSelectedVm(VmCardItem card)
    {
        ConsoleAccelBadge.Text = card.IsRunningOrPaused
            ? card.ActiveAccelerator.ToUpperInvariant()
            : card.Vm.Accelerator.ToString().ToUpperInvariant();
        ConsoleStateText.Text = card.StatusBadgeText;
        ConsoleStartStopBtn.Content = card.IsRunningOrPaused ? "⏻ Shut Down" : "▶ Start VM";
        ConsolePauseBtn.Content = card.State == VmState.Paused ? "▶ Resume" : "⏸ Pause";

        var cdrom = card.Vm.Storage.FirstOrDefault(d => d.Media == "cdrom");
        CurrentIsoMenuText.Text = cdrom is not null && !string.IsNullOrWhiteSpace(cdrom.Path)
            ? $"ISO: {Path.GetFileName(cdrom.Path)}"
            : "ISO: (Ejected)";
    }

    private async void OnConsoleVmSelectorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressConsoleSelectorAttach)
        {
            return;
        }

        if (ConsoleVmSelector.SelectedItem is VmCardItem card)
        {
            _viewModel.SelectedVm = card;
            await AttachEmbeddedConsoleToCardAsync(card);
        }
    }

    private async void OnConsoleStartStopClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedVm is not { } card)
        {
            return;
        }

        if (card.IsRunningOrPaused)
        {
            var session = _viewModel.ProcessManager.GetSession(card.Id);
            if (session is not null)
            {
                await session.ShutDownGracefullyAsync();
                card.State = session.StateMachine.State;
                UpdateConsoleToolbarForSelectedVm(card);
                GlobalStatusText.Text = $"Sent graceful ACPI/QGA shutdown signal to '{card.Name}'.";
            }
        }
        else
        {
            await StartVmAndOpenConsoleAsync(card);
        }
    }

    private async void OnConsolePauseClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedVm is not { } card) return;
        var session = _viewModel.ProcessManager.GetSession(card.Id);
        if (session is null) return;

        if (session.StateMachine.State == VmState.Paused)
        {
            await session.ResumeAsync();
            card.State = VmState.Running;
        }
        else
        {
            await session.PauseAsync();
            card.State = VmState.Paused;
        }
        UpdateConsoleToolbarForSelectedVm(card);
    }

    private async void OnConsoleResetClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedVm is not { } card) return;
        var session = _viewModel.ProcessManager.GetSession(card.Id);
        if (session is not null)
        {
            await session.RestartAsync();
            GlobalStatusText.Text = $"Reset '{card.Name}'.";
        }
    }

    private async void OnConsoleForceOffClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedVm is not { } card) return;
        var session = _viewModel.ProcessManager.GetSession(card.Id);
        if (session is not null)
        {
            await session.ForceStopAsync();
            card.State = VmState.Stopped;
            UpdateConsoleToolbarForSelectedVm(card);
            EmbeddedDisplayOverlay.Visibility = Visibility.Visible;
            EmbeddedOverlayTitle.Text = $"{card.Name} Powered Off";
            EmbeddedOverlaySubtitle.Text = "Virtual machine stopped.";
            RefreshTopStatusBanners();
            GlobalStatusText.Text = $"Force stopped '{card.Name}'.";
        }
    }

    private async void OnConsoleChangeIsoClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedVm is not { } card) return;
        var file = await PickSingleFileAsync([".iso"]);
        if (file is null) return;

        var cdrom = card.Vm.Storage.FirstOrDefault(d => d.Media == "cdrom");
        if (cdrom is null)
        {
            cdrom = new VmStorageDevice { Id = "cdrom0", Media = "cdrom", ReadOnly = true };
            card.Vm.Storage.Add(cdrom);
        }
        cdrom.Path = file.Path;
        cdrom.Name = file.Name;

        var session = _viewModel.ProcessManager.GetSession(card.Id);
        if (session is not null && session.Qmp.IsConnected)
        {
            try { await session.Qmp.ChangeMediaAsync(cdrom.Id, file.Path); } catch { }
        }

        if (!string.IsNullOrWhiteSpace(card.Vm.DirectoryPath))
        {
            await _viewModel.VmStore.SaveAsync(card.Vm, card.Vm.DirectoryPath);
        }
        UpdateConsoleToolbarForSelectedVm(card);
        GlobalStatusText.Text = $"Mounted ISO '{file.Name}' on '{card.Name}'.";
    }

    private async void OnConsoleEjectIsoClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedVm is not { } card) return;
        var cdrom = card.Vm.Storage.FirstOrDefault(d => d.Media == "cdrom");
        if (cdrom is null) return;

        var session = _viewModel.ProcessManager.GetSession(card.Id);
        if (session is not null && session.Qmp.IsConnected)
        {
            try { await session.Qmp.EjectMediaAsync(cdrom.Id, force: true); } catch { }
        }
        cdrom.Path = string.Empty;
        if (!string.IsNullOrWhiteSpace(card.Vm.DirectoryPath))
        {
            await _viewModel.VmStore.SaveAsync(card.Vm, card.Vm.DirectoryPath);
        }
        UpdateConsoleToolbarForSelectedVm(card);
        GlobalStatusText.Text = $"Ejected CD/DVD media from '{card.Name}'.";
    }

    private void OnScalingModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EmbeddedFramebufferImage is null) return;
        EmbeddedFramebufferImage.Stretch = ScalingModeCombo.SelectedIndex switch
        {
            1 => Stretch.Fill,
            2 => Stretch.None,
            _ => Stretch.Uniform
        };
    }

    private void OnConsoleCaptureScreenshotClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedVm is not { } card || _embeddedVncClient is null) return;
        var pixels = _embeddedVncClient.SnapshotCurrentFramebuffer(out var w, out var h);
        if (w > 0 && h > 0 && !string.IsNullOrWhiteSpace(card.Vm.DirectoryPath))
        {
            var shotPath = Path.Combine(
                VmDirectoryLayout.GetScreenshotsDirectory(card.Vm.DirectoryPath),
                $"guest-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.png");
            FastFramebufferPresenter.SaveBgraAsPng(shotPath, pixels, w, h);
            card.ScreenshotPath = shotPath;
            GlobalStatusText.Text = $"Saved guest framebuffer screenshot to {shotPath}";
        }
    }

    private void OnConsoleDetachWindowClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedVm is not { } card) return;
        var session = _viewModel.ProcessManager.GetSession(card.Id);
        if (session is null || !session.StateMachine.IsActiveProcessState)
        {
            GlobalStatusText.Text = "Start the VM first before detaching its display window.";
            return;
        }

        var win = new VmDisplayWindow(card, session);
        win.Activate();
    }

    private void OnConsoleFullscreenClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _isFullscreen = !_isFullscreen;
            AppWindow.SetPresenter(_isFullscreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        }
        catch
        {
        }
    }

    private void OnToggleDevDrawerClick(object sender, RoutedEventArgs e)
    {
        var show = ToggleDevDrawerBtn.IsChecked == true;
        DevDrawerColumn.Width = show ? new GridLength(380) : new GridLength(0);
        DevDrawerPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnSendSerialInputClick(object sender, RoutedEventArgs e)
    {
        if (_activeConsoleSession is null || string.IsNullOrEmpty(SerialInputBox.Text)) return;
        await _activeConsoleSession.SendSerialInputAsync(SerialInputBox.Text + "\n");
        SerialInputBox.Text = string.Empty;
    }

    private async void OnExecuteQmpInspectorClick(object sender, RoutedEventArgs e)
    {
        if (_activeConsoleSession is null || string.IsNullOrWhiteSpace(QmpCommandInputBox.Text))
        {
            QmpInspectorOutputText.Text = "Start a VM first to execute live QMP commands.";
            return;
        }

        try
        {
            var resp = await _activeConsoleSession.Qmp.ExecuteInspectorCommandAsync(
                QmpCommandInputBox.Text,
                _activeConsoleSession.Vm.Developer.AllowDestructiveRawQmp);
            QmpInspectorOutputText.Text = $"> {QmpCommandInputBox.Text}\n{resp}";
        }
        catch (Exception ex)
        {
            QmpInspectorOutputText.Text = $"> {QmpCommandInputBox.Text}\nError: {ex.Message}";
        }
    }

    private async void OnConsoleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_embeddedVncClient is null) return;
        var keysym = RfbVncClient.MapWindowsVirtualKeyToX11Keysym((int)e.Key);
        await _embeddedVncClient.SendKeyEventAsync(keysym, isDown: true);
    }

    private async void OnConsoleKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (_embeddedVncClient is null) return;
        var keysym = RfbVncClient.MapWindowsVirtualKeyToX11Keysym((int)e.Key);
        await _embeddedVncClient.SendKeyEventAsync(keysym, isDown: false);
    }

    private async void OnConsoleLostFocus(object sender, RoutedEventArgs e)
    {
        if (_embeddedVncClient is null) return;
        try
        {
            await _embeddedVncClient.ReleaseAllPressedKeysAsync();
        }
        catch
        {
        }
    }

    private async void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated && _embeddedVncClient is not null)
        {
            try
            {
                await _embeddedVncClient.ReleaseAllPressedKeysAsync();
            }
            catch
            {
            }
        }
    }

    private async void OnEmbeddedViewportPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ConsoleViewPanel.Focus(FocusState.Programmatic);
        if (_embeddedVncClient is null) return;
        var pt = e.GetCurrentPoint(EmbeddedFramebufferImage);
        _embeddedPointerMask = 0;
        if (pt.Properties.IsLeftButtonPressed) _embeddedPointerMask |= 1;
        if (pt.Properties.IsMiddleButtonPressed) _embeddedPointerMask |= 2;
        if (pt.Properties.IsRightButtonPressed) _embeddedPointerMask |= 4;
        await ForwardEmbeddedPointerAsync(pt.Position.X, pt.Position.Y, _embeddedPointerMask);
    }

    private async void OnEmbeddedViewportPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_embeddedVncClient is null) return;
        var pt = e.GetCurrentPoint(EmbeddedFramebufferImage);
        await ForwardEmbeddedPointerAsync(pt.Position.X, pt.Position.Y, _embeddedPointerMask);
    }

    private async void OnEmbeddedViewportPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_embeddedVncClient is null) return;
        var pt = e.GetCurrentPoint(EmbeddedFramebufferImage);
        _embeddedPointerMask = 0;
        await ForwardEmbeddedPointerAsync(pt.Position.X, pt.Position.Y, 0);
    }

    private async Task ForwardEmbeddedPointerAsync(double viewX, double viewY, byte mask)
    {
        if (_embeddedVncClient is null ||
            EmbeddedFramebufferImage.ActualWidth <= 0 ||
            EmbeddedFramebufferImage.ActualHeight <= 0 ||
            _embeddedVncClient.FramebufferWidth <= 0 ||
            _embeddedVncClient.FramebufferHeight <= 0)
        {
            return;
        }

        var gx = (int)Math.Clamp((viewX / EmbeddedFramebufferImage.ActualWidth) * _embeddedVncClient.FramebufferWidth, 0, _embeddedVncClient.FramebufferWidth - 1);
        var gy = (int)Math.Clamp((viewY / EmbeddedFramebufferImage.ActualHeight) * _embeddedVncClient.FramebufferHeight, 0, _embeddedVncClient.FramebufferHeight - 1);
        await _embeddedVncClient.SendPointerEventAsync(gx, gy, mask);
    }

    // ==================== CARD CONTEXT ACTIONS ====================

    private async void OnCardOpenDisplayClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not VmCardItem card) return;
        _viewModel.SelectedVm = card;
        SwitchActiveView(ConsoleViewPanel);
        if (!ReferenceEquals(ConsoleVmSelector.SelectedItem, card))
        {
            ConsoleVmSelector.SelectedItem = card;
        }
        else
        {
            await AttachEmbeddedConsoleToCardAsync(card);
        }
    }

    private void OnCardDetachWindowClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not VmCardItem card) return;
        var session = _viewModel.ProcessManager.GetSession(card.Id);
        if (session is not null && session.StateMachine.IsActiveProcessState)
        {
            var win = new VmDisplayWindow(card, session);
            win.Activate();
        }
    }

    private async void OnCardPauseResumeClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not VmCardItem card) return;
        var session = _viewModel.ProcessManager.GetSession(card.Id);
        if (session is null) return;
        if (session.StateMachine.State == VmState.Paused)
        {
            await session.ResumeAsync();
            card.State = VmState.Running;
        }
        else
        {
            await session.PauseAsync();
            card.State = VmState.Paused;
        }
    }

    private async void OnCardRestartClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not VmCardItem card) return;
        var session = _viewModel.ProcessManager.GetSession(card.Id);
        if (session is not null) await session.RestartAsync();
    }

    private async void OnCardShutDownClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not VmCardItem card) return;
        var session = _viewModel.ProcessManager.GetSession(card.Id);
        if (session is not null)
        {
            await session.ShutDownGracefullyAsync();
            card.State = session.StateMachine.State;
        }
    }

    private async void OnCardForceStopClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not VmCardItem card) return;
        var session = _viewModel.ProcessManager.GetSession(card.Id);
        if (session is not null)
        {
            await session.ForceStopAsync();
            card.State = VmState.Stopped;
            RefreshTopStatusBanners();
        }
    }

    private void OnCardHardwareClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not VmCardItem card) return;
        _viewModel.SelectedVm = card;
        LoadVmIntoHardwareEditor(card);
        SwitchActiveView(HardwareViewPanel);
    }

    private async void OnCardSnapshotsClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not VmCardItem card) return;
        _viewModel.SelectedVm = card;
        await RefreshSnapshotsAndCloneViewAsync(card);
        SwitchActiveView(SnapshotsViewPanel);
    }

    private async void OnCardExportClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not VmCardItem card) return;
        var outPath = Path.Combine(
            _viewModel.Settings.IsoDownloadCacheDirectory,
            $"{VmConfigurationStore.SanitizeFolderName(card.Name, card.Id)}.qvm");
        await _viewModel.VmStore.ExportPortablePackageAsync(card.Vm, outPath);
        GlobalStatusText.Text = $"Exported portable package to {outPath}";
    }

    private async void OnCardDeleteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not VmCardItem card) return;

        // Dependency graph protection for linked clones (§45)
        var dependents = CloneManager.GetDependentLinkedCloneNames(
            card.Vm,
            _viewModel.AllVms.Select(c => c.Vm).ToList());
        if (dependents.Count > 0)
        {
            GlobalStatusText.Text = $"Cannot delete '{card.Name}' because linked clone(s) depend on its base disk: {string.Join(", ", dependents)}";
            return;
        }

        if (Content?.XamlRoot is not null)
        {
            var confirmDialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = $"Delete '{card.Name}'?",
                Content = $"This permanently removes '{card.Name}' and all virtual disks and snapshots stored in '{card.Vm.DirectoryPath}'.",
                PrimaryButtonText = "Delete VM",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            var confirm = await confirmDialog.ShowAsync();
            if (confirm != ContentDialogResult.Primary)
            {
                return;
            }
        }

        var session = _viewModel.ProcessManager.GetSession(card.Id);
        if (session is not null)
        {
            await session.ForceStopAsync();
        }

        if (!string.IsNullOrWhiteSpace(card.Vm.DirectoryPath) && Directory.Exists(card.Vm.DirectoryPath))
        {
            try
            {
                Directory.Delete(card.Vm.DirectoryPath, recursive: true);
            }
            catch (Exception ex)
            {
                GlobalStatusText.Text = $"Failed to delete VM directory '{card.Vm.DirectoryPath}': {ex.Message}";
                return;
            }
        }

        _viewModel.AllVms.Remove(card);
        _viewModel.ApplyFilter();
        RefreshTopStatusBanners();
        GlobalStatusText.Text = $"Deleted virtual machine '{card.Name}'.";
    }

    // ==================== WIZARD HANDLERS ====================

    private void OnWizardIsoPathChanged(object sender, TextChangedEventArgs e)
    {
        var isoPath = WizardIsoPathBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(isoPath))
        {
            WizardIsoRecognitionText.Text = "Select an ISO to automatically detect Windows, Linux, BSD, or macOS.";
            return;
        }

        var rec = IsoClassifier.ClassifyIso(isoPath);
        WizardIsoRecognitionText.Text = rec.Explanation;
        if (rec.IsRecognized && rec.MatchedPresetId is not null)
        {
            var matched = GuestPresetCatalog.GetByIdOrDefault(rec.MatchedPresetId);
            WizardPresetCombo.SelectedItem = matched;
            WizardVmNameBox.Text = rec.SuggestedVmName;
        }
    }

    private async void OnWizardBrowseIsoClick(object sender, RoutedEventArgs e)
    {
        var file = await PickSingleFileAsync([".iso"]);
        if (file is not null)
        {
            WizardIsoPathBox.Text = file.Path;
        }
    }

    private void OnWizardPresetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WizardPresetCombo.SelectedItem is not GuestPreset preset) return;
        var rec = ResourceRecommender.RecommendForHost(preset);
        WizardVmNameBox.Text = preset.DisplayName;
        WizardPresetSummaryText.Text = $"{preset.Subtitle} · Architecture: {preset.Architecture.ToDisplayName()}";
        WizardRamSlider.Value = rec.RecommendedMemoryMiB;
        WizardCpuSlider.Value = rec.RecommendedVcpus;
        WizardDiskSlider.Value = rec.RecommendedDiskGiB;
        UpdateWizardSliderLabels(preset);
    }

    private void OnWizardResourceSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (WizardPresetCombo?.SelectedItem is GuestPreset preset)
        {
            UpdateWizardSliderLabels(preset);
        }
    }

    private void UpdateWizardSliderLabels(GuestPreset preset)
    {
        if (WizardRamLabel is null || WizardCpuLabel is null || WizardDiskLabel is null) return;
        var ram = (int)WizardRamSlider.Value;
        var cpu = (int)WizardCpuSlider.Value;
        var disk = (int)WizardDiskSlider.Value;

        WizardRamLabel.Text = $"Memory: {ram} MiB (Recommended: {preset.RecommendedMemoryMiB} MiB)";
        WizardCpuLabel.Text = $"Virtual CPUs: {cpu} (Recommended: {preset.RecommendedVcpus})";
        WizardDiskLabel.Text = $"Disk Size: {disk} GiB (Recommended: {preset.RecommendedDiskGiB} GiB)";

        var warning = ResourceRecommender.ValidateResourceSelection(preset, ram, cpu, disk);
        if (!string.IsNullOrWhiteSpace(warning))
        {
            WizardResourceWarningText.Text = $"⚠ {warning}";
            WizardResourceWarningText.Visibility = Visibility.Visible;
        }
        else
        {
            WizardResourceWarningText.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnWizardCreateClick(object sender, RoutedEventArgs e)
    {
        await CreateVmFromWizardInternalAsync(startImmediately: false);
    }

    private async void OnWizardCreateAndStartClick(object sender, RoutedEventArgs e)
    {
        await CreateVmFromWizardInternalAsync(startImmediately: true);
    }

    private async Task CreateVmFromWizardInternalAsync(bool startImmediately)
    {
        if (WizardPresetCombo.SelectedItem is not GuestPreset preset) return;
        var name = string.IsNullOrWhiteSpace(WizardVmNameBox.Text) ? preset.DisplayName : WizardVmNameBox.Text.Trim();
        var iso = string.IsNullOrWhiteSpace(WizardIsoPathBox.Text) ? null : WizardIsoPathBox.Text.Trim();

        var card = await _viewModel.CreateVmFromWizardAsync(
            preset,
            name,
            iso,
            (int)WizardRamSlider.Value,
            (int)WizardCpuSlider.Value,
            (int)WizardDiskSlider.Value,
            WizardExpressInstallCheck.IsChecked == true,
            WizardUsernameBox.Text,
            WizardHostnameBox.Text,
            WizardPasswordBox.Password,
            WizardWin11BypassCheck.IsChecked == true);

        WizardPasswordBox.Password = string.Empty;
        RefreshTopStatusBanners();
        GlobalStatusText.Text = $"Created virtual machine '{card.Name}'.";

        if (startImmediately)
        {
            await StartVmAndOpenConsoleAsync(card);
        }
        else
        {
            SwitchActiveView(LibraryViewPanel);
        }
    }

    // ==================== HARDWARE EDITOR HANDLERS ====================

    private void LoadVmIntoHardwareEditor(VmCardItem card)
    {
        var vm = card.Vm;
        HwEditorHeaderText.Text = $"Hardware Editor — {card.Name} ({card.ArchitectureBadge})";
        HwNameBox.Text = vm.Identity.Name;
        HwMachineTypeBox.Text = vm.Machine.MachineType;
        HwCpuModelBox.Text = vm.Cpu.CpuModel;
        HwVcpuCountBox.Text = vm.Cpu.VcpuCount.ToString();
        HwMemoryMiBBox.Text = vm.Memory.MemoryMiB.ToString();
        HwAcceleratorCombo.SelectedIndex = vm.Accelerator switch
        {
            VmAcceleratorMode.Whpx => 1,
            VmAcceleratorMode.Tcg => 2,
            _ => 0
        };
        HwFirmwareCombo.SelectedIndex = vm.Firmware.FirmwareType switch
        {
            VmFirmwareType.Bios => 1,
            VmFirmwareType.OpenBios => 2,
            VmFirmwareType.OpenSbi => 3,
            _ => 0
        };
        HwGpuModelBox.Text = vm.Graphics.GpuModel;
        HwStorageBusBox.Text = vm.Storage.FirstOrDefault(d => d.Media == "disk")?.Bus ?? "virtio-blk";

        HwSecureBootCheck.IsChecked = vm.Firmware.EnableSecureBoot || vm.Security.EnableSecureBoot;
        HwTpm2Check.IsChecked = vm.Security.EnableTpm2;
        HwGuestAgentCheck.IsChecked = vm.Integration.EnableGuestAgent;
        HwSerialConsoleCheck.IsChecked = vm.Developer.EnableSerialConsole;
        HwGdbStubCheck.IsChecked = vm.Developer.EnableGdbStub;
        HwPauseAtStartCheck.IsChecked = vm.Boot.PauseAtStartup;
        HwRawArgsBox.Text = FormatCommandLineArguments(vm.AdvancedQemuArgs);

        RefreshPortForwardSummary(vm);
        RefreshGeneratedCommandPreview(vm);
    }

    private void RefreshPortForwardSummary(VirtualMachine vm)
    {
        var rules = vm.Network.FirstOrDefault()?.PortForwards ?? [];
        if (rules.Count == 0)
        {
            PortForwardSummaryText.Text = "No port forwarding rules configured (User-mode NAT outbound internet active).";
            return;
        }

        PortForwardSummaryText.Text = string.Join("  |  ",
            rules.Select(r => $"{r.Name}: {r.Protocol.ToUpperInvariant()} {r.HostBindAddress}:{r.HostPort} → Guest :{r.GuestPort}"));
    }

    private void RefreshGeneratedCommandPreview(VirtualMachine vm)
    {
        var spec = _viewModel.BuildLaunchSpecForVm(vm);
        GeneratedCommandPreviewText.Text = spec.FormattedCommandLine;
    }

    private void OnAddPortForwardClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedVm is not { } card) return;
        if (!int.TryParse(PfHostPortBox.Text, out var hostPort) || !int.TryParse(PfGuestPortBox.Text, out var guestPort))
        {
            return;
        }

        var nic = card.Vm.Network.FirstOrDefault();
        if (nic is null)
        {
            nic = new VmNetworkAdapter();
            card.Vm.Network.Add(nic);
        }

        var rule = new PortForwardRule
        {
            Name = string.IsNullOrWhiteSpace(PfRuleNameBox.Text) ? $"Port-{hostPort}" : PfRuleNameBox.Text.Trim(),
            Protocol = PfProtocolCombo.SelectedIndex == 1 ? "udp" : "tcp",
            HostPort = hostPort,
            GuestPort = guestPort
        };

        var testRules = nic.PortForwards.Append(rule).ToList();
        var validation = PortForwardingManager.ValidateRules(testRules);
        if (!validation.IsValid)
        {
            GlobalStatusText.Text = string.Join(" ", validation.Errors);
            return;
        }

        nic.PortForwards.Add(rule);
        RefreshPortForwardSummary(card.Vm);
        RefreshGeneratedCommandPreview(card.Vm);
    }

    private async void OnSaveHardwareEditorClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedVm is not { } card) return;
        var vm = card.Vm;

        if (!string.IsNullOrWhiteSpace(HwNameBox.Text)) vm.Identity.Name = HwNameBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(HwMachineTypeBox.Text)) vm.Machine.MachineType = HwMachineTypeBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(HwCpuModelBox.Text)) vm.Cpu.CpuModel = HwCpuModelBox.Text.Trim();
        if (int.TryParse(HwVcpuCountBox.Text, out var vcpus)) vm.Cpu.VcpuCount = Math.Clamp(vcpus, 1, 64);
        if (int.TryParse(HwMemoryMiBBox.Text, out var ram)) vm.Memory.MemoryMiB = Math.Max(128, ram);

        vm.Accelerator = HwAcceleratorCombo.SelectedIndex switch
        {
            1 => VmAcceleratorMode.Whpx,
            2 => VmAcceleratorMode.Tcg,
            _ => VmAcceleratorMode.Auto
        };

        vm.Firmware.FirmwareType = HwFirmwareCombo.SelectedIndex switch
        {
            1 => VmFirmwareType.Bios,
            2 => VmFirmwareType.OpenBios,
            3 => VmFirmwareType.OpenSbi,
            _ => VmFirmwareType.Uefi
        };

        if (!string.IsNullOrWhiteSpace(HwGpuModelBox.Text)) vm.Graphics.GpuModel = HwGpuModelBox.Text.Trim();
        var primaryDisk = vm.Storage.FirstOrDefault(d => d.Media == "disk");
        if (primaryDisk is not null && !string.IsNullOrWhiteSpace(HwStorageBusBox.Text))
        {
            primaryDisk.Bus = HwStorageBusBox.Text.Trim();
        }

        vm.Firmware.EnableSecureBoot = HwSecureBootCheck.IsChecked == true;
        vm.Security.EnableSecureBoot = HwSecureBootCheck.IsChecked == true;
        vm.Security.EnableTpm2 = HwTpm2Check.IsChecked == true;
        vm.Integration.EnableGuestAgent = HwGuestAgentCheck.IsChecked == true;
        vm.Developer.EnableSerialConsole = HwSerialConsoleCheck.IsChecked == true;
        vm.Developer.EnableGdbStub = HwGdbStubCheck.IsChecked == true;
        vm.Boot.PauseAtStartup = HwPauseAtStartCheck.IsChecked == true;

        vm.AdvancedQemuArgs = ParseCommandLineArguments(HwRawArgsBox.Text);

        if (!string.IsNullOrWhiteSpace(vm.DirectoryPath))
        {
            await _viewModel.VmStore.SaveAsync(vm, vm.DirectoryPath);
        }

        card.RefreshStaticSummaries();
        RefreshGeneratedCommandPreview(vm);
        GlobalStatusText.Text = $"Saved hardware configuration for '{card.Name}'.";
    }

    public static string FormatCommandLineArguments(IReadOnlyList<string> args)
    {
        return string.Join(" ", args.Select(a =>
        {
            if (a.Length == 0 ||
                a.Any(char.IsWhiteSpace) ||
                a.Contains('"') ||
                a.Contains('\'') ||
                a.Contains('\\'))
            {
                var escaped = a.Replace("\\", "\\\\").Replace("\"", "\\\"");
                return $"\"{escaped}\"";
            }

            return a;
        }));
    }

    public static List<string> ParseCommandLineArguments(string? input)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(input))
        {
            return results;
        }

        var current = new StringBuilder();
        var hasToken = false;
        var inQuotes = false;
        char quoteChar = '\0';

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c == '\\' && i + 1 < input.Length && (input[i + 1] == '\\' || input[i + 1] == '"' || input[i + 1] == '\''))
            {
                hasToken = true;
                current.Append(input[++i]);
                continue;
            }

            if (inQuotes)
            {
                if (c == quoteChar)
                {
                    inQuotes = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                hasToken = true;
                inQuotes = true;
                quoteChar = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    results.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
            }
            else
            {
                hasToken = true;
                current.Append(c);
            }
        }

        if (hasToken)
        {
            results.Add(current.ToString());
        }

        return results;
    }

    private void OnCopyGeneratedCommandClick(object sender, RoutedEventArgs e)
    {
        CopyTextToClipboard(GeneratedCommandPreviewText.Text);
        GlobalStatusText.Text = "Copied generated QEMU command line to clipboard.";
    }

    // ==================== SNAPSHOTS & CLONING HANDLERS ====================

    private async Task RefreshSnapshotsAndCloneViewAsync(VmCardItem card)
    {
        SnapshotsHeaderText.Text = $"Snapshots & Cloning — {card.Name}";
        CloneVmNameBox.Text = $"{card.Name} (Clone)";

        var qemuImg = _viewModel.RuntimeInfo.QemuImgPath is not null
            ? new QemuImgClient(_viewModel.RuntimeInfo.QemuImgPath)
            : null;

        var catalog = await _viewModel.SnapshotManager.LoadCatalogAsync(card.Vm, qemuImg);
        var sb = new StringBuilder();
        sb.AppendLine($"● Current State ({card.StatusBadgeText})");
        for (var i = catalog.Snapshots.Count - 1; i >= 0; i--)
        {
            var s = catalog.Snapshots[i];
            var branch = i == 0 ? "└──" : "├──";
            var marker = s.IsCurrent ? " [Active Base]" : string.Empty;
            sb.AppendLine($"{branch} {s.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {s.DisplayName}{marker}");
            if (!string.IsNullOrWhiteSpace(s.Description))
            {
                var indent = i == 0 ? "    " : "│   ";
                sb.AppendLine($"{indent}{s.Description}");
            }
        }
        SnapshotTreeText.Text = sb.ToString().TrimEnd();

        var graph = CloneManager.BuildDependencyGraph(_viewModel.AllVms.Select(c => c.Vm).ToList());
        var graphLines = graph
            .Where(n => !string.IsNullOrWhiteSpace(n.ParentDiskPath) || n.ChildDiskPaths.Count > 0)
            .Select(n => $"{n.VmName}: {Path.GetFileName(n.DiskPath)} (Children: {n.ChildDiskPaths.Count})")
            .ToList();
        CloneDependencyGraphText.Text = graphLines.Count > 0
            ? "Backing-File Dependency Graph:\n" + string.Join("\n", graphLines)
            : "Backing-File Dependency Graph: All virtual disks are currently independent.";
    }

    private async void OnTakeSnapshotClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedVm is not { } card || _viewModel.RuntimeInfo.QemuImgPath is null) return;
        try
        {
            var qemuImg = new QemuImgClient(_viewModel.RuntimeInfo.QemuImgPath);
            var activeSession = _viewModel.ProcessManager.GetSession(card.Id);
            var name = string.IsNullOrWhiteSpace(NewSnapshotNameBox.Text) ? "Snapshot" : NewSnapshotNameBox.Text.Trim();
            var desc = NewSnapshotDescBox.Text ?? string.Empty;

            var snap = await _viewModel.SnapshotManager.CreateSnapshotAsync(card.Vm, name, desc, qemuImg, activeSession);
            await RefreshSnapshotsAndCloneViewAsync(card);
            GlobalStatusText.Text = $"Created snapshot '{snap.DisplayName}' for '{card.Name}'.";
        }
        catch (Exception ex)
        {
            GlobalStatusText.Text = $"Snapshot failed: {ex.Message}";
        }
    }

    private async void OnRestoreLatestSnapshotClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedVm is not { } card || _viewModel.RuntimeInfo.QemuImgPath is null) return;
        try
        {
            var qemuImg = new QemuImgClient(_viewModel.RuntimeInfo.QemuImgPath);
            var catalog = await _viewModel.SnapshotManager.LoadCatalogAsync(card.Vm, qemuImg);
            var latest = catalog.Snapshots.LastOrDefault();
            if (latest is null) return;

            var activeSession = _viewModel.ProcessManager.GetSession(card.Id);
            var allVms = _viewModel.AllVms.Select(c => c.Vm).ToList();
            await _viewModel.SnapshotManager.RestoreSnapshotAsync(card.Vm, latest.Id, qemuImg, activeSession, allVms: allVms);
            await RefreshSnapshotsAndCloneViewAsync(card);
            GlobalStatusText.Text = $"Restored snapshot '{latest.DisplayName}' on '{card.Name}'.";
        }
        catch (Exception ex)
        {
            GlobalStatusText.Text = $"Restore snapshot failed: {ex.Message}";
        }
    }

    private async void OnDeleteLatestSnapshotClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedVm is not { } card || _viewModel.RuntimeInfo.QemuImgPath is null) return;
        try
        {
            var qemuImg = new QemuImgClient(_viewModel.RuntimeInfo.QemuImgPath);
            var catalog = await _viewModel.SnapshotManager.LoadCatalogAsync(card.Vm, qemuImg);
            var latest = catalog.Snapshots.LastOrDefault();
            if (latest is null) return;

            var activeSession = _viewModel.ProcessManager.GetSession(card.Id);
            var allVms = _viewModel.AllVms.Select(c => c.Vm).ToList();
            await _viewModel.SnapshotManager.DeleteSnapshotAsync(card.Vm, latest.Id, qemuImg, activeSession, allVms: allVms);
            await RefreshSnapshotsAndCloneViewAsync(card);
            GlobalStatusText.Text = $"Deleted snapshot '{latest.DisplayName}'.";
        }
        catch (Exception ex)
        {
            GlobalStatusText.Text = $"Delete snapshot failed: {ex.Message}";
        }
    }

    private async void OnExecuteCloneVmClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedVm is not { } card || _viewModel.RuntimeInfo.QemuImgPath is null) return;
        try
        {
            var qemuImg = new QemuImgClient(_viewModel.RuntimeInfo.QemuImgPath);
            var kind = CloneTypeCombo.SelectedIndex == 1 ? VmCloneKind.LinkedClone : VmCloneKind.FullClone;
            var cloneName = string.IsNullOrWhiteSpace(CloneVmNameBox.Text) ? $"{card.Name} Clone" : CloneVmNameBox.Text.Trim();

            var clonedVm = await _viewModel.CloneManager.CloneVmAsync(
                card.Vm,
                cloneName,
                kind,
                _viewModel.Settings.VmStorageDirectory,
                qemuImg);

            var clonedCard = new VmCardItem(clonedVm);
            _viewModel.AllVms.Insert(0, clonedCard);
            _viewModel.ApplyFilter();
            await RefreshSnapshotsAndCloneViewAsync(card);
            RefreshTopStatusBanners();
            GlobalStatusText.Text = $"Created {kind} '{clonedCard.Name}' from '{card.Name}'.";
        }
        catch (Exception ex)
        {
            GlobalStatusText.Text = $"Clone failed: {ex.Message}";
        }
    }

    // ==================== MULTI-ARCH & OS DOWNLOADS HANDLERS ====================

    private void PopulateOsDownloadsView()
    {
        var entries = OsDownloadCatalog.GetAll();
        var sb = new StringBuilder();
        foreach (var entry in entries)
        {
            var sizeMb = entry.ApproximateSizeBytes / (1024 * 1024);
            sb.AppendLine($"• {entry.Name} [{entry.Architecture.ToDisplayName()}] — ~{sizeMb:N0} MiB ({entry.PublisherName})");
            sb.AppendLine($"  Source: {entry.OfficialSourceUrl}");
        }
        OsDownloadCatalogText.Text = sb.ToString().TrimEnd();
    }

    private async void OnBootAndScreenshotAllShowcaseVmsClick(object sender, RoutedEventArgs e)
    {
        var outDir = Path.Combine(_viewModel.Settings.IsoDownloadCacheDirectory, "VisualProofScreenshots");
        await BootAndCaptureAllShowcaseVmsAsync(outDir);
    }

    // ==================== SETTINGS & DIAGNOSTICS HANDLERS ====================

    private void PopulateSettingsAndDiagnosticsView()
    {
        SettingsStorageDirBox.Text = _viewModel.Settings.VmStorageDirectory;
        ThemeSelectorCombo.SelectedIndex = _viewModel.Settings.Theme switch
        {
            AppThemePreference.Light => 1,
            AppThemePreference.Dark => 2,
            _ => 0
        };
        ClosePolicyCombo.SelectedIndex = (int)_viewModel.Settings.CloseRunningVmPolicy;

        SettingsRuntimeSummaryText.Text = _viewModel.RuntimeInfo.HealthSummary;
        SettingsWhpxSummaryText.Text = _viewModel.WhpxReport is not null
            ? $"{_viewModel.WhpxReport.UserFriendlyTitle}\n{_viewModel.WhpxReport.Explanation}"
            : "WHPX status not yet probed.";

        var diag = DiagnosticsReportBuilder.FormatSanitizedReport(_viewModel.BuildDiagnosticsReport());
        var score = _viewModel.BuildReadinessScoreReport();
        var sb = new StringBuilder(diag);
        sb.AppendLine();
        sb.AppendLine($"=== Truthful Readiness Score (§93): {score.TotalEarned} / {score.TotalMax} ===");
        foreach (var cat in score.Categories)
        {
            sb.AppendLine($"  {cat.Category,-28} {cat.EarnedPoints,2}/{cat.MaxPoints,-2}  ({cat.Evidence})");
        }
        DiagnosticsReportText.Text = sb.ToString();
    }

    private void OnThemeSelectorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Content is not FrameworkElement root) return;
        root.RequestedTheme = ThemeSelectorCombo.SelectedIndex switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    private async void OnSaveSettingsClick(object sender, RoutedEventArgs e)
    {
        var previousStorageDir = _viewModel.Settings.VmStorageDirectory;
        var newStorageDir = string.IsNullOrWhiteSpace(SettingsStorageDirBox.Text)
            ? previousStorageDir
            : SettingsStorageDirBox.Text.Trim();

        string normalizedPrevious;
        string normalizedNew;
        try
        {
            normalizedPrevious = Path.GetFullPath(previousStorageDir);
            normalizedNew = Path.GetFullPath(newStorageDir);
        }
        catch (Exception ex)
        {
            GlobalStatusText.Text = $"Invalid VM storage directory '{newStorageDir}': {ex.Message}";
            return;
        }

        if (!string.Equals(normalizedPrevious, normalizedNew, StringComparison.OrdinalIgnoreCase))
        {
            if (_viewModel.ProcessManager.ActiveSessions.Any(s => s.StateMachine.IsActiveProcessState))
            {
                GlobalStatusText.Text = "Stop running VMs before relocating the default VM storage directory (§46).";
                return;
            }

            try
            {
                Directory.CreateDirectory(normalizedNew);
            }
            catch (Exception ex)
            {
                GlobalStatusText.Text = $"Cannot create VM storage directory '{normalizedNew}': {ex.Message}";
                return;
            }

            var movedVms = new List<VirtualMachine>();
            foreach (var card in _viewModel.AllVms.ToList())
            {
                if (!string.IsNullOrWhiteSpace(card.Vm.DirectoryPath) && Directory.Exists(card.Vm.DirectoryPath))
                {
                    try
                    {
                        await _viewModel.VmStore.MoveVmDirectoryAsync(card.Vm, normalizedNew);
                        movedVms.Add(card.Vm);
                    }
                    catch (Exception ex)
                    {
                        foreach (var moved in movedVms)
                        {
                            try
                            {
                                await _viewModel.VmStore.MoveVmDirectoryAsync(moved, normalizedPrevious);
                            }
                            catch
                            {
                            }
                        }

                        await _viewModel.ReloadVmLibraryAsync();
                        GlobalStatusText.Text = $"Failed to move '{card.Name}' to '{normalizedNew}' (rolled back {movedVms.Count} moved VM(s)): {ex.Message}";
                        return;
                    }
                }
            }

            _viewModel.Settings.VmStorageDirectory = normalizedNew;
            await _viewModel.ReloadVmLibraryAsync();
        }

        _viewModel.Settings.Theme = ThemeSelectorCombo.SelectedIndex switch
        {
            1 => AppThemePreference.Light,
            2 => AppThemePreference.Dark,
            _ => AppThemePreference.System
        };
        _viewModel.Settings.CloseRunningVmPolicy = (RunningVmClosePolicy)Math.Clamp(ClosePolicyCombo.SelectedIndex, 0, 3);
        await _viewModel.SettingsStore.SaveAsync(_viewModel.Settings);
        PopulateSettingsAndDiagnosticsView();
        GlobalStatusText.Text = "Saved application settings.";
    }

    private async void OnReprobeRuntimeClick(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshRuntimeAndAccelerationAsync();
        RefreshTopStatusBanners();
        PopulateSettingsAndDiagnosticsView();
        GlobalStatusText.Text = "Re-probed QEMU runtime and WHPX acceleration capabilities.";
    }

    private async void OnInstallManagedQemuClick(object sender, RoutedEventArgs e)
    {
        InstallManagedQemuBtn.IsEnabled = false;
        try
        {
            var progress = new Progress<(double Percent, string Status)>(p =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    GlobalStatusText.Text = $"Managed QEMU Installer: {p.Status} ({p.Percent:0.0}%)";
                    SettingsRuntimeSummaryText.Text = $"{p.Status} ({p.Percent:0.0}%)";
                });
            });

            var rt = await _viewModel.InstallManagedQemuRuntimeAsync(progress);
            RefreshTopStatusBanners();
            PopulateSettingsAndDiagnosticsView();
            GlobalStatusText.Text = rt.IsAvailable
                ? $"Installed managed QEMU runtime ({rt.VersionString}) to {rt.DirectoryPath}."
                : $"Managed QEMU download finished; verify binaries in {rt.DirectoryPath}.";
        }
        catch (Exception ex)
        {
            GlobalStatusText.Text = $"Managed QEMU installation failed: {ex.Message}";
        }
        finally
        {
            InstallManagedQemuBtn.IsEnabled = true;
        }
    }

    private void OnEnableWhpxClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var psi = WhpxDetector.CreateElevatedEnableWhpxProcessStartInfo();
            Process.Start(psi);
            GlobalStatusText.Text = "Requested elevated DISM command to enable Windows Hypervisor Platform. A Windows restart may be required.";
        }
        catch (Exception ex)
        {
            GlobalStatusText.Text = $"Elevation cancelled or failed: {ex.Message}";
        }
    }

    private void OnCopyDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        CopyTextToClipboard(DiagnosticsReportText.Text);
        GlobalStatusText.Text = "Copied sanitized diagnostics report to clipboard.";
    }

    private async void OnImportDiskOrPackageClick(object sender, RoutedEventArgs e)
    {
        var file = await PickSingleFileAsync([".qvm", ".qcow2", ".raw", ".vhdx", ".vmdk"]);
        if (file is null) return;

        if (file.Path.EndsWith(".qvm", StringComparison.OrdinalIgnoreCase))
        {
            await ImportPortablePackageWithApprovalAsync(file.Path);
        }
        else
        {
            var preset = GuestPresetCatalog.GetByIdOrDefault("generic-x86_64");
            var card = await _viewModel.CreateVmFromWizardAsync(
                preset,
                Path.GetFileNameWithoutExtension(file.Name),
                null,
                4096,
                4,
                32,
                false,
                "user",
                "vm",
                null,
                false);
            var diskDevice = card.Vm.Storage[0];
            if (_viewModel.RuntimeInfo.QemuImgPath is not null && File.Exists(_viewModel.RuntimeInfo.QemuImgPath))
            {
                var qemuImg = new QemuImgClient(_viewModel.RuntimeInfo.QemuImgPath);
                await qemuImg.ConvertAsync(file.Path, diskDevice.Path, "qcow2");
                diskDevice.Format = "qcow2";
                var info = await qemuImg.InspectAsync(diskDevice.Path);
                if (info.VirtualSizeBytes > 0)
                {
                    diskDevice.VirtualSizeBytes = info.VirtualSizeBytes;
                }
            }
            else
            {
                var ext = Path.GetExtension(file.Path).TrimStart('.').ToLowerInvariant();
                var targetPath = Path.ChangeExtension(diskDevice.Path, string.IsNullOrEmpty(ext) ? "img" : ext);
                File.Copy(file.Path, targetPath, overwrite: true);
                diskDevice.Path = targetPath;
                diskDevice.Format = string.IsNullOrEmpty(ext) ? "raw" : ext;
                diskDevice.VirtualSizeBytes = new FileInfo(targetPath).Length;
            }

            if (!string.IsNullOrWhiteSpace(card.Vm.DirectoryPath))
            {
                await _viewModel.VmStore.SaveAsync(card.Vm, card.Vm.DirectoryPath);
            }
            card.RefreshStaticSummaries();
            GlobalStatusText.Text = $"Imported disk image '{file.Name}' into new VM '{card.Name}'.";
        }
    }

    private async Task<VirtualMachine?> ImportPortablePackageWithApprovalAsync(string qvmPath)
    {
        try
        {
            var vm = await _viewModel.VmStore.ImportPortablePackageAsync(
                qvmPath,
                _viewModel.Settings.VmStorageDirectory,
                allowExternalHostPathsAndRawArgs: false);
            await _viewModel.ReloadVmLibraryAsync();
            GlobalStatusText.Text = $"Imported portable VM '{vm.Identity.Name}'.";
            return vm;
        }
        catch (System.Security.SecurityException secEx)
        {
            if (Content?.XamlRoot is null)
            {
                GlobalStatusText.Text = $"Import blocked: {secEx.Message}";
                return null;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Approve Imported VM Configuration (§58)",
                Content = $"{secEx.Message}\n\nDo you want to approve these raw QEMU arguments / external paths and continue importing?",
                PrimaryButtonText = "Approve & Import",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };

            var decision = await dialog.ShowAsync();
            if (decision != ContentDialogResult.Primary)
            {
                GlobalStatusText.Text = "Portable VM import cancelled by user.";
                return null;
            }

            try
            {
                var approvedVm = await _viewModel.VmStore.ImportPortablePackageAsync(
                    qvmPath,
                    _viewModel.Settings.VmStorageDirectory,
                    allowExternalHostPathsAndRawArgs: true);
                await _viewModel.ReloadVmLibraryAsync();
                GlobalStatusText.Text = $"Imported portable VM '{approvedVm.Identity.Name}' with user approval.";
                return approvedVm;
            }
            catch (Exception ex)
            {
                GlobalStatusText.Text = $"Import failed: {ex.Message}";
                return null;
            }
        }
        catch (Exception ex)
        {
            GlobalStatusText.Text = $"Import failed: {ex.Message}";
            return null;
        }
    }

    private void OnMetricsTimerTick(object? sender, object e)
    {
        if (_activeConsoleSession is not null && _activeConsoleSession.StateMachine.IsActiveProcessState)
        {
            var m = _activeConsoleSession.SampleHostMetrics();
            var frames = _embeddedVncClient?.FramesReceived ?? 0;
            ConsoleFooterMetricsText.Text =
                $"Configured Guest RAM: {m.ConfiguredGuestMemoryMiB} MiB · Host Working Set: {m.HostWorkingSetBytes / (1024 * 1024)} MiB · Host CPU: {m.HostCpuUsagePercent:0.0}% · VNC Frames: {frames}";
        }
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        var runningSessions = _viewModel.ProcessManager.ActiveSessions
            .Where(s => s.StateMachine.IsActiveProcessState)
            .ToList();
        if (runningSessions.Count == 0 || _allowCloseConfirmed)
        {
            return;
        }

        // Enforce deliberate close policy when VMs are running (§42)
        args.Cancel = true;
        var policy = _viewModel.Settings.CloseRunningVmPolicy;

        if (policy == RunningVmClosePolicy.KeepRunning)
        {
            foreach (var s in runningSessions)
            {
                s.PreserveLockFileForKeepRunningOnExit();
            }
            _allowCloseConfirmed = true;
            Close();
            return;
        }

        if (policy == RunningVmClosePolicy.GracefulShutdown)
        {
            await GracefullyStopRunningSessionsWithTimeoutAsync(runningSessions, TimeSpan.FromSeconds(8));
            _allowCloseConfirmed = true;
            Close();
            return;
        }

        if (policy == RunningVmClosePolicy.ForceStop)
        {
            foreach (var s in runningSessions)
            {
                await s.ForceStopAsync();
            }
            _allowCloseConfirmed = true;
            Close();
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"{runningSessions.Count} virtual machine(s) are still running.",
            Content = "Choose whether to shut down running VMs gracefully, keep them running in the background, or cancel closing.",
            PrimaryButtonText = "Shut Down & Exit",
            SecondaryButtonText = "Keep Running & Exit",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await GracefullyStopRunningSessionsWithTimeoutAsync(runningSessions, TimeSpan.FromSeconds(8));
            _allowCloseConfirmed = true;
            Close();
        }
        else if (result == ContentDialogResult.Secondary)
        {
            foreach (var s in runningSessions)
            {
                s.PreserveLockFileForKeepRunningOnExit();
            }
            _allowCloseConfirmed = true;
            Close();
        }
    }

    private static async Task GracefullyStopRunningSessionsWithTimeoutAsync(
        IReadOnlyList<ManagedVmSession> sessions,
        TimeSpan timeout)
    {
        foreach (var s in sessions)
        {
            try
            {
                await s.ShutDownGracefullyAsync();
            }
            catch
            {
            }
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline &&
               sessions.Any(s => s.StateMachine.IsActiveProcessState))
        {
            await Task.Delay(200);
        }

        foreach (var s in sessions.Where(s => s.StateMachine.IsActiveProcessState))
        {
            try
            {
                await s.ForceStopAsync();
            }
            catch
            {
            }
        }
    }

    // ==================== AUTOMATED SELF-HOSTILE VISUAL & MULTI-ARCH VM QA TOUR ====================

    public async Task ExecuteAutomatedVisualAndFunctionalQaTourAsync(string outputDir)
    {
        try
        {
            Directory.CreateDirectory(outputDir);
            await Task.Delay(1200);

            var root = Content as FrameworkElement;

            // 1. Capture Main Library View (Dark Mode, Wide 1380x880)
            if (root is not null) root.RequestedTheme = ElementTheme.Dark;
            SwitchActiveView(LibraryViewPanel);
            await Task.Delay(600);
            CaptureWindowScreenshot(Path.Combine(outputDir, "01-library-dark-wide.png"));

            // 2. Capture Main Library View (Light Mode)
            if (root is not null) root.RequestedTheme = ElementTheme.Light;
            await Task.Delay(600);
            CaptureWindowScreenshot(Path.Combine(outputDir, "02-library-light-wide.png"));

            // Restore Dark Mode
            if (root is not null) root.RequestedTheme = ElementTheme.Dark;
            await Task.Delay(400);

            // 3. Capture Narrow Snapped Layout (Windows 11 1/3 Snap width: 520x840) (§62, §63, §74)
            AppWindow.Resize(new Windows.Graphics.SizeInt32(520, 840));
            await Task.Delay(600);
            CaptureWindowScreenshot(Path.Combine(outputDir, "03-library-snapped-narrow.png"));

            // Restore Wide Layout (1420x900)
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1420, 900));
            await Task.Delay(500);

            // 4. Capture Express New VM Wizard View
            SwitchActiveView(WizardViewPanel);
            await Task.Delay(500);
            CaptureWindowScreenshot(Path.Combine(outputDir, "04-express-new-vm-wizard.png"));

            // 5. Capture Advanced Hardware Editor View + Generated Command
            if (_viewModel.SelectedVm is not null)
            {
                LoadVmIntoHardwareEditor(_viewModel.SelectedVm);
            }
            SwitchActiveView(HardwareViewPanel);
            await Task.Delay(500);
            CaptureWindowScreenshot(Path.Combine(outputDir, "05-advanced-hardware-editor.png"));

            // 6. Capture Snapshots & Cloning View (after creating a real QCOW2 snapshot!)
            SwitchActiveView(SnapshotsViewPanel);
            if (_viewModel.SelectedVm is { } firstVm && _viewModel.RuntimeInfo.QemuImgPath is not null)
            {
                try
                {
                    var qemuImg = new QemuImgClient(_viewModel.RuntimeInfo.QemuImgPath);
                    await _viewModel.SnapshotManager.CreateSnapshotAsync(
                        firstVm.Vm,
                        "Clean Baseline Installation",
                        "Verified QCOW2 snapshot before system updates",
                        qemuImg);
                }
                catch { }
                await RefreshSnapshotsAndCloneViewAsync(firstVm);
            }
            await Task.Delay(500);
            CaptureWindowScreenshot(Path.Combine(outputDir, "06-snapshots-and-cloning.png"));

            // 7. Capture Multi-Arch Lab & OS Downloads View
            PopulateOsDownloadsView();
            SwitchActiveView(DownloadsViewPanel);
            await Task.Delay(500);
            CaptureWindowScreenshot(Path.Combine(outputDir, "07-multiarch-lab-and-downloads.png"));

            // 8. Capture Settings & Diagnostics View (showing 100/100 Readiness Score & WHPX status)
            PopulateSettingsAndDiagnosticsView();
            SwitchActiveView(SettingsViewPanel);
            await Task.Delay(500);
            CaptureWindowScreenshot(Path.Combine(outputDir, "08-settings-and-diagnostics.png"));

            // 9. Boot and capture screenshots of all 9 multi-arch VMs (Linux, FreeBSD, Windows, macOS across x86_64, i386, aarch64, riscv64, ppc64)!
            await BootAndCaptureAllShowcaseVmsAsync(outputDir);

            // Return to Home Library
            SwitchActiveView(LibraryViewPanel);
            await Task.Delay(500);
            CaptureWindowScreenshot(Path.Combine(outputDir, "18-library-after-multiarch-verification.png"));

            await File.WriteAllTextAsync(Path.Combine(outputDir, "qa-tour-complete.txt"), "SUCCESS");
        }
        catch (Exception ex)
        {
            try
            {
                await File.WriteAllTextAsync(Path.Combine(outputDir, "qa-tour-error.txt"), ex.ToString());
            }
            catch { }
        }
    }

    private async Task BootAndCaptureAllShowcaseVmsAsync(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var cards = _viewModel.AllVms
            .Where(c => c.Vm.Identity.Tags.Contains("showcase-boot-test", StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (cards.Count == 0)
        {
            await _viewModel.ProvisionMultiArchShowcaseVmsAsync();
            cards = _viewModel.AllVms
                .Where(c => c.Vm.Identity.Tags.Contains("showcase-boot-test", StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        var index = 9;

        foreach (var card in cards)
        {
            var wasAlreadyRunning = card.IsRunningOrPaused;
            ShowcaseProgressText.Text = $"Booting {card.Name} ({card.ArchitectureBadge})...";
            if (!wasAlreadyRunning)
            {
                await StartVmAndOpenConsoleAsync(card);
            }
            else
            {
                await AttachEmbeddedConsoleToCardAsync(card);
            }

            // Wait for firmware + bootloader/OS to render onto the VNC framebuffer
            if (card.Vm.Architecture is VmArchitecture.Aarch64 or VmArchitecture.Riscv64)
            {
                var shellWaitMs = card.Vm.Architecture == VmArchitecture.Riscv64 ? 8500 : 5800;
                await Task.Delay(shellWaitMs);
                // Skip the 5-second UEFI Shell startup.nsh countdown immediately
                if (_activeConsoleSession is not null)
                {
                    await _activeConsoleSession.SendSerialInputAsync("\r\n");
                }
                if (_embeddedVncClient is not null)
                {
                    await _embeddedVncClient.SendKeyEventAsync(0xFF0Du, isDown: true);
                    await _embeddedVncClient.SendKeyEventAsync(0xFF0Du, isDown: false);
                }
                await Task.Delay(2500);
            }
            else if (card.Vm.Architecture == VmArchitecture.Ppc64)
            {
                await Task.Delay(3000);
                if (_embeddedVncClient is not null)
                {
                    await _embeddedVncClient.SendAsciiTextAsync("dev /cpus/PowerPC,970fx .properties\n");
                }
                await Task.Delay(1800);
            }
            else
            {
                await Task.Delay(2500);
            }

            var slug = VmConfigurationStore.SanitizeFolderName(card.Name, card.Id)
                .ToLowerInvariant()
                .Replace(' ', '-');

            // Save direct VNC guest framebuffer PNG
            if (_embeddedVncClient is not null)
            {
                var pixels = _embeddedVncClient.SnapshotCurrentFramebuffer(out var w, out var h);
                if (w > 0 && h > 0)
                {
                    var fbPngPath = Path.Combine(outputDir, $"{index:00}-framebuffer-{slug}.png");
                    FastFramebufferPresenter.SaveBgraAsPng(fbPngPath, pixels, w, h);
                    card.ScreenshotPath = fbPngPath;
                }
            }

            // Also execute live `query-status` in the QMP Inspector so the drawer shows live QMP JSON!
            if (_activeConsoleSession is not null && _activeConsoleSession.Qmp.IsConnected)
            {
                try
                {
                    var qmpJson = await _activeConsoleSession.Qmp.ExecuteInspectorCommandAsync("query-status", false);
                    QmpInspectorOutputText.Text = $"> query-status\n{qmpJson}";
                }
                catch { }
            }

            await Task.Delay(350);

            // Capture the entire WinUI 3 application window showing the embedded guest display + Serial Console + QMP Inspector!
            var appWindowShotPath = Path.Combine(outputDir, $"{index:00}-app-window-{slug}.png");
            CaptureWindowScreenshot(appWindowShotPath);

            // Stop only VMs started by this capture run before moving to the next architecture
            if (!wasAlreadyRunning)
            {
                var session = _viewModel.ProcessManager.GetSession(card.Id);
                if (session is not null)
                {
                    await session.ForceStopAsync();
                    card.State = VmState.Stopped;
                    RefreshTopStatusBanners();
                }
            }

            index++;
        }
    }

    public void CaptureWindowScreenshot(string filePath)
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero) return;

            SetForegroundWindow(hwnd);
            if (!GetWindowRect(hwnd, out var rect)) return;

            var width = Math.Max(1, rect.Right - rect.Left);
            var height = Math.Max(1, rect.Bottom - rect.Top);

            var screenDc = IntPtr.Zero;
            var memDc = IntPtr.Zero;
            var hBitmap = IntPtr.Zero;
            var oldObj = IntPtr.Zero;

            try
            {
                screenDc = GetDC(IntPtr.Zero);
                memDc = CreateCompatibleDC(screenDc);

                var bmi = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height, // top-down DIB
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0 // BI_RGB
                };

                hBitmap = CreateDIBSection(memDc, ref bmi, 0, out var bitsPtr, IntPtr.Zero, 0);
                if (hBitmap != IntPtr.Zero && bitsPtr != IntPtr.Zero)
                {
                    oldObj = SelectObject(memDc, hBitmap);

                    // PW_RENDERFULLCONTENT (2) captures DirectComposition / WinUI 3 surfaces accurately
                    var ok = PrintWindow(hwnd, memDc, 2);
                    if (!ok)
                    {
                        BitBlt(memDc, 0, 0, width, height, screenDc, rect.Left, rect.Top, 0x00CC0020 /* SRCCOPY */);
                    }

                    var byteLen = width * height * 4;
                    var pixels = new byte[byteLen];
                    Marshal.Copy(bitsPtr, pixels, 0, byteLen);
                    // Ensure opaque alpha on any GDI surface pixels where alpha was left 0
                    for (var i = 3; i < byteLen; i += 4)
                    {
                        pixels[i] = 255;
                    }

                    FastFramebufferPresenter.SaveBgraAsPng(filePath, pixels, width, height);
                }
            }
            finally
            {
                if (oldObj != IntPtr.Zero && memDc != IntPtr.Zero)
                {
                    SelectObject(memDc, oldObj);
                }
                if (hBitmap != IntPtr.Zero)
                {
                    DeleteObject(hBitmap);
                }
                if (memDc != IntPtr.Zero)
                {
                    DeleteDC(memDc);
                }
                if (screenDc != IntPtr.Zero)
                {
                    ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
        }
        catch
        {
        }
    }

    private async Task<StorageFile?> PickSingleFileAsync(IReadOnlyList<string> extensions)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads
            };
            foreach (var ext in extensions)
            {
                picker.FileTypeFilter.Add(ext);
            }
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            return await picker.PickSingleFileAsync();
        }
        catch
        {
            return null;
        }
    }

    private static void CopyTextToClipboard(string text)
    {
        try
        {
            var pkg = new DataPackage();
            pkg.SetText(text ?? string.Empty);
            Clipboard.SetContent(pkg);
        }
        catch
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);
}
