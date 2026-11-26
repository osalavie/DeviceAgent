using System.Text;
using Microsoft.Extensions.Logging;

namespace Sim.DeviceAgent.App.Logging;

/// <summary>按天写入 &lt;stateDirectory&gt;/logs/agent-yyyyMMdd.log 的轻量文件日志，Release 构建同样生效。</summary>
internal sealed class FileLoggerProvider : ILoggerProvider, IDisposable
{
    private const int RetainDays = 7;

    private readonly string _directory;
    private readonly LogLevel _minLevel;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private DateOnly _writerDate;
    private bool _failed;
    private bool _disposed;

    public FileLoggerProvider(string directory, LogLevel minLevel = LogLevel.Information)
    {
        _directory = directory;
        _minLevel = minLevel;

        try
        {
            Directory.CreateDirectory(directory);
            DeleteExpiredFiles();
        }
        catch
        {
            // 日志目录不可写时静默降级为 Debug 输出，不能影响 Agent 启动。
            _failed = true;
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal bool IsEnabled(LogLevel level)
        => !_disposed && !_failed && level != LogLevel.None && level >= _minLevel;

    internal void Write(LogLevel level, string category, EventId eventId, string message, Exception? exception)
    {
        var now = DateTimeOffset.Now;
        var builder = new StringBuilder(256)
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" [").Append(LevelText(level)).Append("] ")
            .Append(category).Append(": ").Append(message);

        if (eventId.Id != 0)
        {
            builder.Append(" (event ").Append(eventId.Id).Append(')');
        }

        if (exception is not null)
        {
            builder.AppendLine().Append(exception);
        }

        var line = builder.ToString();

        lock (_gate)
        {
            if (_failed || _disposed)
            {
                return;
            }

            try
            {
                var date = DateOnly.FromDateTime(now.LocalDateTime);
                if (_writer is null || _writerDate != date)
                {
                    _writer?.Dispose();
                    _writerDate = date;
                    _writer = new StreamWriter(
                        new FileStream(
                            Path.Combine(_directory, $"agent-{date:yyyyMMdd}.log"),
                            FileMode.Append,
                            FileAccess.Write,
                            FileShare.ReadWrite),
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                    {
                        AutoFlush = true
                    };
                }

                _writer.WriteLine(line);
            }
            catch
            {
                _failed = true;
            }
        }
    }

    private void DeleteExpiredFiles()
    {
        var cutoff = DateTime.Now.Date.AddDays(-RetainDays);
        foreach (var file in Directory.EnumerateFiles(_directory, "agent-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // 清理失败不影响日志写入。
            }
        }
    }

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Trace => "Trace",
        LogLevel.Debug => "Debug",
        LogLevel.Information => "Information",
        LogLevel.Warning => "Warning",
        LogLevel.Error => "Error",
        LogLevel.Critical => "Critical",
        _ => level.ToString()
    };

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => _provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            _provider.Write(logLevel, _category, eventId, formatter(state, exception), exception);
        }
    }
}
