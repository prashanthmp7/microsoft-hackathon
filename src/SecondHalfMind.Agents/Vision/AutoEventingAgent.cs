using System.Collections.Concurrent;
using SecondHalfMind.Agents.Analytics;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Agents.Vision;

/// <summary>
/// Auto-eventing: measures pass distance, ball speed and shot speed from ball-tracking frames
/// instead of trusting hand-tagged values. In this project the frames are synthetic; the same
/// contract would accept positions from a video model. The tagged event is used only to score
/// agreement, which is reported so viewers can see how far the measured numbers can be trusted.
/// </summary>
public sealed class AutoEventingAgent : IMatchAgent
{
    private const int SummaryEverySeconds = 900;

    private readonly ITrackingReadModel _tracking;
    private readonly ConcurrentDictionary<string, Memory> _memory = new(StringComparer.Ordinal);

    public AutoEventingAgent(ITrackingReadModel tracking)
    {
        _tracking = tracking;
    }

    public string Name => "vision";

    public AgentStage Stage => AgentStage.Perception;

    public IEnumerable<Insight> OnEvent(AgentContext ctx)
    {
        var e = ctx.Event;
        var memory = _memory.GetOrAdd(ctx.Match.Id, _ => new Memory());
        var output = new List<Insight>();

        if (e.Type is MatchEventType.Pass or MatchEventType.Shot
            && _tracking.GetBallTrack(ctx.Match.Id, e.Sequence) is { } track
            && BallTrackAnalyzer.Analyze(track) is { } kick)
        {
            lock (memory)
            {
                memory.Detections++;
                memory.SpeedErrorSum += Math.Abs(kick.SpeedKmh - (e.SpeedKmh ?? kick.SpeedKmh));
                memory.DistanceErrorSum += Math.Abs(kick.DistanceM - PassDifficulty.Length(e));
            }

            if (e.Type == MatchEventType.Shot)
            {
                output.Add(Shot(ctx, kick));
            }
        }

        if (e.ClockSeconds - memory.LastSummaryClock >= SummaryEverySeconds && memory.Detections > 0)
        {
            memory.LastSummaryClock = e.ClockSeconds - e.ClockSeconds % SummaryEverySeconds;
            output.Add(Summary(ctx, memory));
        }

        return output;
    }

    private Insight Shot(AgentContext ctx, BallKick kick)
    {
        var e = ctx.Event;
        var speed = Math.Round(kick.SpeedKmh);
        var distance = Math.Round(kick.DistanceM);
        var tagged = Math.Round(e.SpeedKmh ?? kick.SpeedKmh);
        var shooter = ctx.PlayerName(e.PlayerId);
        return InsightFactory.Create(ctx, Name, InsightKind.Stat, 2,
            new TextVariant(
                Fmt.S($"Tracked shot: {Fmt.N0(speed)} km/h"),
                Fmt.S($"Measured from {kick.FramesUsed} tracking frames over {Fmt.N0(distance)} m; tagged at {Fmt.N0(tagged)} km/h.")),
            new TextVariant(
                Fmt.S($"Ball tracker: {Fmt.N0(speed)} km/h"),
                Fmt.S($"The tracker clocked {shooter}'s strike over {Fmt.N0(distance)} m.")),
            new Dictionary<string, double>
            {
                ["detectedSpeedKmh"] = speed,
                ["detectedDistanceM"] = distance,
                ["taggedSpeedKmh"] = tagged,
                ["frames"] = kick.FramesUsed,
            },
            e.Team, e.PlayerId, "autoEventing");
    }

    private Insight Summary(AgentContext ctx, Memory memory)
    {
        int count;
        double speedError, distanceError;
        lock (memory)
        {
            count = memory.Detections;
            speedError = Math.Round(memory.SpeedErrorSum / count, 1);
            distanceError = Math.Round(memory.DistanceErrorSum / count, 1);
        }

        return InsightFactory.Create(ctx, Name, InsightKind.Stat, 2,
            new TextVariant(
                Fmt.S($"Auto-eventing: {count} actions measured"),
                Fmt.S($"Ball speed within {Fmt.N1(speedError)} km/h and distance within {Fmt.N1(distanceError)} m of tagged data on average.")),
            new TextVariant(
                Fmt.S($"Ball tracking is keeping up"),
                Fmt.S($"{count} passes and shots measured automatically from tracking frames.")),
            new Dictionary<string, double>
            {
                ["detections"] = count,
                ["meanSpeedErrorKmh"] = speedError,
                ["meanDistanceErrorM"] = distanceError,
            },
            metric: "autoEventing",
            suffix: ctx.Event.ClockSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private sealed class Memory
    {
        public int Detections;
        public double SpeedErrorSum;
        public double DistanceErrorSum;
        public int LastSummaryClock;
    }
}
