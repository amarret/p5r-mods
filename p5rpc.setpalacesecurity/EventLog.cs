namespace p5rpc.setpalacesecurity;

/// <summary>A small per-session text log (overwritten at start-up, capped so a long session cannot fill the disk).</summary>
public sealed class EventLog
{
    private const int MaxLines = 2000;

    private readonly object _lock = new();
    private readonly StreamWriter _writer;
    private int _lines;

    public EventLog(string path)
    {
        _writer = new StreamWriter(path, append: false) { AutoFlush = true };
        _writer.WriteLine($"Set Palace Security Level events, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    }

    public void Write(string line)
    {
        lock (_lock)
        {
            if (_lines > MaxLines) return;
            try
            {
                _writer.WriteLine(++_lines > MaxLines ? "(log full)" : $"{DateTime.Now:HH:mm:ss.fff} {line}");
            }
            catch (IOException)
            {
                // A full disk or a locked file must not stop the game.
            }
        }
    }
}
