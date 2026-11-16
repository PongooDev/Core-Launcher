using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using Core.Launcher.Hosting.Discord;

namespace Core.Launcher.Tests;

/// <summary>
/// <see cref="DiscordPipe"/> against a pipe server playing Discord, on a name of its own (a Discord running on this PC
/// keeps discord-ipc-0 to -9 to itself).
/// </summary>
public sealed class DiscordPipeTests
{
    private const string Ready = """{"cmd":"DISPATCH","data":{"v":1,"config":{},"user":{"id":"1","username":"tester"}},"evt":"READY","nonce":null}""";

    private readonly string _name = $"core-launcher-test-{Guid.NewGuid():N}";
    private readonly ConcurrentQueue<string> _log = new();

    // With buffers, as a write to an unbuffered pipe waits for the other end to read it.
    private NamedPipeServerStream Server() => new(_name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 64 * 1024, 64 * 1024);

    [Fact]
    public async Task ItHandshakesSendsTheActivityAnswersPingsAndSeesThePipeClose()
    {
        var cancellation = TestContext.Current.CancellationToken;
        await using var discord = Server();
        var accepted = discord.WaitForConnectionAsync(cancellation);
        var opening = DiscordPipe.OpenAsync("1234567890", _log.Enqueue, [_name], cancellation);
        await accepted;

        Assert.Equal((DiscordOpcode.Handshake, """{"v":1,"client_id":"1234567890"}"""), await DiscordIpc.ReadAsync(discord, cancellation));
        await discord.WriteAsync(DiscordIpc.Encode(DiscordOpcode.Frame, Ready), cancellation);
        await using var pipe = await opening;
        Assert.NotNull(pipe);
        Assert.True(pipe.Connected);

        var activity = PresenceText.Activity("MrPodge6", null, null, null, Hosting.Backend.PlayerActivity.Unknown);
        var sent = DiscordIpc.ReadAsync(discord, cancellation);
        await pipe.SendAsync(DiscordOpcode.Frame, DiscordIpc.SetActivity(4242, activity, "nonce-1"), cancellation);
        Assert.Equal((DiscordOpcode.Frame, DiscordIpc.SetActivity(4242, activity, "nonce-1")), await sent);

        await discord.WriteAsync(DiscordIpc.Encode(DiscordOpcode.Ping, """{"n":7}"""), cancellation);
        Assert.Equal((DiscordOpcode.Pong, """{"n":7}"""), await DiscordIpc.ReadAsync(discord, cancellation));

        await discord.WriteAsync(DiscordIpc.Encode(DiscordOpcode.Frame, """{"cmd":"SET_ACTIVITY","data":{"code":4000,"message":"child \"state\" fails"},"evt":"ERROR","nonce":"nonce-1"}"""), cancellation);
        await WaitUntilAsync(() => _log.Any(line => line.Contains("refused the activity", StringComparison.Ordinal)));
        Assert.True(pipe.Connected, "a refused command leaves the connection up");

        discord.Disconnect();
        await WaitUntilAsync(() => !pipe.Connected);
        await Assert.ThrowsAsync<IOException>(() => pipe.SendAsync(DiscordOpcode.Frame, "{}", cancellation));
    }

    [Fact]
    public async Task ARefusedApplicationIdOpensNothingAndSaysWhy()
    {
        var cancellation = TestContext.Current.CancellationToken;
        await using var discord = Server();
        var accepted = discord.WaitForConnectionAsync(cancellation);
        var opening = DiscordPipe.OpenAsync("0", _log.Enqueue, [_name], cancellation);
        await accepted;

        Assert.Equal(DiscordOpcode.Handshake, (await DiscordIpc.ReadAsync(discord, cancellation))?.Opcode);
        await discord.WriteAsync(DiscordIpc.Encode(DiscordOpcode.Close, """{"code":4000,"message":"Invalid Client ID"}"""), cancellation);

        Assert.Null(await opening);
        Assert.Contains(_log, line => line.Contains("Invalid Client ID (4000)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithNoDiscordRunningItOpensNothingAtOnceAndQuietly()
    {
        var watch = Stopwatch.StartNew();
        Assert.Null(await DiscordPipe.OpenAsync("1234567890", _log.Enqueue, [.. Enumerable.Range(0, 10).Select(index => $"{_name}-{index}")], TestContext.Current.CancellationToken));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
        Assert.Empty(_log);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
