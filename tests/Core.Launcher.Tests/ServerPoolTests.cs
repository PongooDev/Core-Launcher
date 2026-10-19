using Core.Launcher.Hosting.Logging;
using Core.Launcher.Hosting.Servers;
using Microsoft.Extensions.Time.Testing;

namespace Core.Launcher.Tests;

public sealed class ServerPoolTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeStarter _starter = new();
    private readonly LogChannel _log = new("Launcher");

    private ServerPool Pool(int instances = 2, int readyTimeoutSeconds = 180)
    {
        var pool = new ServerPool(_starter, _log, _time);
        pool.Configure(instances, TimeSpan.FromSeconds(readyTimeoutSeconds));
        pool.SetEnabled(true);
        return pool;
    }

    private void Advance(ServerPool pool, int seconds)
    {
        for (var second = 0; second < seconds; ++second)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            pool.Tick();
        }
    }

    [Fact]
    public void BootsOneServerAtATime()
    {
        using var pool = Pool();

        Assert.Single(_starter.Started);
        Assert.Equal([ServerState.Starting, ServerState.Waiting], pool.Slots.Select(slot => slot.State));

        Advance(pool, 30);
        Assert.Single(_starter.Started);

        _starter.Started[0].SetReady();
        pool.Tick();
        Assert.Equal(2, _starter.Started.Count);
        Assert.Equal([ServerState.Ready, ServerState.Starting], pool.Slots.Select(slot => slot.State));
        Assert.Equal([1, 2], _starter.Started.Select(server => server.Instance));

        _starter.Started[1].SetReady();
        pool.Tick();
        Assert.All(pool.Slots, slot => Assert.Equal(ServerState.Ready, slot.State));
        Assert.Equal("UDP 7777", pool.Slots[0].Note);
    }

    [Fact]
    public void RestartsAServerThatNeverGetsReady()
    {
        using var pool = Pool(instances: 1, readyTimeoutSeconds: 180);
        var first = _starter.Started[0];

        Advance(pool, 179);
        Assert.False(first.Terminated);
        Advance(pool, 1);
        Assert.True(first.Terminated);
        Assert.Equal(ServerState.Stopping, pool.Slots[0].State);

        pool.Tick();
        Assert.Equal(ServerState.Waiting, pool.Slots[0].State);
        Assert.Equal(1, pool.Slots[0].Restarts);

        Advance(pool, 4);
        Assert.Single(_starter.Started);
        Advance(pool, 1);
        Assert.Equal(2, _starter.Started.Count);
    }

    [Fact]
    public void ZeroReadyTimeoutWaitsForever()
    {
        using var pool = Pool(instances: 1, readyTimeoutSeconds: 0);
        Advance(pool, 3600);
        Assert.False(_starter.Started[0].Terminated);
    }

    [Fact]
    public void CrashesBackOffAndReadyResetsTheBackoff()
    {
        using var pool = Pool(instances: 1);

        _starter.Started[0].Exit(0xC0000005);
        pool.Tick();
        Advance(pool, 5);
        Assert.Equal(2, _starter.Started.Count);

        _starter.Started[1].Exit(1);
        pool.Tick();
        Advance(pool, 9);
        Assert.Equal(2, _starter.Started.Count);
        Advance(pool, 1);
        Assert.Equal(3, _starter.Started.Count);

        _starter.Started[2].SetReady();
        pool.Tick();
        _starter.Started[2].Exit(1);
        pool.Tick();
        Advance(pool, 5);
        Assert.Equal(4, _starter.Started.Count);
        Assert.Equal(3, pool.Slots[0].Restarts);
    }

    [Fact]
    public void WaitsWhileTheStarterIsBlocked()
    {
        _starter.Blocked = "waiting for the backend";
        using var pool = Pool();
        Advance(pool, 10);

        Assert.Empty(_starter.Started);
        Assert.Equal("waiting for the backend", pool.Slots[0].Note);

        _starter.Blocked = null;
        pool.Tick();
        Assert.Single(_starter.Started);
    }

    [Fact]
    public void AFailedStartIsRetriedWithBackoff()
    {
        _starter.Failure = "Release v1 has no Core.dll";
        using var pool = Pool(instances: 1);

        Assert.Equal("Release v1 has no Core.dll", pool.Slots[0].Note);
        _starter.Failure = null;
        Advance(pool, 4);
        Assert.Empty(_starter.Started);
        Advance(pool, 1);
        Assert.Single(_starter.Started);
    }

    [Fact]
    public void RestartStartsAgainRightAwayWithoutBackoff()
    {
        using var pool = Pool(instances: 1);
        _starter.Started[0].SetReady();
        pool.Tick();

        pool.Restart(1);
        Assert.True(_starter.Started[0].Terminated);
        pool.Tick();
        Assert.Equal(2, _starter.Started.Count);
    }

    [Fact]
    public void HostingOffStopsAndRemovesEveryServer()
    {
        using var pool = Pool();
        _starter.Started[0].SetReady();
        pool.Tick();
        Assert.Equal(2, _starter.Started.Count);

        pool.SetEnabled(false);
        Assert.All(_starter.Started, server => Assert.True(server.Terminated));
        pool.Tick();
        Assert.Empty(pool.Slots);
        Assert.All(_starter.Started, server => Assert.True(server.Disposed));

        Advance(pool, 120);
        Assert.Equal(2, _starter.Started.Count);
    }

    [Fact]
    public void FewerInstancesStopsTheHighest()
    {
        using var pool = Pool(instances: 3);
        foreach (var _ in Enumerable.Range(0, 3))
        {
            _starter.Started[^1].SetReady();
            pool.Tick();
        }

        Assert.Equal(3, _starter.Started.Count);
        pool.Configure(2, TimeSpan.FromSeconds(180));
        pool.Tick();
        Assert.Equal([false, false, true], _starter.Started.Select(server => server.Terminated));
        pool.Tick();
        Assert.Equal([1, 2], pool.Slots.Select(slot => slot.Instance));
    }

    private sealed class FakeStarter : IServerStarter
    {
        public List<FakeServer> Started { get; } = [];

        public string? Blocked { get; set; }

        public string? Failure { get; set; }

        public string? Blocker() => Blocked;

        public IServerInstance Start(int instance)
        {
            if (Failure is not null)
            {
                throw new InvalidOperationException(Failure);
            }

            var server = new FakeServer(instance, 1000 + Started.Count);
            Started.Add(server);
            return server;
        }
    }

    private sealed class FakeServer(int instance, int id) : IServerInstance
    {
        private readonly TaskCompletionSource<uint> _exited = new();
        private readonly TaskCompletionSource _ready = new();

        public int Instance { get; } = instance;

        public int Id { get; } = id;

        public bool Terminated { get; private set; }

        public bool Disposed { get; private set; }

        public Task<uint> Exited => _exited.Task;

        public Task Ready => _ready.Task;

        public Task Finished => _exited.Task;

        public void SetReady() => _ready.TrySetResult();

        public void Exit(uint code) => _exited.TrySetResult(code);

        public void Terminate()
        {
            Terminated = true;
            Exit(1);
        }

        public IReadOnlyList<int> UdpPorts() => Ready.IsCompleted ? [7776 + Instance] : [];

        public void Dispose() => Disposed = true;
    }
}
