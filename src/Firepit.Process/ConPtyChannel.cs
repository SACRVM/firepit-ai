using System.Threading.Channels;
using Firepit.Core.Process;
using Porta.Pty;

namespace Firepit.Process;

public sealed class ConPtyChannel : IPtyChannel
{
    private readonly IPtyConnection _connection;
    private readonly Channel<ReadOnlyMemory<byte>> _outputChannel;
    private readonly CancellationTokenSource _readLoopCts;
    private readonly Task _readLoopTask;
    private readonly TaskCompletionSource<int> _exitTcs;
    private int _disposed;

    internal ConPtyChannel(IPtyConnection connection)
    {
        _connection = connection;
        Pid = connection.Pid;

        _outputChannel = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true,
        });
        _readLoopCts = new CancellationTokenSource();
        _exitTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        _connection.ProcessExited += OnProcessExited;
        _readLoopTask = Task.Run(() => ReadLoopAsync(_readLoopCts.Token));
    }

    public int Pid { get; }

    public Task<int> WaitForExitAsync(CancellationToken ct)
    {
        return ct.CanBeCanceled
            ? _exitTcs.Task.WaitAsync(ct)
            : _exitTcs.Task;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _connection.WriterStream.WriteAsync(data, ct).ConfigureAwait(false);
        await _connection.WriterStream.FlushAsync(ct).ConfigureAwait(false);
    }

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync(CancellationToken ct)
    {
        return _outputChannel.Reader.ReadAllAsync(ct);
    }

    public void Resize(int cols, int rows)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _connection.Resize(cols, rows);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try { _readLoopCts.Cancel(); } catch { /* ignored */ }

        try
        {
            if (!_exitTcs.Task.IsCompleted)
            {
                _connection.Kill();
            }
        }
        catch { /* ignored */ }

        try
        {
            await _readLoopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected on cancel
        }

        _connection.ProcessExited -= OnProcessExited;
        _connection.Dispose();
        _readLoopCts.Dispose();
    }

    private void OnProcessExited(object? sender, PtyExitedEventArgs e)
    {
        _exitTcs.TrySetResult(e.ExitCode);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await _connection.ReaderStream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (IOException) when (!OperatingSystem.IsWindows())
                {
                    // On Linux, reading the master side of a pseudo-terminal
                    // whose child has exited fails with EIO instead of
                    // returning 0. That is the end of the stream, not an error.
                    break;
                }

                if (read <= 0)
                {
                    break;
                }

                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                await _outputChannel.Writer.WriteAsync(chunk, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        finally
        {
            _outputChannel.Writer.TryComplete();
        }
    }
}
