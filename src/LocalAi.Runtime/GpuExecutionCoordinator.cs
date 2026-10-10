using System.Text;

namespace LocalAi.Runtime;

/// <summary>Serializes local AI work within and across applications using this runtime.</summary>
public sealed class GpuExecutionCoordinator
{
    private static readonly SemaphoreSlim ProcessGate = new(1, 1);
    private readonly string _lockPath;

    public GpuExecutionCoordinator(string? lockDirectory = null)
    {
        lockDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalAiRuntime");
        Directory.CreateDirectory(lockDirectory);
        _lockPath = Path.Combine(lockDirectory, "ollama-gpu.lock");
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await ProcessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var stream = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                        FileShare.None, 128, FileOptions.Asynchronous);
                    var owner = Encoding.UTF8.GetBytes($"pid={Environment.ProcessId}; acquired={DateTimeOffset.UtcNow:O}");
                    stream.SetLength(0);
                    await stream.WriteAsync(owner, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    return new Lease(stream);
                }
                catch (IOException)
                {
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            ProcessGate.Release();
            throw;
        }
    }

    private sealed class Lease : IAsyncDisposable
    {
        private FileStream? _stream;
        public Lease(FileStream stream) => _stream = stream;

        public ValueTask DisposeAsync()
        {
            var stream = Interlocked.Exchange(ref _stream, null);
            if (stream is null) return ValueTask.CompletedTask;
            stream.Dispose();
            ProcessGate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
