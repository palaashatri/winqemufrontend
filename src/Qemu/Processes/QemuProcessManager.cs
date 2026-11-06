using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using WinQemu.Core.Configuration;
using WinQemu.Core.Diagnostics;
using WinQemu.Core.Lifecycle;
using WinQemu.Core.Models;
using WinQemu.Qemu.CommandBuilder;
using WinQemu.Qemu.GuestAgent;
using WinQemu.Qemu.Qmp;

namespace WinQemu.Qemu.Processes;

public sealed record VmHostResourceMetrics(
    int ProcessId,
    double HostCpuUsagePercent,
    long HostWorkingSetBytes,
    int ConfiguredGuestMemoryMiB,
    TimeSpan Uptime);

public sealed class ManagedVmSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly LogRotator _logRotator;
    private readonly StringBuilder _stderrBuffer = new();
    private readonly StringBuilder _serialBuffer = new();
    private readonly Lock _serialLock = new();
    private readonly CancellationTokenSource _sessionCts = new();
    private VmExclusiveLockHandle? _lockHandle;
    private IAsyncDisposable? _companionResource;
    private TcpClient? _serialClient;
    private NetworkStream? _serialStream;
    private TimeSpan _lastCpuTime;
    private DateTimeOffset _lastSampleTimeUtc;
    private VmHostResourceMetrics? _lastSampledMetrics;

    public ManagedVmSession(
        VirtualMachine vm,
        QemuLaunchSpec launchSpec,
        Process process,
        QmpClient qmpClient,
        LogRotator logRotator,
        VmExclusiveLockHandle? lockHandle = null,
        IAsyncDisposable? companionResource = null)
    {
        Vm = vm;
        LaunchSpec = launchSpec;
        _process = process;
        Qmp = qmpClient;
        _logRotator = logRotator;
        _lockHandle = lockHandle;
        _companionResource = companionResource;
        StateMachine = new VmStateMachine(vm.Identity.Id, VmState.Starting);
        StartedAtUtc = DateTimeOffset.UtcNow;
        _lastSampleTimeUtc = StartedAtUtc;

        if (launchSpec.Endpoints.GuestAgentPort is > 0)
        {
            GuestAgent = new QgaClient(launchSpec.Endpoints.GuestAgentPort.Value);
        }
    }

    public VirtualMachine Vm { get; }
    public QemuLaunchSpec LaunchSpec { get; }
    public QmpClient Qmp { get; }
    public QgaClient? GuestAgent { get; }
    public VmStateMachine StateMachine { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public int ProcessId => _process.Id;
    public string ActiveAccelerator => LaunchSpec.ActiveAccelerator;
    public ErrorRemediation? LastRemediation { get; private set; }
    public VmHostResourceMetrics LastSampledMetrics => _lastSampledMetrics ?? SampleHostMetrics();

    public void PreserveLockFileForKeepRunningOnExit()
    {
        if (_lockHandle is not null)
        {
            _lockHandle.DeleteFileOnDispose = false;
            _lockHandle.Dispose();
            _lockHandle = null;
        }
    }

    public event EventHandler<string>? SerialOutputReceived;
    public event EventHandler<string>? LogLineAppended;

    internal void AttachProcessStreamsAndEvents()
    {
        _process.EnableRaisingEvents = true;
        _process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                AppendLog("STDOUT", e.Data);
            }
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                lock (_stderrBuffer)
                {
                    if (_stderrBuffer.Length < 16384)
                    {
                        _stderrBuffer.AppendLine(e.Data);
                    }
                }
                AppendLog("STDERR", e.Data);
            }
        };
        _process.Exited += (_, _) => OnProcessExited();

        try { _process.BeginOutputReadLine(); } catch { }
        try { _process.BeginErrorReadLine(); } catch { }

        Qmp.EventReceived += OnQmpEventReceived;
        Qmp.RawTrafficLogged += (_, line) => AppendLog("QMP", line);

        if (LaunchSpec.Endpoints.SerialPort is > 0)
        {
            _ = Task.Run(() => ConnectSerialConsoleLoopAsync(LaunchSpec.Endpoints.SerialPort.Value, _sessionCts.Token));
        }
    }

    public void AppendLog(string category, string message)
    {
        _logRotator.AppendLine(category, message);
        try
        {
            LogLineAppended?.Invoke(this, $"[{category}] {message}");
        }
        catch
        {
        }
    }

    public string GetRecentLogs(int maxLines = 300) => _logRotator.ReadRecentTail(maxLines);

    public string GetSerialConsoleHistory()
    {
        lock (_serialLock)
        {
            return _serialBuffer.ToString();
        }
    }

    public async Task SendSerialInputAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text) || _serialStream is null)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        await _serialStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _serialStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public VmHostResourceMetrics SampleHostMetrics()
    {
        try
        {
            if (_process.HasExited)
            {
                var exitedMetrics = new VmHostResourceMetrics(0, 0, 0, Vm.Memory.MemoryMiB, TimeSpan.Zero);
                _lastSampledMetrics = exitedMetrics;
                return exitedMetrics;
            }

            var now = DateTimeOffset.UtcNow;
            var elapsedMs = (now - _lastSampleTimeUtc).TotalMilliseconds;
            if (elapsedMs < 750 && _lastSampledMetrics is not null)
            {
                return _lastSampledMetrics;
            }

            _process.Refresh();
            var cpuTime = _process.TotalProcessorTime;
            var cpuPercent = 0.0;

            if (elapsedMs > 50 && _lastCpuTime != TimeSpan.Zero)
            {
                var cpuDeltaMs = (cpuTime - _lastCpuTime).TotalMilliseconds;
                cpuPercent = Math.Clamp(
                    (cpuDeltaMs / (elapsedMs * Math.Max(1, Environment.ProcessorCount))) * 100.0,
                    0.0,
                    100.0);
            }

            _lastCpuTime = cpuTime;
            _lastSampleTimeUtc = now;

            var sampled = new VmHostResourceMetrics(
                ProcessId: _process.Id,
                HostCpuUsagePercent: Math.Round(cpuPercent, 1),
                HostWorkingSetBytes: _process.WorkingSet64,
                ConfiguredGuestMemoryMiB: Vm.Memory.MemoryMiB,
                Uptime: now - StartedAtUtc);
            _lastSampledMetrics = sampled;
            return sampled;
        }
        catch
        {
            return _lastSampledMetrics ?? new VmHostResourceMetrics(0, 0, 0, Vm.Memory.MemoryMiB, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Graceful guest shutdown (§41): tries QGA first if available, then QMP system_powerdown.
    /// Never kills power immediately.
    /// </summary>
    public async Task ShutDownGracefullyAsync(CancellationToken cancellationToken = default)
    {
        if (!StateMachine.IsActiveProcessState)
        {
            return;
        }

        StateMachine.TransitionTo(VmState.ShuttingDown, "User requested graceful guest shutdown");
        AppendLog("LIFECYCLE", "Requesting graceful guest shutdown (QGA / ACPI system_powerdown).");

        if (GuestAgent is not null && await GuestAgent.PingAsync(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false))
        {
            if (await GuestAgent.RequestShutdownAsync("powerdown", cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        if (Qmp.IsConnected)
        {
            await Qmp.SystemPowerdownAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Immediate power-off (§41): sends QMP quit and terminates the QEMU process if needed.
    /// </summary>
    public async Task ForceStopAsync(CancellationToken cancellationToken = default)
    {
        if (StateMachine.State is VmState.Stopped or VmState.Crashed)
        {
            ReleaseSessionLockAndCompanion();
            return;
        }

        StateMachine.TransitionTo(VmState.Stopping, "User requested force stop");
        AppendLog("LIFECYCLE", "Executing force stop (QMP quit / process termination).");

        try
        {
            if (Qmp.IsConnected)
            {
                await Qmp.QuitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
        }

        try
        {
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waitCts.CancelAfter(TimeSpan.FromMilliseconds(1500));
            await _process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        }
        finally
        {
            ReleaseSessionLockAndCompanion();
            if (StateMachine.State != VmState.Stopped)
            {
                StateMachine.TransitionTo(VmState.Stopped, "VM process stopped");
            }
        }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await Qmp.PauseAsync(cancellationToken).ConfigureAwait(false);
        StateMachine.TransitionTo(VmState.Paused, "Paused via QMP");
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await Qmp.ResumeAsync(cancellationToken).ConfigureAwait(false);
        StateMachine.TransitionTo(VmState.Running, "Resumed via QMP");
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await Qmp.SystemResetAsync(cancellationToken).ConfigureAwait(false);
        AppendLog("LIFECYCLE", "Guest reset triggered via QMP system_reset.");
    }

    private void OnQmpEventReceived(object? sender, QmpEventMessage e)
    {
        AppendLog("QMP-EVENT", e.EventName);
        switch (e.EventName.ToUpperInvariant())
        {
            case "STOP":
                StateMachine.TransitionTo(VmState.Paused, "QMP STOP event");
                break;
            case "RESUME":
                StateMachine.TransitionTo(VmState.Running, "QMP RESUME event");
                break;
            case "POWERDOWN":
                StateMachine.TransitionTo(VmState.ShuttingDown, "QMP POWERDOWN event");
                break;
            case "SHUTDOWN":
                StateMachine.TransitionTo(VmState.Stopping, "QMP SHUTDOWN event");
                break;
            case "GUEST_PANICKED":
                StateMachine.TransitionTo(VmState.Crashed, "Guest kernel panic reported by QMP");
                break;
        }
    }

    private void ReleaseSessionLockAndCompanion()
    {
        var handle = Interlocked.Exchange(ref _lockHandle, null);
        if (handle is not null)
        {
            try { handle.Dispose(); } catch { }
        }
        else
        {
            VmLockManager.ReleaseLockFile(Vm.DirectoryPath);
        }

        var companion = Interlocked.Exchange(ref _companionResource, null);
        if (companion is not null)
        {
            try { companion.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        }
    }

    private void OnProcessExited()
    {
        ReleaseSessionLockAndCompanion();
        int exitCode = 0;
        try { exitCode = _process.ExitCode; } catch { }

        string stderrText;
        lock (_stderrBuffer)
        {
            stderrText = _stderrBuffer.ToString();
        }

        if (exitCode != 0 && StateMachine.State is not VmState.Stopping and not VmState.ShuttingDown)
        {
            LastRemediation = ErrorTranslator.Translate(stderrText, exitCode);
            StateMachine.TransitionTo(VmState.Crashed, $"QEMU exited with code {exitCode}", LastRemediation.UserFriendlyExplanation);
            AppendLog("ERROR", $"QEMU exited unexpectedly with code {exitCode}: {stderrText}");
        }
        else
        {
            StateMachine.TransitionTo(VmState.Stopped, $"QEMU exited cleanly (code {exitCode})");
            AppendLog("LIFECYCLE", $"QEMU process exited (code {exitCode}).");
        }
    }

    private async Task ConnectSerialConsoleLoopAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
            _serialClient = client;
            _serialStream = client.GetStream();

            var buffer = new byte[2048];
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await _serialStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                var chunk = Encoding.UTF8.GetString(buffer, 0, read);
                lock (_serialLock)
                {
                    if (_serialBuffer.Length > 256 * 1024)
                    {
                        _serialBuffer.Remove(0, 64 * 1024);
                    }
                    _serialBuffer.Append(chunk);
                }

                try
                {
                    SerialOutputReceived?.Invoke(this, chunk);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _sessionCts.CancelAsync().ConfigureAwait(false);
        try { _serialClient?.Dispose(); } catch { }
        await Qmp.DisposeAsync().ConfigureAwait(false);
        var companion = Interlocked.Exchange(ref _companionResource, null);
        if (companion is not null)
        {
            try { await companion.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        var handle = Interlocked.Exchange(ref _lockHandle, null);
        if (handle is not null)
        {
            try { await handle.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        _logRotator.Dispose();
        _process.Dispose();
        _sessionCts.Dispose();
    }
}

public sealed class QemuProcessManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ManagedVmSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ManagedVmSession> ActiveSessions =>
        _sessions.Values.Where(s => s.StateMachine.IsActiveProcessState).ToList();

    public ManagedVmSession? GetSession(string vmId) =>
        _sessions.GetValueOrDefault(vmId);

    public async Task<ManagedVmSession> StartVmAsync(
        VirtualMachine vm,
        QemuLaunchSpec launchSpec,
        TimeSpan? qmpConnectTimeout = null,
        CancellationToken cancellationToken = default,
        IAsyncDisposable? companionResource = null)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(launchSpec);

        if (_sessions.TryGetValue(vm.Identity.Id, out var existing) && existing.StateMachine.IsActiveProcessState)
        {
            if (companionResource is not null)
            {
                await companionResource.DisposeAsync().ConfigureAwait(false);
            }
            throw new InvalidOperationException($"Virtual machine '{vm.Identity.Name}' is already running.");
        }

        VmExclusiveLockHandle? lockHandle = null;
        LogRotator? logRotator = null;
        Process? proc = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(vm.DirectoryPath))
            {
                lockHandle = await VmLockManager.AcquireExclusiveLockAsync(vm.DirectoryPath, cancellationToken).ConfigureAwait(false);
            }

            var logPath = !string.IsNullOrWhiteSpace(vm.DirectoryPath)
                ? Path.Combine(VmDirectoryLayout.GetLogsDirectory(vm.DirectoryPath), "qemu-runtime.log")
                : Path.Combine(Path.GetTempPath(), $"winqemu-{vm.Identity.Id}.log");

            logRotator = new LogRotator(logPath);
            logRotator.AppendLine("COMMAND", launchSpec.FormattedCommandLine);

            var psi = new ProcessStartInfo
            {
                FileName = launchSpec.ExecutablePath,
                WorkingDirectory = launchSpec.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in launchSpec.ArgumentList)
            {
                psi.ArgumentList.Add(arg);
            }

            proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start QEMU process.");
        }
        catch
        {
            logRotator?.Dispose();
            if (lockHandle is not null)
            {
                await lockHandle.DisposeAsync().ConfigureAwait(false);
            }
            if (companionResource is not null)
            {
                await companionResource.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }

        var qmpClient = new QmpClient();
        var session = new ManagedVmSession(vm, launchSpec, proc, qmpClient, logRotator, lockHandle, companionResource);
        session.AttachProcessStreamsAndEvents();
        _sessions[vm.Identity.Id] = session;

        try
        {
            if (lockHandle is not null)
            {
                DateTimeOffset startTimeUtc = DateTimeOffset.UtcNow;
                try { startTimeUtc = proc.StartTime.ToUniversalTime(); } catch { }
                await lockHandle.WriteRecordAsync(
                    new VmProcessLockRecord
                    {
                        VmId = vm.Identity.Id,
                        ProcessId = proc.Id,
                        ProcessStartTimeUtc = startTimeUtc,
                        QmpPort = launchSpec.Endpoints.QmpPort,
                        VncPort = launchSpec.Endpoints.VncPort,
                        SerialPort = launchSpec.Endpoints.SerialPort,
                        ActiveAccelerator = launchSpec.ActiveAccelerator
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            await qmpClient.ConnectTcpLoopbackAsync(
                launchSpec.Endpoints.QmpPort,
                qmpConnectTimeout ?? TimeSpan.FromSeconds(12),
                cancellationToken).ConfigureAwait(false);

            var status = await qmpClient.QueryStatusAsync(cancellationToken).ConfigureAwait(false);
            var mappedState = session.StateMachine.MapQmpRunState(status.Status);
            session.StateMachine.TransitionTo(
                mappedState == VmState.Starting ? VmState.Running : mappedState,
                $"QMP connected ({status.Status})");

            return session;
        }
        catch (Exception ex)
        {
            _sessions.TryRemove(vm.Identity.Id, out _);
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            var remediation = ErrorTranslator.Translate(ex.Message);
            session.StateMachine.TransitionTo(VmState.Crashed, "Failed during startup/QMP handshake", remediation.UserFriendlyExplanation);
            try { await session.DisposeAsync().ConfigureAwait(false); } catch { }
            throw;
        }
    }

    public async Task<ManagedVmSession?> TryReattachSessionAsync(
        VirtualMachine vm,
        VmProcessLockRecord lockRecord,
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(lockRecord);

        if (_sessions.TryGetValue(vm.Identity.Id, out var existing) && existing.StateMachine.IsActiveProcessState)
        {
            return existing;
        }

        Process? proc = null;
        QmpClient? qmpClient = null;
        LogRotator? logRotator = null;
        ManagedVmSession? session = null;

        try
        {
            proc = Process.GetProcessById(lockRecord.ProcessId);
            if (proc.HasExited || !VmLockManager.IsMatchingQemuProcessAlive(lockRecord))
            {
                proc.Dispose();
                proc = null;
                VmLockManager.ReleaseLockFile(vm.DirectoryPath);
                return null;
            }

            qmpClient = new QmpClient();
            await qmpClient.ConnectTcpLoopbackAsync(
                lockRecord.QmpPort,
                TimeSpan.FromSeconds(3),
                cancellationToken).ConfigureAwait(false);

            var endpoints = new QemuRuntimeEndpoints(
                QmpPort: lockRecord.QmpPort,
                VncPort: lockRecord.VncPort,
                SerialPort: lockRecord.SerialPort,
                GuestAgentPort: null,
                TpmEmulatorPort: null);

            var launchSpec = new QemuLaunchSpec(
                ExecutablePath: executablePath,
                ArgumentList: [],
                WorkingDirectory: vm.DirectoryPath ?? Environment.CurrentDirectory,
                ActiveAccelerator: lockRecord.ActiveAccelerator,
                Endpoints: endpoints,
                FormattedCommandLine: $"{executablePath} (reattached PID {lockRecord.ProcessId})");

            var logPath = !string.IsNullOrWhiteSpace(vm.DirectoryPath)
                ? Path.Combine(VmDirectoryLayout.GetLogsDirectory(vm.DirectoryPath), "qemu-runtime.log")
                : Path.Combine(Path.GetTempPath(), $"winqemu-{vm.Identity.Id}.log");
            logRotator = new LogRotator(logPath);

            session = new ManagedVmSession(vm, launchSpec, proc, qmpClient, logRotator);
            session.AttachProcessStreamsAndEvents();

            var status = await qmpClient.QueryStatusAsync(cancellationToken).ConfigureAwait(false);
            var mappedState = session.StateMachine.MapQmpRunState(status.Status);
            session.StateMachine.TransitionTo(
                mappedState == VmState.Starting ? VmState.Running : mappedState,
                $"Reattached to running QEMU process PID {lockRecord.ProcessId} ({status.Status})");

            _sessions[vm.Identity.Id] = session;
            return session;
        }
        catch
        {
            if (session is not null)
            {
                try { await session.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            else
            {
                if (qmpClient is not null)
                {
                    try { await qmpClient.DisposeAsync().ConfigureAwait(false); } catch { }
                }
                logRotator?.Dispose();
                proc?.Dispose();
            }
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }
        _sessions.Clear();
    }
}
