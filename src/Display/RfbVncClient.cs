using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WinQemu.Display;

public readonly record struct DirtyRect(int X, int Y, int Width, int Height);

public sealed class FramebufferUpdateEventArgs(
    int width,
    int height,
    byte[] bgraPixels,
    IReadOnlyList<DirtyRect> dirtyRects) : EventArgs
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] BgraPixels { get; } = bgraPixels;
    public IReadOnlyList<DirtyRect> DirtyRects { get; } = dirtyRects;
}

/// <summary>
/// Production-quality localhost-only RFB 3.8 VNC client (§28, §29, §31, §32).
/// Requests 32bpp BGRA pixels matching WinUI 3 WriteableBitmap/Direct3D surfaces directly.
/// </summary>
public sealed class RfbVncClient : IAsyncDisposable
{
    private const int EncodingRaw = 0;
    private const int EncodingCopyRect = 1;
    private const int EncodingDesktopSize = -223;
    private const int EncodingCursor = -239;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Lock _fbLock = new();
    private readonly HashSet<uint> _pressedKeysyms = [];
    private readonly CancellationTokenSource _disposeCts = new();
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private Task? _receiveLoopTask;
    private byte[] _framebuffer = [];
    private int _isConnected;
    private long _framesReceived;

    public int FramebufferWidth { get; private set; }
    public int FramebufferHeight { get; private set; }
    public string ServerDesktopName { get; private set; } = string.Empty;
    public bool IsConnected => Volatile.Read(ref _isConnected) == 1;
    public long FramesReceived => Interlocked.Read(ref _framesReceived);

    public event EventHandler<FramebufferUpdateEventArgs>? FrameUpdated;
    public event EventHandler<string>? ServerClipboardReceived;
    public event EventHandler<Exception?>? Disconnected;

    public async Task ConnectLocalhostAsync(
        int port,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (port is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(effectiveTimeout);

        var deadline = DateTimeOffset.UtcNow + effectiveTimeout;
        Exception? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var client = new TcpClient();
            try
            {
                // Strictly connect to 127.0.0.1 only (§29)
                await client.ConnectAsync(IPAddress.Loopback, port, timeoutCts.Token).ConfigureAwait(false);
                client.NoDelay = true;
                client.ReceiveBufferSize = 256 * 1024;
                _tcpClient = client;
                _stream = client.GetStream();

                await PerformHandshakeAsync(_stream, timeoutCts.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref _isConnected, 1);
                _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_disposeCts.Token));

                // Request full initial frame
                await RequestFramebufferUpdateAsync(incremental: false, timeoutCts.Token).ConfigureAwait(false);
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
                await Task.Delay(80, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new TimeoutException($"Failed to connect to local VNC server on 127.0.0.1:{port}: {lastError?.Message}", lastError);
    }

    public byte[] SnapshotCurrentFramebuffer(out int width, out int height)
    {
        lock (_fbLock)
        {
            width = FramebufferWidth;
            height = FramebufferHeight;
            var copy = new byte[_framebuffer.Length];
            Buffer.BlockCopy(_framebuffer, 0, copy, 0, _framebuffer.Length);
            return copy;
        }
    }

    public async Task RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken = default)
    {
        if (!IsConnected || _stream is null)
        {
            return;
        }

        var msg = new byte[10];
        msg[0] = 3; // FramebufferUpdateRequest
        msg[1] = incremental ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(4, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(6, 2), (ushort)Math.Max(1, FramebufferWidth));
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(8, 2), (ushort)Math.Max(1, FramebufferHeight));

        await SendBytesAsync(msg, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendKeyEventAsync(uint keysym, bool isDown, CancellationToken cancellationToken = default)
    {
        if (!IsConnected || _stream is null)
        {
            return;
        }

        lock (_pressedKeysyms)
        {
            if (isDown)
            {
                _pressedKeysyms.Add(keysym);
            }
            else
            {
                _pressedKeysyms.Remove(keysym);
            }
        }

        var msg = new byte[8];
        msg[0] = 4; // KeyEvent
        msg[1] = isDown ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4, 4), keysym);
        await SendBytesAsync(msg, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases all currently pressed keys when focus leaves the guest view so keys never stick (§31).
    /// </summary>
    public async Task ReleaseAllPressedKeysAsync(CancellationToken cancellationToken = default)
    {
        uint[] keysToRelease;
        lock (_pressedKeysyms)
        {
            keysToRelease = _pressedKeysyms.ToArray();
            _pressedKeysyms.Clear();
        }

        foreach (var key in keysToRelease)
        {
            await SendKeyEventAsync(key, isDown: false, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SendCtrlAltDeleteAsync(CancellationToken cancellationToken = default)
    {
        const uint XK_Control_L = 0xFFE3;
        const uint XK_Alt_L = 0xFFE9;
        const uint XK_Delete = 0xFFFF;

        await SendKeyEventAsync(XK_Control_L, true, cancellationToken).ConfigureAwait(false);
        await SendKeyEventAsync(XK_Alt_L, true, cancellationToken).ConfigureAwait(false);
        await SendKeyEventAsync(XK_Delete, true, cancellationToken).ConfigureAwait(false);
        await Task.Delay(40, cancellationToken).ConfigureAwait(false);
        await SendKeyEventAsync(XK_Delete, false, cancellationToken).ConfigureAwait(false);
        await SendKeyEventAsync(XK_Alt_L, false, cancellationToken).ConfigureAwait(false);
        await SendKeyEventAsync(XK_Control_L, false, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendAsciiTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!IsConnected || string.IsNullOrEmpty(text))
        {
            return;
        }

        const uint XK_Shift_L = 0xFFE1;
        const uint XK_Return = 0xFF0D;

        foreach (var ch in text)
        {
            if (ch == '\r')
            {
                continue;
            }
            if (ch == '\n')
            {
                await SendKeyEventAsync(XK_Return, true, cancellationToken).ConfigureAwait(false);
                await SendKeyEventAsync(XK_Return, false, cancellationToken).ConfigureAwait(false);
                await Task.Delay(15, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var needsShift = char.IsUpper(ch) || ch is '!' or '@' or '#' or '$' or '%' or '^' or '&' or '*' or '(' or ')' or '_' or '+' or '{' or '}' or '|' or ':' or '"' or '<' or '>' or '?' or '~';
            if (needsShift)
            {
                await SendKeyEventAsync(XK_Shift_L, true, cancellationToken).ConfigureAwait(false);
            }

            await SendKeyEventAsync(ch, true, cancellationToken).ConfigureAwait(false);
            await SendKeyEventAsync(ch, false, cancellationToken).ConfigureAwait(false);

            if (needsShift)
            {
                await SendKeyEventAsync(XK_Shift_L, false, cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(8, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SendPointerEventAsync(int x, int y, byte buttonMask, CancellationToken cancellationToken = default)
    {
        if (!IsConnected || _stream is null || FramebufferWidth <= 0 || FramebufferHeight <= 0)
        {
            return;
        }

        var clampedX = (ushort)Math.Clamp(x, 0, FramebufferWidth - 1);
        var clampedY = (ushort)Math.Clamp(y, 0, FramebufferHeight - 1);

        var msg = new byte[6];
        msg[0] = 5; // PointerEvent
        msg[1] = buttonMask;
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2, 2), clampedX);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(4, 2), clampedY);
        await SendBytesAsync(msg, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends host clipboard text to guest via RFB ClientCutText (§32).
    /// Never throws if clipboard synchronization fails.
    /// </summary>
    public async Task SendClipboardTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!IsConnected || _stream is null || string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            var bytes = Encoding.Latin1.GetBytes(text.Length > 65536 ? text[..65536] : text);
            var msg = new byte[8 + bytes.Length];
            msg[0] = 6; // ClientCutText
            BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4, 4), (uint)bytes.Length);
            bytes.CopyTo(msg.AsSpan(8));
            await SendBytesAsync(msg, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Per §32: Clipboard failures must never crash or hang the VM.
        }
    }

    public static uint MapWindowsVirtualKeyToX11Keysym(int virtualKey, bool shiftPressed = false)
    {
        return virtualKey switch
        {
            0x08 => 0xFF08, // Backspace
            0x09 => 0xFF09, // Tab
            0x0D => 0xFF0D, // Enter
            0x1B => 0xFF1B, // Escape
            0x20 => 0x0020, // Space
            0x21 => 0xFF55, // PageUp
            0x22 => 0xFF56, // PageDown
            0x23 => 0xFF57, // End
            0x24 => 0xFF50, // Home
            0x25 => 0xFF51, // Left
            0x26 => 0xFF52, // Up
            0x27 => 0xFF53, // Right
            0x28 => 0xFF54, // Down
            0x2D => 0xFF63, // Insert
            0x2E => 0xFFFF, // Delete
            0x10 or 0xA0 => 0xFFE1, // Shift_L
            0xA1 => 0xFFE2, // Shift_R
            0x11 or 0xA2 => 0xFFE3, // Control_L
            0xA3 => 0xFFE4, // Control_R
            0x12 or 0xA4 => 0xFFE9, // Alt_L
            0xA5 => 0xFFEA, // Alt_R
            0x5B => 0xFFEB, // Super_L (Windows Key)
            0x5C => 0xFFEC, // Super_R
            >= 0x70 and <= 0x7B => (uint)(0xFFBE + (virtualKey - 0x70)), // F1..F12
            >= 0x30 and <= 0x39 => MapDigitOrShiftedSymbol(virtualKey, shiftPressed),
            >= 0x41 and <= 0x5A => (uint)(shiftPressed ? virtualKey : (virtualKey + 32)), // A..Z / a..z
            0xBA => shiftPressed ? (uint)':' : ';',
            0xBB => shiftPressed ? (uint)'+' : '=',
            0xBC => shiftPressed ? (uint)'<' : ',',
            0xBD => shiftPressed ? (uint)'_' : '-',
            0xBE => shiftPressed ? (uint)'>' : '.',
            0xBF => shiftPressed ? (uint)'?' : '/',
            0xC0 => shiftPressed ? (uint)'~' : '`',
            0xDB => shiftPressed ? (uint)'{' : '[',
            0xDC => shiftPressed ? (uint)'|' : '\\',
            0xDD => shiftPressed ? (uint)'}' : ']',
            0xDE => shiftPressed ? (uint)'"' : '\'',
            _ => (uint)virtualKey
        };
    }

    private static uint MapDigitOrShiftedSymbol(int vk, bool shift)
    {
        if (!shift)
        {
            return (uint)vk;
        }

        return (vk - 0x30) switch
        {
            1 => '!',
            2 => '@',
            3 => '#',
            4 => '$',
            5 => '%',
            6 => '^',
            7 => '&',
            8 => '*',
            9 => '(',
            0 => ')',
            _ => (uint)vk
        };
    }

    private async Task PerformHandshakeAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        // 1. Read Server ProtocolVersion (12 bytes: "RFB 003.008\n")
        var versionBuf = new byte[12];
        await ReadExactAsync(stream, versionBuf, cancellationToken).ConfigureAwait(false);
        var versionStr = Encoding.ASCII.GetString(versionBuf);
        if (!versionStr.StartsWith("RFB ", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Invalid VNC server greeting: '{versionStr}'");
        }

        // Echo RFB 003.008\n
        var clientVersion = Encoding.ASCII.GetBytes("RFB 003.008\n");
        await stream.WriteAsync(clientVersion, cancellationToken).ConfigureAwait(false);

        // 2. Security negotiation
        var secCountBuf = new byte[1];
        await ReadExactAsync(stream, secCountBuf, cancellationToken).ConfigureAwait(false);
        var secCount = secCountBuf[0];
        if (secCount == 0)
        {
            var reasonLenBuf = new byte[4];
            await ReadExactAsync(stream, reasonLenBuf, cancellationToken).ConfigureAwait(false);
            var reasonLen = BinaryPrimitives.ReadInt32BigEndian(reasonLenBuf);
            var reasonBytes = new byte[Math.Clamp(reasonLen, 0, 4096)];
            await ReadExactAsync(stream, reasonBytes, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"VNC server rejected connection: {Encoding.UTF8.GetString(reasonBytes)}");
        }

        var secTypes = new byte[secCount];
        await ReadExactAsync(stream, secTypes, cancellationToken).ConfigureAwait(false);
        if (!secTypes.Contains((byte)1))
        {
            throw new InvalidOperationException("Local VNC server does not offer SecurityType None (1).");
        }

        // Select SecurityType = 1 (None)
        await stream.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false);

        // Read 4-byte SecurityResult (0 = OK)
        var secResultBuf = new byte[4];
        await ReadExactAsync(stream, secResultBuf, cancellationToken).ConfigureAwait(false);
        var secResult = BinaryPrimitives.ReadUInt32BigEndian(secResultBuf);
        if (secResult != 0)
        {
            throw new InvalidOperationException($"VNC security handshake failed with status {secResult}.");
        }

        // 3. Send ClientInit (shared = 1)
        await stream.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false);

        // 4. Read ServerInit (24 bytes + desktop name)
        var serverInitBuf = new byte[24];
        await ReadExactAsync(stream, serverInitBuf, cancellationToken).ConfigureAwait(false);
        var width = BinaryPrimitives.ReadUInt16BigEndian(serverInitBuf.AsSpan(0, 2));
        var height = BinaryPrimitives.ReadUInt16BigEndian(serverInitBuf.AsSpan(2, 2));
        var nameLen = BinaryPrimitives.ReadInt32BigEndian(serverInitBuf.AsSpan(20, 4));
        if (nameLen is > 0 and <= 4096)
        {
            var nameBytes = new byte[nameLen];
            await ReadExactAsync(stream, nameBytes, cancellationToken).ConfigureAwait(false);
            ServerDesktopName = Encoding.UTF8.GetString(nameBytes);
        }

        lock (_fbLock)
        {
            FramebufferWidth = width;
            FramebufferHeight = height;
            _framebuffer = new byte[width * height * 4];
        }

        // 5. Send SetPixelFormat (32bpp BGRA8 little-endian)
        var spf = new byte[20];
        spf[0] = 0; // SetPixelFormat
        spf[4] = 32; // bits-per-pixel
        spf[5] = 24; // depth
        spf[6] = 0;  // big-endian-flag = false
        spf[7] = 1;  // true-color-flag = true
        BinaryPrimitives.WriteUInt16BigEndian(spf.AsSpan(8, 2), 255);  // red-max
        BinaryPrimitives.WriteUInt16BigEndian(spf.AsSpan(10, 2), 255); // green-max
        BinaryPrimitives.WriteUInt16BigEndian(spf.AsSpan(12, 2), 255); // blue-max
        spf[14] = 16; // red-shift (BGRA in little-endian memory: B=0, G=8, R=16, A=24)
        spf[15] = 8;  // green-shift
        spf[16] = 0;  // blue-shift
        await stream.WriteAsync(spf, cancellationToken).ConfigureAwait(false);

        // 6. Send SetEncodings (Raw, CopyRect, DesktopSize, Cursor)
        int[] encodings = [EncodingCopyRect, EncodingRaw, EncodingDesktopSize, EncodingCursor];
        var se = new byte[4 + encodings.Length * 4];
        se[0] = 2; // SetEncodings
        BinaryPrimitives.WriteUInt16BigEndian(se.AsSpan(2, 2), (ushort)encodings.Length);
        for (var i = 0; i < encodings.Length; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(se.AsSpan(4 + i * 4, 4), encodings[i]);
        }
        await stream.WriteAsync(se, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        Exception? fault = null;
        var msgTypeBuf = new byte[1];
        try
        {
            while (!cancellationToken.IsCancellationRequested && _stream is not null)
            {
                await ReadExactAsync(_stream, msgTypeBuf, cancellationToken).ConfigureAwait(false);
                switch (msgTypeBuf[0])
                {
                    case 0: // FramebufferUpdate
                        await HandleFramebufferUpdateAsync(_stream, cancellationToken).ConfigureAwait(false);
                        // Request next incremental frame after short pacing interval (~60 FPS max)
                        await Task.Delay(16, cancellationToken).ConfigureAwait(false);
                        await RequestFramebufferUpdateAsync(incremental: true, cancellationToken).ConfigureAwait(false);
                        break;

                    case 1: // SetColourMapEntries
                        var cmapHdr = new byte[5];
                        await ReadExactAsync(_stream, cmapHdr, cancellationToken).ConfigureAwait(false);
                        var numColors = BinaryPrimitives.ReadUInt16BigEndian(cmapHdr.AsSpan(3, 2));
                        var cmapBytes = new byte[numColors * 6];
                        await ReadExactAsync(_stream, cmapBytes, cancellationToken).ConfigureAwait(false);
                        break;

                    case 2: // Bell
                        break;

                    case 3: // ServerCutText (§32)
                        var cutHdr = new byte[7];
                        await ReadExactAsync(_stream, cutHdr, cancellationToken).ConfigureAwait(false);
                        var cutLen = BinaryPrimitives.ReadInt32BigEndian(cutHdr.AsSpan(3, 4));
                        if (cutLen is > 0 and <= 1024 * 1024)
                        {
                            var cutBytes = new byte[cutLen];
                            await ReadExactAsync(_stream, cutBytes, cancellationToken).ConfigureAwait(false);
                            var text = Encoding.Latin1.GetString(cutBytes);
                            try { ServerClipboardReceived?.Invoke(this, text); } catch { }
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            fault = ex;
        }
        finally
        {
            Interlocked.Exchange(ref _isConnected, 0);
            try { Disconnected?.Invoke(this, fault); } catch { }
        }
    }

    private async Task HandleFramebufferUpdateAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var hdr = new byte[3];
        await ReadExactAsync(stream, hdr, cancellationToken).ConfigureAwait(false);
        var numRects = BinaryPrimitives.ReadUInt16BigEndian(hdr.AsSpan(1, 2));
        var dirtyRects = new List<DirtyRect>(numRects);
        var rectHdr = new byte[12];

        for (var i = 0; i < numRects; i++)
        {
            await ReadExactAsync(stream, rectHdr, cancellationToken).ConfigureAwait(false);
            var rx = BinaryPrimitives.ReadUInt16BigEndian(rectHdr.AsSpan(0, 2));
            var ry = BinaryPrimitives.ReadUInt16BigEndian(rectHdr.AsSpan(2, 2));
            var rw = BinaryPrimitives.ReadUInt16BigEndian(rectHdr.AsSpan(4, 2));
            var rh = BinaryPrimitives.ReadUInt16BigEndian(rectHdr.AsSpan(6, 2));
            var encoding = BinaryPrimitives.ReadInt32BigEndian(rectHdr.AsSpan(8, 4));

            if (encoding == EncodingDesktopSize)
            {
                lock (_fbLock)
                {
                    if (rw > 0 && rh > 0 && (rw != FramebufferWidth || rh != FramebufferHeight))
                    {
                        FramebufferWidth = rw;
                        FramebufferHeight = rh;
                        _framebuffer = new byte[rw * rh * 4];
                    }
                }
                dirtyRects.Add(new DirtyRect(0, 0, FramebufferWidth, FramebufferHeight));
                continue;
            }

            if (encoding == EncodingCursor)
            {
                var pixelBytesCount = rw * rh * 4;
                var maskBytesCount = ((rw + 7) / 8) * rh;
                var cursorBuf = new byte[pixelBytesCount + maskBytesCount];
                if (cursorBuf.Length > 0)
                {
                    await ReadExactAsync(stream, cursorBuf, cancellationToken).ConfigureAwait(false);
                }
                continue;
            }

            if (encoding == EncodingCopyRect)
            {
                var copySrcBuf = new byte[4];
                await ReadExactAsync(stream, copySrcBuf, cancellationToken).ConfigureAwait(false);
                var srcX = BinaryPrimitives.ReadUInt16BigEndian(copySrcBuf.AsSpan(0, 2));
                var srcY = BinaryPrimitives.ReadUInt16BigEndian(copySrcBuf.AsSpan(2, 2));

                lock (_fbLock)
                {
                    CopyRectangleLocked(srcX, srcY, rx, ry, rw, rh);
                }
                dirtyRects.Add(new DirtyRect(rx, ry, rw, rh));
                continue;
            }

            if (encoding == EncodingRaw)
            {
                var rectBytes = new byte[rw * rh * 4];
                await ReadExactAsync(stream, rectBytes, cancellationToken).ConfigureAwait(false);

                // Force alpha channel = 255 so WinUI 3 WriteableBitmap renders opaque pixels
                for (var p = 3; p < rectBytes.Length; p += 4)
                {
                    rectBytes[p] = 255;
                }

                lock (_fbLock)
                {
                    BlitRawRectangleLocked(rx, ry, rw, rh, rectBytes);
                }
                dirtyRects.Add(new DirtyRect(rx, ry, rw, rh));
            }
        }

        if (dirtyRects.Count > 0)
        {
            Interlocked.Increment(ref _framesReceived);
            byte[] fbSnapshot;
            int w, h;
            lock (_fbLock)
            {
                w = FramebufferWidth;
                h = FramebufferHeight;
                fbSnapshot = _framebuffer;
            }

            try
            {
                FrameUpdated?.Invoke(this, new FramebufferUpdateEventArgs(w, h, fbSnapshot, dirtyRects));
            }
            catch
            {
            }
        }
    }

    private void BlitRawRectangleLocked(int rx, int ry, int rw, int rh, byte[] rectBytes)
    {
        var fbW = FramebufferWidth;
        var fbH = FramebufferHeight;
        if (rx >= fbW || ry >= fbH || rw <= 0 || rh <= 0)
        {
            return;
        }

        var copyW = Math.Min(rw, fbW - rx);
        var copyH = Math.Min(rh, fbH - ry);
        var srcStride = rw * 4;
        var dstStride = fbW * 4;
        var rowBytes = copyW * 4;

        for (var row = 0; row < copyH; row++)
        {
            var srcOffset = row * srcStride;
            var dstOffset = (ry + row) * dstStride + (rx * 4);
            Buffer.BlockCopy(rectBytes, srcOffset, _framebuffer, dstOffset, rowBytes);
        }
    }

    private void CopyRectangleLocked(int srcX, int srcY, int dstX, int dstY, int rw, int rh)
    {
        var fbW = FramebufferWidth;
        var fbH = FramebufferHeight;
        if (srcX >= fbW || srcY >= fbH || dstX >= fbW || dstY >= fbH || rw <= 0 || rh <= 0)
        {
            return;
        }

        var copyW = Math.Min(rw, Math.Min(fbW - srcX, fbW - dstX));
        var copyH = Math.Min(rh, Math.Min(fbH - srcY, fbH - dstY));
        var stride = fbW * 4;
        var rowBytes = copyW * 4;
        var temp = new byte[rowBytes * copyH];

        for (var row = 0; row < copyH; row++)
        {
            Buffer.BlockCopy(_framebuffer, (srcY + row) * stride + srcX * 4, temp, row * rowBytes, rowBytes);
        }
        for (var row = 0; row < copyH; row++)
        {
            Buffer.BlockCopy(temp, row * rowBytes, _framebuffer, (dstY + row) * stride + dstX * 4, rowBytes);
        }
    }

    private async Task SendBytesAsync(byte[] data, CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read <= 0)
            {
                throw new EndOfStreamException("VNC stream closed while reading frame data.");
            }
            offset += read;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _isConnected, 0);
        await _disposeCts.CancelAsync().ConfigureAwait(false);
        try { _tcpClient?.Dispose(); } catch { }
        if (_receiveLoopTask is not null)
        {
            try { await _receiveLoopTask.ConfigureAwait(false); } catch { }
        }
        _writeLock.Dispose();
        _disposeCts.Dispose();
    }
}
