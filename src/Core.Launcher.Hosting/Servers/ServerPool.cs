using Core.Launcher.Hosting.Logging;

namespace Core.Launcher.Hosting.Servers;

public interface IServerInstance : IDisposable
{
    int Id { get; }

    Task<uint> Exited { get; }

    Task Ready { get; }

    /// <summary>Completes after exit, once the instance's logs are read to the end.</summary>
    Task Finished { get; }

    void Terminate();

    IReadOnlyList<int> UdpPorts();
}

public interface IServerStarter
{
    /// <summary>Null when a server can start now, otherwise why not (shown on waiting slots).</summary>
    string? Blocker();

    IServerInstance Start(int instance);

    /// <summary>
    /// Whether the backend lists the matchmaking session of the server started at <paramref name="startedAt"/> in this
    /// slot (it may use the UDP <paramref name="ports"/> it bound). Must not block: the pool asks every tick.
    /// </summary>
    SessionCheck Session(int instance, DateTimeOffset startedAt, IReadOnlyList<int> ports);
}

public enum ServerState
{
    Waiting,
    Starting,

    /// <summary>Logged "Server ready" but its matchmaking session isn't listed yet, so clients can't find it.</summary>
    Registering,
    Ready,
    Stopping,
}

public sealed record ServerSlotView(int Instance, ServerState State, int? ProcessId, DateTimeOffset? Since, int Restarts, IReadOnlyList<int> Ports, string Note);

/// <summary>
/// Keeps <see cref="Instances"/> servers running while hosting is on: one boots at a time, a server that hasn't logged
/// "Server ready" within the ready timeout is restarted, and every exit is restarted after a backoff. After "Server ready"
/// (it has bound its UDP port) a server still has to log in and register its matchmaking session before clients can
/// find it, so it only counts as ready, and the next one only boots, once the backend lists that session; a backend
/// without a session listing, or one that doesn't list it within <see cref="SessionTimeout"/>, counts it ready anyway.
/// </summary>
public sealed class ServerPool : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PortRefresh = TimeSpan.FromSeconds(5);

    /// <summary>How long after "Server ready" to wait for the server's matchmaking session (it usually takes 35–75 s).</summary>
    public static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(180);

    private readonly IServerStarter _starter;
    private readonly LogChannel _log;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly List<Slot> _slots = [];
    private ITimer? _timer;

    public ServerPool(IServerStarter starter, LogChannel log, TimeProvider time)
    {
        _starter = starter;
        _log = log;
        _time = time;
    }

    public bool Enabled { get; private set; }

    public int Instances { get; private set; }

    public TimeSpan ReadyTimeout { get; private set; }

    public event Action? Changed;

    public void Configure(int instances, TimeSpan readyTimeout)
    {
        lock (_gate)
        {
            Instances = instances;
            ReadyTimeout = readyTimeout;
        }
    }

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (Enabled == enabled)
            {
                return;
            }

            Enabled = enabled;
            _log.Write(enabled ? $"Hosting on: keeping {Instances} server(s) running" : "Hosting off: stopping the servers");
        }

        Tick();
    }

    public void StartTimer() => _timer ??= _time.CreateTimer(_ => Tick(), null, TimeSpan.Zero, TickInterval);

    public IReadOnlyList<ServerSlotView> Slots
    {
        get
        {
            lock (_gate)
            {
                return [.. _slots.Select(slot => slot.View())];
            }
        }
    }

    /// <summary>Stops a server on purpose; it starts again right away, without counting as a failure.</summary>
    public void Restart(int instance)
    {
        lock (_gate)
        {
            if (_slots.FirstOrDefault(slot => slot.Instance == instance) is { Server: not null } slot)
            {
                _log.Write($"Server {instance}: restart requested");
                slot.Stop(StopReason.Restart);
            }
        }

        Tick();
    }

    public void Tick()
    {
        bool changed;
        lock (_gate)
        {
            try
            {
                changed = Update(_time.GetUtcNow());
            }
            catch (Exception exception)
            {
                _log.Write($"Server pool: {exception}");
                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private bool Update(DateTimeOffset now)
    {
        var changed = Reconcile(now);
        foreach (var slot in _slots.ToList())
        {
            changed |= Advance(slot, now);
        }

        _slots.RemoveAll(slot => slot.Removed);

        if (_slots.Any(slot => slot.State is ServerState.Starting or ServerState.Registering))
        {
            foreach (var slot in _slots.Where(slot => slot.Server is null))
            {
                changed |= slot.SetNote("waiting for the booting server");
            }

            return changed;
        }

        var next = _slots.FirstOrDefault(slot => slot.Server is null && !slot.Removing && slot.NextStart <= now);
        foreach (var slot in _slots.Where(slot => slot.Server is null && slot != next))
        {
            changed |= slot.SetNote(slot.NextStart > now ? $"next start in {(slot.NextStart - now).TotalSeconds:0} s" : "waiting for its turn");
        }

        if (next is null)
        {
            return changed;
        }

        if (_starter.Blocker() is { } blocker)
        {
            return next.SetNote(blocker) | changed;
        }

        try
        {
            next.Begin(_starter.Start(next.Instance), now);
            _log.Write($"Server {next.Instance}: started (pid {next.Server!.Id})");
        }
        catch (Exception exception)
        {
            next.Failures++;
            next.NextStart = now + Backoff.Delay(next.Failures);
            next.SetNote(exception.Message);
            _log.Write($"Server {next.Instance}: couldn't start: {exception.Message}");
        }

        return true;
    }

    private bool Reconcile(DateTimeOffset now)
    {
        var target = Enabled ? Instances : 0;
        var changed = false;
        for (var instance = 1; instance <= target; ++instance)
        {
            if (_slots.All(slot => slot.Instance != instance))
            {
                _slots.Add(new Slot(instance) { NextStart = now });
                changed = true;
            }
        }

        _slots.Sort((left, right) => left.Instance.CompareTo(right.Instance));
        foreach (var slot in _slots.Where(slot => slot.Instance > target && !slot.Removing))
        {
            slot.Removing = true;
            slot.Stop(StopReason.Removed);
            changed = true;
        }

        return changed;
    }

    private bool Advance(Slot slot, DateTimeOffset now)
    {
        if (slot.Server is not { } server)
        {
            slot.Removed = slot.Removing;
            return slot.Removed;
        }

        if (server.Finished.IsCompleted)
        {
            Finish(slot, server, now);
            return true;
        }

        if (server.Exited.IsCompleted || slot.State == ServerState.Stopping)
        {
            return slot.SetState(ServerState.Stopping);
        }

        if (slot.State == ServerState.Starting)
        {
            if (server.Ready.IsCompleted)
            {
                slot.State = ServerState.Registering;
                slot.ServerReadyAt = now;
                slot.RefreshPorts(now);
                _log.Write($"Server {slot.Instance}: \"Server ready\" after {(now - slot.StartedAt).TotalSeconds:0} s{OnPorts(slot)}; waiting for its matchmaking session");
                Register(slot, now);
                return true;
            }

            if (ReadyTimeout > TimeSpan.Zero && now - slot.StartedAt >= ReadyTimeout)
            {
                _log.Write($"Server {slot.Instance}: no \"Server ready\" after {ReadyTimeout.TotalSeconds:0} s, restarting it");
                slot.Stop(StopReason.TimedOut);
                return true;
            }

            return slot.SetNote($"booting, {(now - slot.StartedAt).TotalSeconds:0} s");
        }

        if (slot.State == ServerState.Registering)
        {
            if (now - slot.PortsCheckedAt >= PortRefresh)
            {
                slot.RefreshPorts(now);
            }

            return Register(slot, now);
        }

        if (slot.State == ServerState.Ready && now - slot.PortsCheckedAt >= PortRefresh)
        {
            var before = string.Join(",", slot.Ports);
            slot.RefreshPorts(now);
            return slot.SetNote(slot.Ports.Count > 0 ? $"UDP {string.Join(", ", slot.Ports)}" : "") || before != string.Join(",", slot.Ports);
        }

        return false;
    }

    /// <summary>Makes a registering server ready once its session is listed, or when there is no point waiting any longer.</summary>
    private bool Register(Slot slot, DateTimeOffset now)
    {
        var waited = now - slot.ServerReadyAt;
        switch (_starter.Session(slot.Instance, slot.StartedAt, slot.Ports))
        {
            case SessionCheck.Listed:
                MakeReady(slot, now, $"matchmaking session registered {waited.TotalSeconds:0} s after \"Server ready\"");
                return true;
            case SessionCheck.Unsupported:
                MakeReady(slot, now, "the backend doesn't list matchmaking sessions, so it counts as ready at \"Server ready\"");
                return true;
        }

        if (waited >= SessionTimeout)
        {
            MakeReady(slot, now, $"no matchmaking session listed {SessionTimeout.TotalSeconds:0} s after \"Server ready\", counting it as ready anyway");
            return true;
        }

        return slot.SetNote($"registering its matchmaking session, {waited.TotalSeconds:0} s");
    }

    private void MakeReady(Slot slot, DateTimeOffset now, string how)
    {
        slot.State = ServerState.Ready;
        slot.Since = now;
        slot.Failures = 0;
        slot.SetNote(slot.Ports.Count > 0 ? $"UDP {string.Join(", ", slot.Ports)}" : "");
        _log.Write($"Server {slot.Instance}: ready after {(now - slot.StartedAt).TotalSeconds:0} s{OnPorts(slot)} ({how})");
    }

    private static string OnPorts(Slot slot) => slot.Ports.Count > 0 ? $" on UDP {string.Join(", ", slot.Ports)}" : "";

    private void Finish(Slot slot, IServerInstance server, DateTimeOffset now)
    {
        var code = server.Exited.Result;
        var reason = slot.StopReason;
        server.Dispose();
        slot.Server = null;
        slot.State = ServerState.Waiting;
        slot.Since = null;
        slot.Ports = [];
        slot.StopReason = StopReason.None;

        if (slot.Removing)
        {
            slot.Removed = true;
            _log.Write($"Server {slot.Instance}: stopped");
            return;
        }

        slot.Restarts++;
        if (reason == StopReason.Restart)
        {
            slot.NextStart = now;
            return;
        }

        slot.Failures++;
        var delay = Backoff.Delay(slot.Failures);
        slot.NextStart = now + delay;
        _log.Write(reason == StopReason.TimedOut
            ? $"Server {slot.Instance}: stopped (ready timeout); next start in {delay.TotalSeconds:0} s"
            : $"Server {slot.Instance}: exited with 0x{code:X8}; next start in {delay.TotalSeconds:0} s");
    }

    /// <summary>Turns hosting off and waits for every server to exit and its logs to be read.</summary>
    public async Task StopAllAsync(TimeSpan timeout)
    {
        SetEnabled(false);
        List<Task> finishing;
        lock (_gate)
        {
            finishing = [.. _slots.Where(slot => slot.Server is not null).Select(slot => slot.Server!.Finished)];
        }

        await Task.WhenAny(Task.WhenAll(finishing), Task.Delay(timeout));
        Tick();
    }

    public void Dispose()
    {
        _timer?.Dispose();
        lock (_gate)
        {
            foreach (var slot in _slots)
            {
                slot.Server?.Terminate();
            }
        }
    }

    private enum StopReason
    {
        None,
        Restart,
        TimedOut,
        Removed,
    }

    private sealed class Slot(int instance)
    {
        public int Instance { get; } = instance;

        public IServerInstance? Server { get; set; }

        public ServerState State { get; set; } = ServerState.Waiting;

        public DateTimeOffset StartedAt { get; private set; }

        public DateTimeOffset? Since { get; set; }

        public DateTimeOffset NextStart { get; set; }

        public DateTimeOffset ServerReadyAt { get; set; }

        public DateTimeOffset PortsCheckedAt { get; private set; }

        public int Failures { get; set; }

        public int Restarts { get; set; }

        public IReadOnlyList<int> Ports { get; set; } = [];

        public string Note { get; private set; } = "";

        public StopReason StopReason { get; set; }

        public bool Removing { get; set; }

        public bool Removed { get; set; }

        public void Begin(IServerInstance server, DateTimeOffset now)
        {
            Server = server;
            State = ServerState.Starting;
            StartedAt = now;
            Since = now;
            Note = "booting";
        }

        public void Stop(StopReason reason)
        {
            if (Server is null || State == ServerState.Stopping)
            {
                return;
            }

            StopReason = reason;
            State = ServerState.Stopping;
            Note = "stopping";
            Server.Terminate();
        }

        public void RefreshPorts(DateTimeOffset now)
        {
            PortsCheckedAt = now;
            Ports = Server?.UdpPorts() ?? [];
        }

        public bool SetNote(string note)
        {
            if (Note == note)
            {
                return false;
            }

            Note = note;
            return true;
        }

        public bool SetState(ServerState state)
        {
            if (State == state)
            {
                return false;
            }

            State = state;
            Note = "stopping";
            return true;
        }

        public ServerSlotView View() => new(Instance, State, Server?.Id, Since, Restarts, Ports, Note);
    }
}
