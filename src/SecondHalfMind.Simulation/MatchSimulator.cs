using SecondHalfMind.Domain;

namespace SecondHalfMind.Simulation;

/// <summary>
/// Event-level synthetic match generator. A match is a sequence of possession chains made of passes,
/// pressure, tackles, interceptions, shots, set pieces, cards, substitutions and sprints.
/// Output is deterministic for a given seed. Rates are tied to the calibration profile so totals
/// (goals, shots, corners, cards) land near real top-flight averages.
/// </summary>
public sealed class MatchSimulator
{
    public const int HalfLengthSeconds = 2700;
    public const int MatchLengthSeconds = 5400;
    private const double PitchLength = 105;
    private const double PitchWidth = 68;

    private readonly MatchInfo _match;
    private readonly CalibrationProfile _calibration;
    private readonly Random _random;
    private readonly List<MatchEvent> _events = [];
    private readonly TeamState _home;
    private readonly TeamState _away;

    private long _sequence;
    private int _clock;
    private TeamState _possession;
    private Player? _carrier;
    private double _a = 52.5; // distance from the possessing team's own goal line
    private double _y = 34;

    private MatchSimulator(MatchInfo match, Club home, Club away, CalibrationProfile calibration)
    {
        _match = match;
        _calibration = calibration;
        _random = new Random(match.Seed);
        var (lambdaHome, lambdaAway) = StrengthModel.ExpectedGoals(home, away, calibration);
        _home = new TeamState(Side.Home, home, lambdaHome, _random);
        _away = new TeamState(Side.Away, away, lambdaAway, _random);
        _possession = _home;
    }

    public static IReadOnlyList<MatchEvent> Simulate(MatchInfo match, Club home, Club away, CalibrationProfile calibration)
    {
        var simulator = new MatchSimulator(match, home, away, calibration);
        simulator.Run();
        return simulator._events;
    }

    private TeamState Opponent(TeamState team) => team.Side == Side.Home ? _away : _home;

    private void Run()
    {
        KickOff(_home);
        var secondHalfStarted = false;

        while (_clock < MatchLengthSeconds)
        {
            if (!secondHalfStarted && _clock >= HalfLengthSeconds)
            {
                _clock = HalfLengthSeconds;
                Emit(MatchEventType.HalfTime, _possession, null);
                secondHalfStarted = true;
                KickOff(_away);
                continue;
            }

            ApplySubstitutions(_home);
            ApplySubstitutions(_away);
            UpdateIntensity(_home);
            UpdateIntensity(_away);

            if (_random.Chance(0.3))
            {
                EmitSprint();
            }

            RunPossessionChain(secondHalfStarted ? MatchLengthSeconds : HalfLengthSeconds);
        }

        _clock = MatchLengthSeconds;
        Emit(MatchEventType.FullTime, _possession, null);
    }

    private void KickOff(TeamState team)
    {
        _possession = team;
        _a = 52.5;
        _y = 34;
        _carrier = team.PickByZone(_random, 50, exclude: null);
        Emit(MatchEventType.KickOff, team, _carrier);
    }

    private void RunPossessionChain(int periodEnd)
    {
        var team = _possession;
        var opponent = Opponent(team);
        var shotPropensity = ShotPropensity(team);

        while (_clock < periodEnd)
        {
            var passer = _carrier ?? team.PickByZone(_random, _a, exclude: null);

            // Opposition pressure on the ball.
            var pressureChance = Math.Clamp(0.2 + 0.3 * opponent.Intensity + 0.05 * (opponent.RedCards == 0 ? 1 : -1), 0.05, 0.6);
            var pressured = _random.Chance(pressureChance);
            if (pressured)
            {
                var presser = opponent.PickByZone(_random, PitchLength - _a, exclude: null);
                Emit(MatchEventType.Pressure, opponent, presser, x: PitchX(team, _a), y: _y);
            }

            // Pass geometry in the attacking frame.
            var length = Math.Clamp(_random.NextGaussian(16, 8), 4, 55);
            var forward = Math.Clamp(length * _random.NextGaussian(0.4, 0.5), -length, length);
            var lateral = Math.Sqrt(Math.Max(0, length * length - forward * forward)) * (_random.Chance(0.5) ? 1 : -1);
            var newA = Math.Clamp(_a + forward, 1, PitchLength - 1);
            var newY = Math.Clamp(_y + lateral, 2, PitchWidth - 2);

            var successChance = 0.86
                + 0.14 * (passer.Attributes.Passing - 0.6)
                - (pressured ? 0.12 : 0)
                - 0.004 * Math.Max(0, length - 18)
                - (newA > 88 ? 0.07 : 0)
                + 0.06 * team.Intensity
                - 0.05 * (team.RedCards > 0 ? 1 : 0)
                - 0.04 * (1 / opponent.Club.Defense - 1)
                + 0.05 * Math.Log(team.ExpectedGoals / Math.Max(0.2, opponent.ExpectedGoals));
            var success = _random.Chance(Math.Clamp(successChance, 0.45, 0.97));

            var ballSpeedMs = Math.Clamp(9 + 0.25 * length + _random.NextGaussian(0, 1.5), 7, 30);
            var flightSeconds = length / ballSpeedMs;
            var receiver = team.PickByZone(_random, newA, exclude: passer);

            Emit(MatchEventType.Pass, team, passer, receiver.Id,
                x: PitchX(team, _a), y: _y, endX: PitchX(team, newA), endY: newY,
                success: success, underPressure: pressured, speedKmh: Math.Round(ballSpeedMs * 3.6, 1));
            _clock += (int)Math.Ceiling(flightSeconds + 1 + _random.NextDouble() * 2.5);

            if (!success)
            {
                LoseBall(team, opponent, (_a + newA) / 2, newY);
                return;
            }

            _a = newA;
            _y = newY;
            _carrier = receiver;

            if (_random.Chance(0.031))
            {
                Foul(team, opponent);
            }

            if (_random.Chance(0.035))
            {
                // Ball out of play, possession goes to the opponent (throw-in or goal kick).
                ChangePossession(opponent, PitchLength - _a, _y, null);
                return;
            }

            if (_a > 92 && (_y < 14 || _y > PitchWidth - 14) && _random.Chance(0.06))
            {
                Corner(team, opponent);
                return;
            }

            if (_a >= 75)
            {
                var shotChance = shotPropensity * (1 + 0.5 * team.Intensity) * (_a >= 88 ? 1.7 : 1.0);
                if (_random.Chance(shotChance))
                {
                    Shoot(team, opponent, _carrier, passer, setPiece: false);
                    return;
                }
            }
        }
    }

    private double ShotPropensity(TeamState team)
    {
        var leagueMean = (_calibration.MeanHomeGoals + _calibration.MeanAwayGoals) / 2;
        var redCardFactor = team.RedCards > 0 ? 0.75 : 1.0;
        var opponentRedCardFactor = Opponent(team).RedCards > 0 ? 1.25 : 1.0;
        // Super-linear in expected goals so stronger sides convert their possession into more shots.
        return 0.057 * Math.Pow(team.ExpectedGoals / leagueMean, 1.6) * redCardFactor * opponentRedCardFactor
            * (_calibration.ShotsPerTeamMean / 12.8);
    }

    private void LoseBall(TeamState team, TeamState opponent, double atA, double atY)
    {
        var winner = opponent.PickByZone(_random, PitchLength - atA, exclude: null);
        var type = _random.Chance(0.6) ? MatchEventType.Interception : MatchEventType.Tackle;
        Emit(type, opponent, winner, x: PitchX(team, atA), y: atY, success: true);
        ChangePossession(opponent, PitchLength - atA, atY, winner);
    }

    private void ChangePossession(TeamState newTeam, double newA, double newY, Player? carrier)
    {
        _possession = newTeam;
        _a = newA;
        _y = newY;
        _carrier = carrier ?? newTeam.PickByZone(_random, newA, exclude: null);
        Emit(MatchEventType.PossessionChange, newTeam, _carrier, x: PitchX(newTeam, _a), y: _y);
        _clock += 2 + _random.Next(5);
    }

    private void Foul(TeamState victimTeam, TeamState foulingTeam)
    {
        var offender = foulingTeam.PickByZone(_random, PitchLength - _a, exclude: null);
        Emit(MatchEventType.Foul, foulingTeam, offender, _carrier?.Id, x: PitchX(victimTeam, _a), y: _y);
        _clock += 8 + _random.Next(15);

        var yellowChance = _calibration.YellowCardsPerTeamMean / 10.5;
        var redChance = _calibration.RedCardsPerTeamMean / 10.5;
        if (_random.Chance(redChance) && foulingTeam.OnPitch.Count > 7)
        {
            Emit(MatchEventType.RedCard, foulingTeam, offender, x: PitchX(victimTeam, _a), y: _y);
            foulingTeam.SendOff(offender);
        }
        else if (_random.Chance(yellowChance))
        {
            Emit(MatchEventType.YellowCard, foulingTeam, offender, x: PitchX(victimTeam, _a), y: _y);
        }
    }

    private void Corner(TeamState team, TeamState opponent)
    {
        var taker = team.PickByZone(_random, 95, exclude: null);
        _a = PitchLength;
        _y = _random.Chance(0.5) ? 0 : PitchWidth;
        Emit(MatchEventType.Corner, team, taker, x: PitchX(team, _a), y: _y);
        _clock += 25 + _random.Next(20);

        if (_random.Chance(0.3))
        {
            _a = PitchLength - 8 - _random.NextDouble() * 6;
            _y = 34 + _random.NextGaussian(0, 6);
            var header = team.PickByZone(_random, 100, exclude: taker);
            Shoot(team, opponent, header, taker, setPiece: true);
        }
        else
        {
            ChangePossession(opponent, PitchLength - (PitchLength - 10), 34 + _random.NextGaussian(0, 8), null);
        }
    }

    private void Shoot(TeamState team, TeamState opponent, Player shooter, Player? assister, bool setPiece)
    {
        var distance = Math.Sqrt(Math.Pow(PitchLength - _a, 2) + Math.Pow(34 - _y, 2));
        var xg = Math.Clamp(0.75 * Math.Exp(-distance / 7.5) + 0.02, 0.02, 0.75) * (setPiece ? 0.7 : 1.0);
        var finishing = shooter.Attributes.Finishing;
        var onTargetChance = Math.Clamp(0.28 + 0.3 * (finishing - 0.5) + 0.12 * Math.Exp(-distance / 12), 0.12, 0.7);
        var onTarget = _random.Chance(onTargetChance);
        var speed = Math.Clamp(68 + 40 * finishing - 0.35 * distance + _random.NextGaussian(0, 9), 45, 132);

        Emit(MatchEventType.Shot, team, shooter, assister?.Id,
            x: PitchX(team, _a), y: _y, endX: PitchX(team, PitchLength), endY: 34 + _random.NextGaussian(0, onTarget ? 2.5 : 5),
            success: onTarget, speedKmh: Math.Round(speed, 1), expectedGoals: Math.Round(xg, 3), onTarget: onTarget);
        _clock += 3;

        var keeper = opponent.Goalkeeper;
        if (onTarget)
        {
            var keeperFactor = 1.1 - 0.4 * (keeper.Attributes.Defending - 0.5);
            var goalChance = Math.Clamp(1.0 * xg / Math.Max(onTargetChance, 0.2) * (0.8 + 0.4 * finishing) * keeperFactor, 0.02, 0.9);
            if (_random.Chance(goalChance))
            {
                Emit(MatchEventType.Goal, team, shooter, assister?.Id, x: PitchX(team, _a), y: _y, expectedGoals: Math.Round(xg, 3));
                team.Goals++;
                _clock += 45 + _random.Next(30);
                KickOff(opponent);
                return;
            }

            Emit(MatchEventType.Save, opponent, keeper, shooter.Id, x: PitchX(team, PitchLength), y: 34);
            if (_random.Chance(0.35))
            {
                Corner(team, opponent);
                return;
            }
        }
        else if (_random.Chance(0.2))
        {
            Corner(team, opponent);
            return;
        }

        _clock += 6 + _random.Next(10);
        ChangePossession(opponent, 6, 34, keeper);
    }

    private void ApplySubstitutions(TeamState team)
    {
        while (team.PendingSubClocks.Count > 0 && team.PendingSubClocks[0] <= _clock)
        {
            team.PendingSubClocks.RemoveAt(0);
            var (outgoing, incoming) = team.MakeSubstitution(_random);
            if (outgoing is null || incoming is null)
            {
                continue;
            }

            if (_carrier == outgoing)
            {
                _carrier = incoming;
            }

            Emit(MatchEventType.Substitution, team, incoming, outgoing.Id, x: 52.5, y: 0);
            team.Intensity = Math.Clamp(team.Intensity + 0.22, -0.5, 0.5);
            _clock += 20;
        }
    }

    private void UpdateIntensity(TeamState team)
    {
        // Mean-reverting random walk with slight late fatigue. After the hour a trailing side pushes
        // and a leading side sits deeper, which produces genuine late spells of pressure.
        var fatigue = _clock > 4200 ? -0.004 : 0;
        var margin = team.Goals - Opponent(team).Goals;
        var gameState = _clock > 3600 ? -0.02 * Math.Clamp(margin, -2, 2) : 0;
        team.Intensity = Math.Clamp(team.Intensity * 0.97 + _random.NextGaussian(0, 0.035) + fatigue + gameState, -0.5, 0.5);
    }

    private void EmitSprint()
    {
        var team = _random.Chance(0.5) ? _home : _away;
        var outfield = team.OnPitch.Where(p => p.Position != Position.Goalkeeper).ToList();
        var runner = _random.Pick(outfield);
        var speed = Math.Clamp(24.5 + 10 * runner.Attributes.Pace + _random.NextGaussian(0, 1.2), 21, 37.5);
        Emit(MatchEventType.Sprint, team, runner, x: PitchX(_possession, _a), y: _y, speedKmh: Math.Round(speed, 1));
    }

    private static double PitchX(TeamState team, double attackingFrameA) =>
        Math.Round(team.Side == Side.Home ? attackingFrameA : PitchLength - attackingFrameA, 1);

    private void Emit(
        MatchEventType type,
        TeamState team,
        Player? player,
        string? targetPlayerId = null,
        double? x = null,
        double? y = null,
        double? endX = null,
        double? endY = null,
        bool? success = null,
        bool underPressure = false,
        double? speedKmh = null,
        double? expectedGoals = null,
        bool? onTarget = null)
    {
        _sequence++;
        var clock = Math.Min(_clock, type == MatchEventType.FullTime ? MatchLengthSeconds : MatchLengthSeconds - 1);
        _events.Add(new MatchEvent
        {
            Id = $"{_match.Id}-{_sequence:00000}",
            MatchId = _match.Id,
            Sequence = _sequence,
            ClockSeconds = clock,
            Type = type,
            Team = team.Side,
            PlayerId = player?.Id,
            TargetPlayerId = targetPlayerId,
            X = Math.Round(x ?? PitchX(team, _a), 1),
            Y = Math.Round(Math.Clamp(y ?? _y, 0, PitchWidth), 1),
            EndX = endX,
            EndY = endY is null ? null : Math.Round(Math.Clamp(endY.Value, 0, PitchWidth), 1),
            Success = success,
            UnderPressure = underPressure,
            SpeedKmh = speedKmh,
            ExpectedGoals = expectedGoals,
            OnTarget = onTarget,
        });
    }

    private sealed class TeamState
    {
        private readonly List<Player> _bench;

        public TeamState(Side side, Club club, double expectedGoals, Random random)
        {
            Side = side;
            Club = club;
            ExpectedGoals = expectedGoals;
            OnPitch = club.Squad.Take(11).ToList();
            _bench = club.Squad.Skip(11).ToList();
            Goalkeeper = OnPitch.First(p => p.Position == Position.Goalkeeper);
            PendingSubClocks = Enumerable.Range(0, 3).Select(_ => 3300 + random.Next(1500)).Order().ToList();
        }

        public Side Side { get; }
        public Club Club { get; }
        public double ExpectedGoals { get; }
        public List<Player> OnPitch { get; }
        public Player Goalkeeper { get; }
        public List<int> PendingSubClocks { get; }
        public double Intensity { get; set; }
        public int RedCards { get; private set; }
        public int Goals { get; set; }

        public void SendOff(Player player)
        {
            if (player.Position != Position.Goalkeeper && OnPitch.Remove(player))
            {
                RedCards++;
            }
        }

        public (Player? Outgoing, Player? Incoming) MakeSubstitution(Random random)
        {
            var outgoing = OnPitch
                .Where(p => p.Position is Position.Midfielder or Position.Forward)
                .OrderBy(p => p.Attributes.Stamina + random.NextDouble() * 0.2)
                .FirstOrDefault();
            if (outgoing is null)
            {
                return (null, null);
            }

            var incoming = _bench.FirstOrDefault(p => p.Position == outgoing.Position)
                ?? _bench.FirstOrDefault(p => p.Position != Position.Goalkeeper);
            if (incoming is null)
            {
                return (null, null);
            }

            OnPitch[OnPitch.IndexOf(outgoing)] = incoming;
            _bench.Remove(incoming);
            return (outgoing, incoming);
        }

        /// <summary>Pick an on-pitch player appropriate to the ball's zone (attacking frame).</summary>
        public Player PickByZone(Random random, double a, Player? exclude)
        {
            var candidates = OnPitch.Where(p => p != exclude).ToList();
            return random.PickWeighted(candidates, p => (p.Position, a) switch
            {
                (Position.Goalkeeper, < 25) => 0.6,
                (Position.Goalkeeper, _) => 0.02,
                (Position.Defender, < 35) => 2.5,
                (Position.Defender, < 70) => 1.2,
                (Position.Defender, _) => 0.4,
                (Position.Midfielder, < 35) => 1.8,
                (Position.Midfielder, _) => 2.6,
                (Position.Forward, < 35) => 0.4,
                (Position.Forward, < 70) => 1.4,
                (Position.Forward, _) => 3.0,
                _ => 1.0,
            });
        }
    }
}
