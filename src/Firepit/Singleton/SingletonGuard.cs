using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Firepit.Singleton;

public sealed class SingletonGuard : IDisposable
{
    /// <summary>
    /// The pipe this instance answers to. Per instance rather than constant:
    /// that is what lets a candidate build run beside the installed Firepit
    /// instead of merely focusing it.
    /// </summary>
    public static string PipeName => Firepit.Core.FirepitPaths.SingletonPipeName;

    private NamedPipeServerStream? _server;
    private CancellationTokenSource? _listenerCts;

    public bool TryAcquire()
    {
        try
        {
            _server = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            return true;
        }
        catch (IOException)
        {
            // pipe already exists — another instance is alive
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public Task<bool> TrySendAsync(SingletonCommand command, TimeSpan timeout)
    {
        return Task.Run(async () =>
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                client.Connect((int)timeout.TotalMilliseconds);
                var json = JsonSerializer.Serialize(command);
                var bytes = Encoding.UTF8.GetBytes(json);
                await client.WriteAsync(bytes);
                await client.FlushAsync();
                return true;
            }
            catch
            {
                return false;
            }
        });
    }

    public void StartListening(Func<SingletonCommand, Task> handler)
    {
        if (_server is null)
        {
            throw new InvalidOperationException("TryAcquire must succeed before StartListening.");
        }
        _listenerCts = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoopAsync(_server, handler, _listenerCts.Token));
    }

    private static async Task ListenLoopAsync(
        NamedPipeServerStream server,
        Func<SingletonCommand, Task> handler,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await server.WaitForConnectionAsync(ct);
                using var ms = new MemoryStream();
                var buffer = new byte[1024];
                while (true)
                {
                    var read = await server.ReadAsync(buffer, ct);
                    if (read <= 0) break;
                    ms.Write(buffer, 0, read);
                }

                if (ms.Length > 0)
                {
                    SingletonCommand? cmd = null;
                    try
                    {
                        cmd = JsonSerializer.Deserialize<SingletonCommand>(ms.ToArray());
                    }
                    catch (JsonException) { /* drop */ }

                    if (cmd is not null)
                    {
                        try { await handler(cmd); } catch { /* handler errors don't kill the listener */ }
                    }
                }

                if (server.IsConnected) server.Disconnect();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                // pipe broken — try to recover by disconnecting and looping
                try { if (server.IsConnected) server.Disconnect(); } catch { /* ignored */ }
            }
        }
    }

    public void Dispose()
    {
        try { _listenerCts?.Cancel(); } catch { /* ignored */ }
        try { _server?.Dispose(); } catch { /* ignored */ }
        _listenerCts?.Dispose();
    }
}

public sealed record SingletonCommand(string Command, string? Project = null)
{
    public static SingletonCommand Focus()                        => new("focus");
    public static SingletonCommand Summon(string projectName)     => new("summon", projectName);
}
