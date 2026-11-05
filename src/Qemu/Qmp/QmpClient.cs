using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinQemu.Qemu.Qmp;

public sealed record QmpGreeting(
    string QemuVersionString,
    int Major,
    int Minor,
    int Micro);

public sealed record QmpEventMessage(
    string EventName,
    DateTimeOffset TimestampUtc,
    JsonObject? Data,
    string RawJson);

public sealed record QmpStatusResult(
    bool Running,
    string Status,
    bool Singlestep);

public sealed class QmpException(string errorClass, string description)
    : Exception($"QMP error [{errorClass}]: {description}")
{
    public string ErrorClass { get; } = errorClass;
    public string Description { get; } = description;
}

public sealed class QmpClient : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pendingRequests = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private Stream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private TcpClient? _tcpClient;
    private Task? _readLoopTask;
    private long _nextRequestId;
    private int _isConnected;

    public QmpGreeting? Greeting { get; private set; }
    public bool IsConnected => Volatile.Read(ref _isConnected) == 1;

    public event EventHandler<QmpEventMessage>? EventReceived;
    public event EventHandler<string>? RawTrafficLogged;
    public event EventHandler<Exception?>? Disconnected;

    public async Task ConnectTcpLoopbackAsync(
        int port,
        TimeSpan? connectTimeout = null,
        CancellationToken cancellationToken = default)
    {
        if (port is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        var timeout = connectTimeout ?? TimeSpan.FromSeconds(10);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var client = new TcpClient();
            try
            {
                // Strictly connect to IPAddress.Loopback (127.0.0.1) only (§10)
                await client.ConnectAsync(IPAddress.Loopback, port, timeoutCts.Token).ConfigureAwait(false);
                client.NoDelay = true;
                _tcpClient = client;
                await AttachStreamAndNegotiateAsync(client.GetStream(), timeoutCts.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                client.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                client.Dispose();
                lastError = ex;
                await Task.Delay(75, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new TimeoutException(
            $"Timed out connecting to local QMP server on 127.0.0.1:{port}. Last error: {lastError?.Message}",
            lastError);
    }

    public async Task ConnectNamedPipeAsync(
        string pipeName,
        TimeSpan? connectTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        var timeout = connectTimeout ?? TimeSpan.FromSeconds(5);

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            await AttachStreamAndNegotiateAsync(pipe, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task AttachStreamAndNegotiateAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        _reader = new StreamReader(stream, utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
        _writer = new StreamWriter(stream, utf8NoBom, bufferSize: 8192, leaveOpen: true) { AutoFlush = true };

        // 1. Read greeting line before starting read loop
        string? greetingLine = null;
        while (greetingLine is null)
        {
            var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new EndOfStreamException("QMP stream closed before greeting was received.");

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            RawTrafficLogged?.Invoke(this, $"<- {line}");
            var node = JsonNode.Parse(line) as JsonObject
                ?? throw new InvalidDataException("Malformed QMP greeting JSON.");

            if (node["QMP"] is JsonObject qmpObj)
            {
                var verObj = qmpObj["version"]?["qemu"] as JsonObject;
                var pkg = qmpObj["version"]?["package"]?.GetValue<string>() ?? string.Empty;
                var major = verObj?["major"]?.GetValue<int>() ?? 0;
                var minor = verObj?["minor"]?.GetValue<int>() ?? 0;
                var micro = verObj?["micro"]?.GetValue<int>() ?? 0;
                Greeting = new QmpGreeting($"{major}.{minor}.{micro} {pkg}".Trim(), major, minor, micro);
                greetingLine = line;
            }
        }

        Interlocked.Exchange(ref _isConnected, 1);
        _readLoopTask = Task.Run(() => ReadLoopAsync(_disposeCts.Token));

        // 2. Negotiate capabilities
        await ExecuteAsync("qmp_capabilities", arguments: null, timeout: TimeSpan.FromSeconds(5), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<JsonObject> ExecuteAsync(
        string command,
        JsonObject? arguments = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (!IsConnected || _writer is null)
        {
            throw new InvalidOperationException("QMP client is not connected.");
        }

        var reqId = $"req-{Interlocked.Increment(ref _nextRequestId)}";
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[reqId] = tcs;

        var payload = new JsonObject
        {
            ["execute"] = command,
            ["id"] = reqId
        };
        if (arguments is not null)
        {
            payload["arguments"] = arguments;
        }

        var serialized = payload.ToJsonString();
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        timeoutCts.CancelAfter(effectiveTimeout);

        try
        {
            await _writeLock.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            try
            {
                RawTrafficLogged?.Invoke(this, $"-> {serialized}");
                await _writer.WriteLineAsync(serialized.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            await using var reg = timeoutCts.Token.Register(() =>
            {
                if (_pendingRequests.TryRemove(reqId, out var pending))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        pending.TrySetCanceled(cancellationToken);
                    }
                    else
                    {
                        pending.TrySetException(new TimeoutException($"QMP command '{command}' (id={reqId}) timed out after {effectiveTimeout.TotalMilliseconds:0} ms."));
                    }
                }
            });

            var response = await tcs.Task.ConfigureAwait(false);
            if (response["error"] is JsonObject errorObj)
            {
                var errClass = errorObj["class"]?.GetValue<string>() ?? "GenericError";
                var desc = errorObj["desc"]?.GetValue<string>() ?? "Unknown QMP error";
                throw new QmpException(errClass, desc);
            }

            return response;
        }
        finally
        {
            _pendingRequests.TryRemove(reqId, out _);
        }
    }

    public async Task<QmpStatusResult> QueryStatusAsync(CancellationToken cancellationToken = default)
    {
        var resp = await ExecuteAsync("query-status", cancellationToken: cancellationToken).ConfigureAwait(false);
        var ret = resp["return"] as JsonObject;
        var running = ret?["running"]?.GetValue<bool>() ?? false;
        var status = ret?["status"]?.GetValue<string>() ?? "unknown";
        var singlestep = ret?["singlestep"]?.GetValue<bool>() ?? false;
        return new QmpStatusResult(running, status, singlestep);
    }

    public Task PauseAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("stop", cancellationToken: cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("cont", cancellationToken: cancellationToken);

    public Task SystemPowerdownAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("system_powerdown", cancellationToken: cancellationToken);

    public Task SystemResetAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("system_reset", cancellationToken: cancellationToken);

    public Task QuitAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("quit", cancellationToken: cancellationToken);

    public async Task ScreendumpAsync(string filename, string format = "png", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        var args = new JsonObject
        {
            ["filename"] = filename
        };
        if (!string.Equals(format, "ppm", StringComparison.OrdinalIgnoreCase))
        {
            args["format"] = format;
        }

        try
        {
            await ExecuteAsync("screendump", args, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (QmpException) when (!string.Equals(format, "ppm", StringComparison.OrdinalIgnoreCase))
        {
            // Fallback for QEMU builds where screendump only accepts PPM format
            var fallbackArgs = new JsonObject { ["filename"] = filename };
            await ExecuteAsync("screendump", fallbackArgs, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<string> HumanMonitorCommandAsync(string commandLine, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        var args = new JsonObject
        {
            ["command-line"] = commandLine
        };
        var resp = await ExecuteAsync("human-monitor-command", args, timeout: TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);
        return resp["return"]?.GetValue<string>() ?? string.Empty;
    }

    public async Task EjectMediaAsync(string deviceOrId, bool force = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceOrId);
        var args = new JsonObject
        {
            ["id"] = deviceOrId,
            ["force"] = force
        };
        try
        {
            await ExecuteAsync("eject", args, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (QmpException)
        {
            var fallback = new JsonObject
            {
                ["device"] = deviceOrId,
                ["force"] = force
            };
            await ExecuteAsync("eject", fallback, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ChangeMediaAsync(string deviceOrId, string targetFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceOrId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFilePath);

        try
        {
            var args = new JsonObject
            {
                ["id"] = deviceOrId,
                ["filename"] = targetFilePath,
                ["format"] = "raw"
            };
            await ExecuteAsync("blockdev-change-medium", args, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (QmpException)
        {
            var fallback = new JsonObject
            {
                ["device"] = deviceOrId,
                ["filename"] = targetFilePath,
                ["format"] = "raw"
            };
            await ExecuteAsync("blockdev-change-medium", fallback, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes a command from the Developer QMP Inspector (§53), blocking destructive commands
    /// unless raw destructive mode is explicitly enabled.
    /// </summary>
    public async Task<string> ExecuteInspectorCommandAsync(
        string commandOrJson,
        bool allowDestructiveRawMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandOrJson);
        var trimmed = commandOrJson.Trim();

        string commandName;
        JsonObject? args = null;

        if (trimmed.StartsWith('{'))
        {
            var parsed = JsonNode.Parse(trimmed) as JsonObject
                ?? throw new InvalidDataException("Inspector input is not a valid JSON object.");
            commandName = parsed["execute"]?.GetValue<string>()
                ?? throw new InvalidDataException("JSON command must contain an 'execute' property.");
            args = parsed["arguments"] as JsonObject;
        }
        else
        {
            commandName = trimmed;
        }

        string[] destructiveCommands =
        [
            "quit", "system_powerdown", "system_reset", "blockdev-del",
            "drive_del", "device_del", "human-monitor-command", "x-oob-test"
        ];

        if (!allowDestructiveRawMode &&
            destructiveCommands.Contains(commandName, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Command '{commandName}' is potentially destructive. Enable 'Allow destructive raw QMP commands' in Developer Mode to run it (§53).");
        }

        var response = await ExecuteAsync(commandName, args, cancellationToken: cancellationToken).ConfigureAwait(false);
        return response.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        Exception? disconnectReason = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested && _reader is not null)
            {
                var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    disconnectReason = new EndOfStreamException("QMP connection closed by remote host.");
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                RawTrafficLogged?.Invoke(this, $"<- {line}");

                JsonObject? obj;
                try
                {
                    obj = JsonNode.Parse(line) as JsonObject;
                }
                catch (Exception ex)
                {
                    RawTrafficLogged?.Invoke(this, $"[QMP Malformed JSON Ignored]: {ex.Message}");
                    continue;
                }

                if (obj is null)
                {
                    continue;
                }

                // 1. Check if this is an asynchronous QMP event
                if (obj["event"]?.GetValue<string>() is { } eventName && !string.IsNullOrWhiteSpace(eventName))
                {
                    var tsObj = obj["timestamp"] as JsonObject;
                    var seconds = tsObj?["seconds"]?.GetValue<long>() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var micros = tsObj?["microseconds"]?.GetValue<long>() ?? 0;
                    var timestamp = DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(micros * 10);
                    var data = obj["data"] as JsonObject;

                    var evt = new QmpEventMessage(eventName, timestamp, data, line);
                    try
                    {
                        EventReceived?.Invoke(this, evt);
                    }
                    catch
                    {
                    }
                    continue;
                }

                // 2. Check if this is a command response ("return" or "error")
                if (obj.ContainsKey("return") || obj.ContainsKey("error"))
                {
                    var id = obj["id"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(id) && _pendingRequests.TryRemove(id, out var tcs))
                    {
                        tcs.TrySetResult(obj);
                    }
                    else if (string.IsNullOrWhiteSpace(id) && _pendingRequests.Count == 1)
                    {
                        // Handle missing request ID when only 1 command is in flight (§70)
                        var singleKey = _pendingRequests.Keys.FirstOrDefault();
                        if (singleKey is not null && _pendingRequests.TryRemove(singleKey, out var singleTcs))
                        {
                            singleTcs.TrySetResult(obj);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            disconnectReason = ex;
        }
        finally
        {
            Interlocked.Exchange(ref _isConnected, 0);
            var fault = disconnectReason ?? new IOException("QMP client disconnected.");
            foreach (var kvp in _pendingRequests)
            {
                if (_pendingRequests.TryRemove(kvp.Key, out var pending))
                {
                    pending.TrySetException(fault);
                }
            }

            try
            {
                Disconnected?.Invoke(this, disconnectReason);
            }
            catch
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _isConnected, 0);
        await _disposeCts.CancelAsync().ConfigureAwait(false);

        try { _tcpClient?.Dispose(); } catch { }
        try { if (_stream is not null) await _stream.DisposeAsync().ConfigureAwait(false); } catch { }

        if (_readLoopTask is not null)
        {
            try { await _readLoopTask.ConfigureAwait(false); } catch { }
        }

        _writeLock.Dispose();
        _disposeCts.Dispose();
    }
}
