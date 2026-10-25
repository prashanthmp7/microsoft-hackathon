using SecondHalfMind.Domain;

namespace SecondHalfMind.Data;

/// <summary>Folds the event stream into an immutable <see cref="MatchSnapshot"/>. Platform-owned.</summary>
internal sealed class StateProjector
{
    private readonly string _matchId;
    private readonly Dictionary<string, PlayerAccumulator> _players = new(StringComparer.Ordinal);
    private readonly TeamAccumulator _home = new();
    private readonly TeamAccumulator _away = new();
    private readonly List<GoalRecord> _goals = [];

    private int _clock;
    private long _lastSequence;
    private Side? _inPossession;
    private MatchPhase _phase = MatchPhase.PreMatch;

    public StateProjector(string matchId, Club home, Club away)
    {
        _matchId = matchId;
        foreach (var (club, side) in new[] { (home, Side.Home), (away, Side.Away) })
        {
            for (var i = 0; i < club.Squad.Count; i++)
            {
                var player = club.Squad[i];
                _players[player.Id] = new PlayerAccumulator(player, side, onPitch: i < 11);
            }
        }
    }

    public MatchSnapshot Apply(MatchEvent e)
    {
        AdvanceClock(e.ClockSeconds);
        _lastSequence = e.Sequence;
        var team = e.Team == Side.Home ? _home : _away;
        var player = e.PlayerId is null ? null : _players.GetValueOrDefault(e.PlayerId);

        switch (e.Type)
        {
            case MatchEventType.KickOff:
                _phase = _phase == MatchPhase.HalfTime ? MatchPhase.SecondHalf : _phase == MatchPhase.PreMatch ? MatchPhase.FirstHalf : _phase;
                _inPossession = e.Team;
                break;
            case MatchEventType.Pass:
                _inPossession = e.Team;
                team.PassesAttempted++;
                if (player is not null)
                {
                    player.Passes++;
                }

                if (e.Success == true)
                {
                    team.PassesCompleted++;
                    if (player is not null)
                    {
                        player.PassesCompleted++;
                    }
                }

                break;
            case MatchEventType.Shot:
                _inPossession = e.Team;
                team.Shots++;
                team.ExpectedGoals += e.ExpectedGoals ?? 0;
                team.MaxShotSpeed = Math.Max(team.MaxShotSpeed, e.SpeedKmh ?? 0);
                if (e.OnTarget == true)
                {
                    team.ShotsOnTarget++;
                }

                if (player is not null)
                {
                    player.Shots++;
                }

                break;
            case MatchEventType.Goal:
                team.Goals++;
                if (player is not null)
                {
                    player.Goals++;
                }

                _goals.Add(new GoalRecord(e.Minute, e.Team, e.PlayerId ?? string.Empty));
                break;
            case MatchEventType.Tackle:
                team.Tackles++;
                _inPossession = e.Team;
                if (player is not null)
                {
                    player.Tackles++;
                }

                break;
            case MatchEventType.Interception:
                team.Interceptions++;
                _inPossession = e.Team;
                break;
            case MatchEventType.PossessionChange:
                _inPossession = e.Team;
                break;
            case MatchEventType.Pressure:
                team.PressureEvents++;
                break;
            case MatchEventType.Foul:
                team.Fouls++;
                break;
            case MatchEventType.Corner:
                team.Corners++;
                _inPossession = e.Team;
                break;
            case MatchEventType.YellowCard:
                team.YellowCards++;
                break;
            case MatchEventType.RedCard:
                team.RedCards++;
                if (player is not null)
                {
                    player.OnPitch = false;
                }

                break;
            case MatchEventType.Substitution:
                team.Substitutions++;
                if (player is not null)
                {
                    player.OnPitch = true;
                }

                if (e.TargetPlayerId is not null && _players.TryGetValue(e.TargetPlayerId, out var outgoing))
                {
                    outgoing.OnPitch = false;
                }

                break;
            case MatchEventType.Sprint:
                if (player is not null)
                {
                    player.TopSpeed = Math.Max(player.TopSpeed, e.SpeedKmh ?? 0);
                }

                break;
            case MatchEventType.HalfTime:
                _phase = MatchPhase.HalfTime;
                break;
            case MatchEventType.FullTime:
                _phase = MatchPhase.FullTime;
                break;
            case MatchEventType.Save:
                break;
        }

        return Snapshot();
    }

    public MatchSnapshot Snapshot() => new(
        _matchId,
        _lastSequence,
        _clock,
        _phase,
        _home.ToStats(),
        _away.ToStats(),
        _goals.ToArray(),
        _players.Values.ToDictionary(p => p.Player.Id, p => p.ToLive(), StringComparer.Ordinal));

    private void AdvanceClock(int clock)
    {
        var delta = clock - _clock;
        if (delta <= 0)
        {
            return;
        }

        if (_inPossession is { } side && _phase is MatchPhase.FirstHalf or MatchPhase.SecondHalf)
        {
            (side == Side.Home ? _home : _away).PossessionSeconds += delta;
        }

        if (_phase is MatchPhase.FirstHalf or MatchPhase.SecondHalf)
        {
            foreach (var p in _players.Values.Where(p => p.OnPitch))
            {
                // Roughly 9.8 - 12.6 km over 90 minutes depending on stamina; goalkeepers cover far less.
                var kmPerMinute = (9.8 + 2.8 * p.Player.Attributes.Stamina) / 90 * (p.Player.Position == Position.Goalkeeper ? 0.45 : 1);
                p.DistanceKm += kmPerMinute * delta / 60.0;
            }
        }

        _clock = clock;
    }

    private sealed class TeamAccumulator
    {
        public int Goals, Shots, ShotsOnTarget, PassesAttempted, PassesCompleted, Tackles, Interceptions,
            Corners, Fouls, YellowCards, RedCards, PressureEvents, Substitutions;
        public double ExpectedGoals, PossessionSeconds, MaxShotSpeed;

        public TeamStats ToStats() => new(Goals, Shots, ShotsOnTarget, Math.Round(ExpectedGoals, 2), PassesAttempted,
            PassesCompleted, Tackles, Interceptions, Corners, Fouls, YellowCards, RedCards, PressureEvents, Substitutions,
            PossessionSeconds, MaxShotSpeed);
    }

    private sealed class PlayerAccumulator(Player player, Side side, bool onPitch)
    {
        public Player Player { get; } = player;
        public Side Side { get; } = side;
        public bool OnPitch { get; set; } = onPitch;
        public int Passes { get; set; }
        public int PassesCompleted { get; set; }
        public int Shots { get; set; }
        public int Goals { get; set; }
        public int Tackles { get; set; }
        public double DistanceKm { get; set; }
        public double TopSpeed { get; set; }

        public PlayerLive ToLive() => new(Player.Id, Side, OnPitch, Passes, PassesCompleted, Shots, Goals, Tackles,
            Math.Round(DistanceKm, 2), TopSpeed);
    }
}
