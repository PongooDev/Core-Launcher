using Core.Launcher.Hosting.Builds;
using Core.Launcher.Hosting.Settings;

namespace Core.Launcher.Hosting.Servers;

/// <summary>What a server hosts.</summary>
public enum ServerMode
{
    /// <summary>Save the World: every server of a build without Battle Royale, and the selected build's first ones.</summary>
    SaveTheWorld,

    /// <summary>Battle Royale (<see cref="KnownBuilds.BattleRoyaleServerArguments"/>): its matches come from the backend's matchmaking.</summary>
    BattleRoyale,

    /// <summary>
    /// Creative, on a build that has it (<see cref="KnownBuilds.HasCreative"/>, 7.00 on): a Battle Royale server that the
    /// launcher keeps for Creative. It starts as Battle Royale's do (<see cref="KnownBuilds.BattleRoyaleServerArguments"/>),
    /// and the backend's matchmaking gives it whichever match comes, a Creative party's or a Battle Royale one.
    /// </summary>
    Creative,
}

/// <summary>What a server slot hosts: a build (by its name in the settings) and its mode.</summary>
public sealed record ServerAssignment(string Build, ServerMode Mode)
{
    /// <summary>
    /// Whether it starts as a Battle Royale server and takes its matches from the backend's matchmaking: Battle Royale's
    /// servers and Creative's. Core-Backend gives a Creative party (Playlist_PlaygroundV2) any free one of its build.
    /// </summary>
    public bool BattleRoyale => Mode != ServerMode.SaveTheWorld;

    /// <summary>
    /// Whether a server of this assignment takes <paramref name="other"/>'s players: the same build, and both Save the
    /// World or both on the matchmaking service (Battle Royale's and Creative's servers take each other's matches).
    /// </summary>
    public bool Same(ServerAssignment? other) =>
        other is not null && other.BattleRoyale == BattleRoyale && string.Equals(other.Build, Build, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Players of a build waiting for a server none of the running ones can take (Core-Backend's GET /core/api/demand).</summary>
public sealed record BuildDemand(int Changelist, bool BattleRoyale, int Players);

/// <summary>A server to move to another build: slot <see cref="Instance"/>, from now on hosting <see cref="To"/>.</summary>
public sealed record ServerSwitch(int Instance, ServerAssignment From, ServerAssignment To);

/// <summary>
/// Which build each server slot hosts (Core-Launcher #18). The selected build keeps today's servers: Servers to keep
/// running (Save the World), on a build with Battle Royale the Battle Royale servers after them, and on a build with
/// Creative (7.00 on, Core-Launcher #20) the Creative servers after those. Every build with Extra servers (Settings →
/// Builds) gets that many more after those, whichever build is selected: Battle Royale on a build that has it, otherwise
/// Save the World. So with no extra servers anywhere nothing changes.
/// </summary>
public static class ServerPlan
{
    public static IReadOnlyList<ServerAssignment> For(LauncherSettings settings)
    {
        var selected = settings.FindSelectedBuild();
        var name = selected?.Name ?? "";
        var plan = new List<ServerAssignment>();
        plan.AddRange(Enumerable.Repeat(new ServerAssignment(name, ServerMode.SaveTheWorld), settings.Servers.Instances));
        if (selected is not null && KnownBuilds.HasBattleRoyale(selected.Changelist))
        {
            plan.AddRange(Enumerable.Repeat(new ServerAssignment(name, ServerMode.BattleRoyale), settings.Servers.BattleRoyaleInstances));
        }

        if (selected is not null && KnownBuilds.HasCreative(selected.Changelist))
        {
            plan.AddRange(Enumerable.Repeat(new ServerAssignment(name, ServerMode.Creative), settings.Servers.CreativeInstances));
        }

        foreach (var build in settings.Builds.Where(build => build.ExtraServers > 0))
        {
            plan.AddRange(Enumerable.Repeat(new ServerAssignment(build.Name, ModeOf(build.Changelist, true)), build.ExtraServers));
        }

        return plan;
    }

    /// <summary>
    /// The build players waiting for <paramref name="demand"/> need a server of: the first build in the settings with
    /// that changelist that can host. Null when there is none, so the demand is left to other PCs.
    /// </summary>
    public static ServerAssignment? Resolve(LauncherSettings settings, BuildDemand demand) =>
        settings.Builds.FirstOrDefault(build => build.Changelist == demand.Changelist && build.Changelist > 0 && KnownBuilds.CanHost(build)) is { } found
            ? new ServerAssignment(found.Name, ModeOf(found.Changelist, demand.BattleRoyale))
            : null;

    private static ServerMode ModeOf(int changelist, bool battleRoyale) =>
        battleRoyale && KnownBuilds.HasBattleRoyale(changelist) ? ServerMode.BattleRoyale : ServerMode.SaveTheWorld;
}

/// <summary>
/// Moves an idle server to a build whose players wait for one (Settings → Servers, "Switch idle servers to the builds
/// players are on", Core-Launcher #18). A server is idle once it has been ready with nobody in its session for
/// <see cref="IdleFor"/>. A build gets one only when none of its servers here is free or booting, and an idle server is
/// never taken from a build that is waiting itself. A moved server stays on its new build until players of another
/// build need it, its own planned build included, or the settings change.
/// </summary>
public sealed class ServerSwitcher(TimeProvider time)
{
    /// <summary>How long a ready server has to be empty before it may move: a match's players take a few seconds to arrive.</summary>
    public static readonly TimeSpan IdleFor = TimeSpan.FromSeconds(30);

    private readonly Dictionary<int, ServerAssignment> _overrides = [];
    private readonly Dictionary<int, DateTimeOffset> _idleSince = [];

    /// <summary>What slot <paramref name="instance"/> hosts: the build it was moved to, otherwise the plan's.</summary>
    public ServerAssignment? Assignment(IReadOnlyList<ServerAssignment> plan, int instance) =>
        _overrides.TryGetValue(instance, out var moved) ? moved
        : instance >= 1 && instance <= plan.Count ? plan[instance - 1]
        : null;

    public bool HasOverrides => _overrides.Count > 0;

    /// <summary>Puts every server back on the plan (the settings changed, or switching was turned off).</summary>
    public void Clear()
    {
        _overrides.Clear();
        _idleSince.Clear();
    }

    /// <summary>
    /// The server to move now, if any: at most one a call, recorded as moved before it is returned (the caller restarts
    /// it). <paramref name="players"/> is a ready server's session's players, null when the backend doesn't say;
    /// <paramref name="resolve"/> names the build of a demand, or null when this PC can't host it.
    /// </summary>
    public ServerSwitch? Decide(IReadOnlyList<ServerAssignment> plan, IReadOnlyList<ServerSlotView> slots, Func<ServerSlotView, SessionPlayers?> players,
        IReadOnlyList<BuildDemand> demand, Func<BuildDemand, ServerAssignment?> resolve)
    {
        var now = time.GetUtcNow();
        foreach (var instance in _overrides.Keys.Where(instance => instance > plan.Count).ToList())
        {
            _overrides.Remove(instance);
        }

        var counts = new Dictionary<int, SessionPlayers?>();
        foreach (var slot in slots)
        {
            var count = slot.State == ServerState.Ready ? players(slot) : null;
            counts[slot.Instance] = count;
            if (slot.State == ServerState.Ready && count is { Current: 0 })
            {
                _idleSince.TryAdd(slot.Instance, now);
            }
            else
            {
                _idleSince.Remove(slot.Instance);
            }
        }

        var wanted = demand.Where(entry => entry.Players > 0).Select(resolve).OfType<ServerAssignment>().ToList();
        foreach (var target in wanted)
        {
            // A server of the build that is (re)starting, or ready and empty (or not counted), takes its players already.
            if (slots.Any(slot => target.Same(Assignment(plan, slot.Instance)) && (slot.State != ServerState.Ready || counts[slot.Instance] is not { Current: > 0 })))
            {
                continue;
            }

            var candidate = slots
                .Where(slot => slot.State == ServerState.Ready && _idleSince.TryGetValue(slot.Instance, out var since) && now - since >= IdleFor)
                .Where(slot => Assignment(plan, slot.Instance) is { } current && !wanted.Any(current.Same))
                // A server moved off the build first goes back to it; otherwise the last one, the plan's least needed.
                .OrderByDescending(slot => target.Same(slot.Instance <= plan.Count ? plan[slot.Instance - 1] : null))
                .ThenByDescending(slot => slot.Instance)
                .FirstOrDefault();
            if (candidate is null)
            {
                continue;
            }

            var from = Assignment(plan, candidate.Instance)!;
            var to = target;
            if (candidate.Instance <= plan.Count && target.Same(plan[candidate.Instance - 1]))
            {
                // Back on its plan's assignment, which may be a Creative server taking Battle Royale's players.
                _overrides.Remove(candidate.Instance);
                to = plan[candidate.Instance - 1];
            }
            else
            {
                _overrides[candidate.Instance] = target;
            }

            _idleSince.Remove(candidate.Instance);
            return new ServerSwitch(candidate.Instance, from, to);
        }

        return null;
    }
}
