namespace F1Telemetry.App.Infrastructure;

/// <summary>
/// Cross-platform single-instance guard: an exclusively opened lock file (released by the OS if the process dies).
/// A second instance would also fight over the UDP port and the DuckDB file lock.
/// </summary>
public sealed class SingleInstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private SingleInstanceLock(FileStream stream) => _stream = stream;

    public static SingleInstanceLock? TryAcquire(string path)
    {
        try
        {
            return new SingleInstanceLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
