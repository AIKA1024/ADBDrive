using System.Text;

namespace ADBDrive.Core.Diagnostics;

/// <summary>日志级别。</summary>
public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
}

/// <summary>一条日志记录。</summary>
public readonly record struct LogEntry(DateTime Timestamp, LogLevel Level, string Category, string Message)
{
    public string Format() => $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level,-5}] {Category}: {Message}";
}

/// <summary>
/// 应用日志：内存环形缓冲（给 UI 用）+ 按天滚动的文本文件（给排查用）。
/// 线程安全，可在任意线程调用。
/// </summary>
public sealed class AppLog : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<LogEntry> _ring;
    private readonly int _capacity;
    private readonly string? _logDirectory;
    private StreamWriter? _writer;
    private DateTime _writerDate;
    private bool _disposed;

    public AppLog(string? logDirectory, int capacity = 4000)
    {
        _capacity = capacity;
        _ring = new Queue<LogEntry>(capacity);
        _logDirectory = logDirectory;
        if (!string.IsNullOrEmpty(logDirectory))
        {
            try
            {
                Directory.CreateDirectory(logDirectory);
            }
            catch
            {
                _logDirectory = null;
            }
        }
    }

    /// <summary>日志产生时触发（可能在任意线程，UI 需要自行切线程）。</summary>
    public event Action<LogEntry>? EntryWritten;

    public string? LogDirectory => _logDirectory;

    public void Trace(string category, string message) => Write(LogLevel.Trace, category, message);

    public void Debug(string category, string message) => Write(LogLevel.Debug, category, message);

    public void Info(string category, string message) => Write(LogLevel.Info, category, message);

    public void Warn(string category, string message) => Write(LogLevel.Warn, category, message);

    public void Error(string category, string message) => Write(LogLevel.Error, category, message);

    public void Error(string category, string message, Exception ex) =>
        Write(LogLevel.Error, category, $"{message} :: {ex.GetType().Name}: {ex.Message}");

    public void Write(LogLevel level, string category, string message)
    {
        if (_disposed)
        {
            return;
        }

        var entry = new LogEntry(DateTime.Now, level, category, message);
        Action<LogEntry>? handler;
        lock (_gate)
        {
            _ring.Enqueue(entry);
            while (_ring.Count > _capacity)
            {
                _ring.Dequeue();
            }

            AppendToFile(entry);
            handler = EntryWritten;
        }

        handler?.Invoke(entry);
    }

    public LogEntry[] Snapshot()
    {
        lock (_gate)
        {
            return _ring.ToArray();
        }
    }

    private void AppendToFile(LogEntry entry)
    {
        if (_logDirectory is null)
        {
            return;
        }

        try
        {
            var today = entry.Timestamp.Date;
            if (_writer is null || _writerDate != today)
            {
                _writer?.Dispose();
                var path = Path.Combine(_logDirectory, $"adbdrive-{today:yyyyMMdd}.log");
                _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
                {
                    AutoFlush = true,
                };
                _writerDate = today;
            }

            _writer.WriteLine(entry.Format());
        }
        catch
        {
            // 日志落盘失败不能影响主流程。
            _writer = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }
}
