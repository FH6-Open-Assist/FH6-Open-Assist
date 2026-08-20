using System.Text;

namespace FH6OpenAssist.Core;

public sealed class AutomationLogger : IDisposable
{
    private readonly object _sync = new();
    private readonly StreamWriter _writer;

    public event Action<string>? LineWritten;

    public AutomationLogger(string baseDirectory)
    {
        var logDirectory = Path.Combine(baseDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        var path = Path.Combine(logDirectory, $"forza-farm-{DateTime.Now:yyyyMMdd}.log");
        _writer = new StreamWriter(path, append: true, new UTF8Encoding(false))
        {
            AutoFlush = true
        };
    }

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("AVISO", message);

    public void Error(string message) => Write("ERRO", message);

    public void State(string workflow, string state, string message) =>
        Write("ESTADO", $"[{workflow}/{state}] {message}");

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        lock (_sync)
        {
            _writer.WriteLine(line);
        }

        LineWritten?.Invoke(line);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _writer.Dispose();
        }
    }
}
