using System.Text;
using System.Text.RegularExpressions;

namespace WinQemu.Core.Lifecycle;

/// <summary>
/// Bounded per-VM log writer with automatic file rotation and sensitive data redaction (§50).
/// </summary>
public sealed partial class LogRotator : IDisposable
{
    private readonly string _logFilePath;
    private readonly long _maxBytesPerFile;
    private readonly int _maxRotatedFiles;
    private readonly Lock _sync = new();
    private StreamWriter? _writer;
    private long _currentLength;

    public LogRotator(string logFilePath, int maxFileSizeMiB = 10, int maxRotatedFiles = 5)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logFilePath);
        _logFilePath = Path.GetFullPath(logFilePath);
        _maxBytesPerFile = Math.Max(64 * 1024L, maxFileSizeMiB * 1024L * 1024L);
        _maxRotatedFiles = Math.Max(1, maxRotatedFiles);

        Directory.CreateDirectory(Path.GetDirectoryName(_logFilePath)!);
        OpenWriter();
    }

    public string LogFilePath => _logFilePath;

    public void AppendLine(string category, string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        var sanitized = RedactSecrets(message);
        var line = $"[{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC] [{category}] {sanitized}";

        lock (_sync)
        {
            if (_writer is null)
            {
                return;
            }

            var byteCount = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            if (_currentLength + byteCount > _maxBytesPerFile)
            {
                RotateLocked();
            }

            _writer.WriteLine(line);
            _writer.Flush();
            _currentLength += byteCount;
        }
    }

    public string ReadRecentTail(int maxLines = 300)
    {
        lock (_sync)
        {
            _writer?.Flush();
            if (!File.Exists(_logFilePath))
            {
                return string.Empty;
            }

            using var fs = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var lines = new Queue<string>(maxLines);
            while (reader.ReadLine() is { } line)
            {
                if (lines.Count >= maxLines)
                {
                    lines.Dequeue();
                }
                lines.Enqueue(line);
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    public static string RedactSecrets(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = PasswordRegex().Replace(text, "$1=[REDACTED]");
        result = TokenRegex().Replace(result, "$1=[REDACTED]");
        return result;
    }

    private void OpenWriter()
    {
        var fs = new FileStream(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _currentLength = fs.Length;
        _writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void RotateLocked()
    {
        _writer?.Dispose();
        _writer = null;

        try
        {
            var oldest = $"{_logFilePath}.{_maxRotatedFiles}";
            if (File.Exists(oldest))
            {
                File.Delete(oldest);
            }

            for (var i = _maxRotatedFiles - 1; i >= 1; i--)
            {
                var src = $"{_logFilePath}.{i}";
                var dst = $"{_logFilePath}.{i + 1}";
                if (File.Exists(src))
                {
                    File.Move(src, dst, overwrite: true);
                }
            }

            if (File.Exists(_logFilePath))
            {
                File.Move(_logFilePath, $"{_logFilePath}.1", overwrite: true);
            }
        }
        catch
        {
        }

        OpenWriter();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    [GeneratedRegex(@"(?i)(password|passwd|secret|passphrase|credential)\s*[:=]\s*\S+")]
    private static partial Regex PasswordRegex();

    [GeneratedRegex(@"(?i)(token|apikey|api_key|authorization|bearer)\s*[:=]\s*\S+")]
    private static partial Regex TokenRegex();
}
