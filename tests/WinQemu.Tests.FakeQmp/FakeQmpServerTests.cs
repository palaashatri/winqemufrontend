using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using WinQemu.Qemu.Qmp;
using Xunit;

namespace WinQemu.Tests.FakeQmp;

public sealed class DeterministicFakeQmpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private TcpClient? _acceptedClient;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Task? _serverTask;

    public DeterministicFakeQmpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }
    public Func<JsonObject, StreamWriter, Task>? CustomCommandHandler { get; set; }

    public void Start()
    {
        _serverTask = Task.Run(AcceptAndServeLoopAsync);
    }

    public async Task SendRawLineAsync(string rawLine)
    {
        if (_writer is not null)
        {
            await _writer.WriteLineAsync(rawLine).ConfigureAwait(false);
        }
    }

    public void SimulateAbruptCrash()
    {
        try
        {
            _acceptedClient?.Client.Close(0);
            _acceptedClient?.Dispose();
        }
        catch
        {
        }
    }

    private async Task AcceptAndServeLoopAsync()
    {
        try
        {
            _acceptedClient = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            var stream = _acceptedClient.GetStream();
            var utf8 = new UTF8Encoding(false);
            _reader = new StreamReader(stream, utf8, false, 4096, leaveOpen: true);
            _writer = new StreamWriter(stream, utf8, 4096, leaveOpen: true) { AutoFlush = true };

            // Send QMP Greeting
            await _writer.WriteLineAsync(
                """{"QMP":{"version":{"qemu":{"micro":0,"minor":1,"major":11},"package":"v11.1.0-fake"},"capabilities":["oob"]}}""")
                .ConfigureAwait(false);

            while (!_cts.Token.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                var req = JsonNode.Parse(line) as JsonObject;
                if (req is null)
                {
                    continue;
                }

                var cmd = req["execute"]?.GetValue<string>() ?? string.Empty;
                var id = req["id"]?.GetValue<string>();

                if (cmd == "qmp_capabilities")
                {
                    await _writer.WriteLineAsync($$"""{"return":{},"id":"{{id}}" }""").ConfigureAwait(false);
                    continue;
                }

                if (CustomCommandHandler is not null)
                {
                    await CustomCommandHandler(req, _writer).ConfigureAwait(false);
                    continue;
                }

                // Default happy-path handler
                switch (cmd)
                {
                    case "query-status":
                        await _writer.WriteLineAsync($$"""{"return":{"running":true,"singlestep":false,"status":"running"},"id":"{{id}}" }""").ConfigureAwait(false);
                        break;
                    case "stop":
                        await _writer.WriteLineAsync("""{"event":"STOP","timestamp":{"seconds":1730000000,"microseconds":100}}""").ConfigureAwait(false);
                        await _writer.WriteLineAsync($$"""{"return":{},"id":"{{id}}" }""").ConfigureAwait(false);
                        break;
                    case "cont":
                        await _writer.WriteLineAsync("""{"event":"RESUME","timestamp":{"seconds":1730000001,"microseconds":200}}""").ConfigureAwait(false);
                        await _writer.WriteLineAsync($$"""{"return":{},"id":"{{id}}" }""").ConfigureAwait(false);
                        break;
                    default:
                        await _writer.WriteLineAsync($$"""{"return":{},"id":"{{id}}" }""").ConfigureAwait(false);
                        break;
                }
            }
        }
        catch
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try { _listener.Stop(); } catch { }
        try { _acceptedClient?.Dispose(); } catch { }
        if (_serverTask is not null)
        {
            try { await _serverTask.ConfigureAwait(false); } catch { }
        }
        _cts.Dispose();
    }
}

public sealed class FakeQmpServerTests
{
    [Fact]
    public async Task ValidNegotiation_And_EventsBetweenResponses()
    {
        await using var server = new DeterministicFakeQmpServer();
        server.Start();

        await using var client = new QmpClient();
        var events = new List<string>();
        client.EventReceived += (_, e) => events.Add(e.EventName);

        await client.ConnectTcpLoopbackAsync(server.Port);
        Assert.NotNull(client.Greeting);
        Assert.Equal(11, client.Greeting!.Major);

        var status = await client.QueryStatusAsync();
        Assert.True(status.Running);
        Assert.Equal("running", status.Status);

        await client.PauseAsync();
        await client.ResumeAsync();

        Assert.Contains("STOP", events);
        Assert.Contains("RESUME", events);
    }

    [Fact]
    public async Task MalformedJson_And_RapidEvents_DoNotBreakClient()
    {
        await using var server = new DeterministicFakeQmpServer();
        server.CustomCommandHandler = async (req, writer) =>
        {
            var id = req["id"]?.GetValue<string>();
            // Inject malformed JSON noise + 25 rapid events before sending the actual response
            await writer.WriteLineAsync("{malformed_json: [1, 2, ");
            for (var i = 0; i < 25; i++)
            {
                await writer.WriteLineAsync($$"""{"event":"RTC_CHANGE","data":{"offset":{{i}}},"timestamp":{"seconds":1730000000,"microseconds":{{i}} } }""");
            }
            await writer.WriteLineAsync($$"""{"return":{"running":true,"singlestep":false,"status":"running"},"id":"{{id}}" }""");
        };
        server.Start();

        await using var client = new QmpClient();
        var eventCount = 0;
        client.EventReceived += (_, _) => Interlocked.Increment(ref eventCount);

        await client.ConnectTcpLoopbackAsync(server.Port);
        var status = await client.QueryStatusAsync();

        Assert.True(status.Running);
        Assert.Equal(25, eventCount);
    }

    [Fact]
    public async Task OutOfOrderResponses_MatchByRequestIdCorrectly()
    {
        await using var server = new DeterministicFakeQmpServer();
        var heldRequests = new List<(string Id, string Cmd)>();

        server.CustomCommandHandler = async (req, writer) =>
        {
            var id = req["id"]!.GetValue<string>();
            var cmd = req["execute"]!.GetValue<string>();
            lock (heldRequests)
            {
                heldRequests.Add((id, cmd));
                if (heldRequests.Count < 2)
                {
                    return;
                }
            }

            // Reply in reverse order (second request first, first request second)
            var second = heldRequests[1];
            var first = heldRequests[0];
            await writer.WriteLineAsync($$"""{"return":{"cmd":"{{second.Cmd}}"},"id":"{{second.Id}}"}""");
            await writer.WriteLineAsync($$"""{"return":{"cmd":"{{first.Cmd}}"},"id":"{{first.Id}}"}""");
        };
        server.Start();

        await using var client = new QmpClient();
        await client.ConnectTcpLoopbackAsync(server.Port);

        var task1 = client.ExecuteAsync("cmd-alpha");
        var task2 = client.ExecuteAsync("cmd-beta");

        var results = await Task.WhenAll(task1, task2);
        Assert.Equal("cmd-alpha", results[0]["return"]?["cmd"]?.GetValue<string>());
        Assert.Equal("cmd-beta", results[1]["return"]?["cmd"]?.GetValue<string>());
    }

    [Fact]
    public async Task MissingRequestId_And_UnexpectedQemuError_HandledDeterministically()
    {
        await using var server = new DeterministicFakeQmpServer();
        server.CustomCommandHandler = async (req, writer) =>
        {
            var cmd = req["execute"]?.GetValue<string>();
            if (cmd == "missing-id-cmd")
            {
                // Response missing "id" field
                await writer.WriteLineAsync("""{"return":{"ok":true}}""");
            }
            else
            {
                var id = req["id"]?.GetValue<string>();
                await writer.WriteLineAsync($$"""{"error":{"class":"DeviceNotFound","desc":"Device 'foo' not found"},"id":"{{id}}"}""");
            }
        };
        server.Start();

        await using var client = new QmpClient();
        await client.ConnectTcpLoopbackAsync(server.Port);

        var resp = await client.ExecuteAsync("missing-id-cmd");
        Assert.True(resp["return"]?["ok"]?.GetValue<bool>());

        var ex = await Assert.ThrowsAsync<QmpException>(() => client.ExecuteAsync("bad-device-cmd"));
        Assert.Equal("DeviceNotFound", ex.ErrorClass);
    }

    [Fact]
    public async Task Timeout_And_ServerCrashDisconnect_SurfaceCleanExceptions()
    {
        await using var server = new DeterministicFakeQmpServer();
        server.CustomCommandHandler = (req, _) =>
        {
            var cmd = req["execute"]?.GetValue<string>();
            if (cmd == "hang-forever")
            {
                // Intentionally omit writing a response without blocking the server read loop
                return Task.CompletedTask;
            }
            if (cmd == "crash-now")
            {
                server.SimulateAbruptCrash();
            }
            return Task.CompletedTask;
        };
        server.Start();

        await using var client = new QmpClient();
        await client.ConnectTcpLoopbackAsync(server.Port);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            client.ExecuteAsync("hang-forever", timeout: TimeSpan.FromMilliseconds(200)));

        var disconnectEx = await Assert.ThrowsAsync<IOException>(() =>
            client.ExecuteAsync("crash-now", timeout: TimeSpan.FromSeconds(2)));
        Assert.False(string.IsNullOrWhiteSpace(disconnectEx.Message));
    }

    [Fact]
    public async Task InspectorCommandGuard_BlocksDestructiveCommandsUnlessExplicitlyEnabled()
    {
        await using var server = new DeterministicFakeQmpServer();
        server.Start();

        await using var client = new QmpClient();
        await client.ConnectTcpLoopbackAsync(server.Port);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ExecuteInspectorCommandAsync("quit", allowDestructiveRawMode: false));

        var allowed = await client.ExecuteInspectorCommandAsync("quit", allowDestructiveRawMode: true);
        Assert.Contains("return", allowed);
    }
}
