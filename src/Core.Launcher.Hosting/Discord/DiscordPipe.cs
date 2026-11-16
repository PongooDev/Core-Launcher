using System.IO.Pipes;

namespace Core.Launcher.Hosting.Discord;

/// <summary>A connection to Discord that <see cref="DiscordPresence"/> sends its commands on.</summary>
public interface IDiscordConnection : IAsyncDisposable
{
    /// <summary>False once Discord closed it or a write failed; the presence then opens a new one when it next has something to show.</summary>
    bool Connected { get; }

    /// <summary>Throws <see cref="IOException"/> (or <see cref="ObjectDisposedException"/>) when the pipe is gone.</summary>
    Task SendAsync(DiscordOpcode opcode, string json, CancellationToken cancellation);
}

/// <summary>
/// One connection to the local Discord client over its IPC pipe (<see cref="DiscordIpc"/>). Its reader awaits Discord's
/// frames: it answers pings, logs a command Discord refused, and marks the connection closed when the pipe ends.
/// </summary>
public sealed class DiscordPipe : IDiscordConnection
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    private readonly NamedPipeClientStream _pipe;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reader;
    private volatile bool _connected = true;

    private DiscordPipe(NamedPipeClientStream pipe, Action<string> log)
    {
        _pipe = pipe;
        _log = log;
        _reader = Task.Run(ReadAsync);
    }

    public bool Connected => _connected;

    /// <summary>
    /// Connects to the first Discord pipe that answers the handshake for <paramref name="applicationId"/>. Null when Discord
    /// isn't running (no pipe exists, which takes no wait) or refused it; why is logged only for a refusal.
    /// </summary>
    public static Task<DiscordPipe?> OpenAsync(string applicationId, Action<string> log, CancellationToken cancellation) =>
        OpenAsync(applicationId, log, Enumerable.Range(0, DiscordIpc.PipeCount).Select(index => DiscordIpc.PipePrefix + index), cancellation);

    /// <summary><see cref="OpenAsync(string, Action{string}, CancellationToken)"/> over the pipes named (tests use a pipe of their own).</summary>
    internal static async Task<DiscordPipe?> OpenAsync(string applicationId, Action<string> log, IEnumerable<string> pipeNames, CancellationToken cancellation)
    {
        foreach (var name in pipeNames)
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                // 0: one try. A pipe that doesn't exist fails at once (CreateFile's ERROR_FILE_NOT_FOUND), so this costs
                // nothing while Discord isn't running.
                pipe.Connect(0);
            }
            catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
            {
                await pipe.DisposeAsync();
                continue;
            }

            try
            {
                await pipe.WriteAsync(DiscordIpc.Encode(DiscordOpcode.Handshake, DiscordIpc.Handshake(applicationId)), cancellation);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(HandshakeTimeout);
                var answer = await DiscordIpc.ReadAsync(pipe, timeout.Token);
                if (answer is (DiscordOpcode.Frame, var json) && DiscordIpc.IsReady(json))
                {
                    return new DiscordPipe(pipe, log);
                }

                // The same application id is refused by every Discord on the PC, so the others aren't tried.
                log(answer is (DiscordOpcode.Close, var reason)
                    ? $"Discord Rich Presence: Discord refused the launcher's application id ({DiscordIpc.CloseReason(reason)})"
                    : "Discord Rich Presence: Discord didn't answer the handshake");
                await pipe.DisposeAsync();
                return null;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
            {
                await pipe.DisposeAsync();
                if (cancellation.IsCancellationRequested)
                {
                    throw;
                }
            }
        }

        return null;
    }

    public async Task SendAsync(DiscordOpcode opcode, string json, CancellationToken cancellation)
    {
        var frame = DiscordIpc.Encode(opcode, json);
        await _writing.WaitAsync(cancellation);
        try
        {
            await _pipe.WriteAsync(frame, cancellation);
            await _pipe.FlushAsync(cancellation);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            _connected = false;
            throw new IOException("The Discord pipe closed", exception);
        }
        finally
        {
            _writing.Release();
        }
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await DiscordIpc.ReadAsync(_pipe, _stop.Token) is (var opcode, var json))
            {
                switch (opcode)
                {
                    case DiscordOpcode.Ping:
                        await SendAsync(DiscordOpcode.Pong, json, _stop.Token);
                        break;
                    case DiscordOpcode.Close:
                        _log($"Discord Rich Presence: Discord closed the connection ({DiscordIpc.CloseReason(json)})");
                        return;
                    case DiscordOpcode.Frame when DiscordIpc.Error(json) is { } error:
                        _log($"Discord Rich Presence: Discord refused the activity ({error})");
                        break;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            _connected = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _connected = false;
        await _stop.CancelAsync();
        await _pipe.DisposeAsync();
        try
        {
            await _reader;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }

        _stop.Dispose();
        _writing.Dispose();
    }
}
