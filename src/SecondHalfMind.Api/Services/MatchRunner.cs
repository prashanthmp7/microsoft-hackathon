using System.Threading.Channels;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Api.Services;

public sealed record StartRequest(string MatchId, double Speed);

/// <summary>
/// Ingest stage. Reads a match from the configured <see cref="IMatchFeed"/> (synthetic today, a
/// broadcaster adapter later) and writes it into the append-only stores, mirroring each event to
/// Event Hubs when configured. The feed never gets write access; this runner does the writing.
/// </summary>
public sealed class MatchRunner : BackgroundService
{
    public const double MinSpeed = 1;
    public const double MaxSpeed = 60;

    private readonly Channel<StartRequest> _requests = Channel.CreateBounded<StartRequest>(16);
    private readonly IMatchReadModel _matches;
    private readonly IMatchRegistryWriter _registry;
    private readonly IMatchEventWriter _writer;
    private readonly ITrackingWriter _tracking;
    private readonly IMatchFeed _feed;
    private readonly EventHubsMirror _mirror;
    private readonly ILogger<MatchRunner> _logger;

    public MatchRunner(IMatchReadModel matches, IMatchRegistryWriter registry, IMatchEventWriter writer, ITrackingWriter tracking, IMatchFeed feed, EventHubsMirror mirror, ILogger<MatchRunner> logger)
    {
        _matches = matches;
        _registry = registry;
        _writer = writer;
        _tracking = tracking;
        _feed = feed;
        _mirror = mirror;
        _logger = logger;
    }

    public bool TryStart(string matchId, double speed) =>
        _matches.GetStatus(matchId) == MatchStatus.Scheduled
        && _requests.Writer.TryWrite(new StartRequest(matchId, Math.Clamp(speed, MinSpeed, MaxSpeed)));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in _requests.Reader.ReadAllAsync(stoppingToken))
        {
            // Matches run concurrently; each replay is its own task.
            _ = Task.Run(() => ReplayAsync(request, stoppingToken), stoppingToken);
        }
    }

    private async Task ReplayAsync(StartRequest request, CancellationToken ct)
    {
        var match = _matches.GetMatch(request.MatchId);
        if (match is null || _matches.GetStatus(match.Id) != MatchStatus.Scheduled)
        {
            return;
        }

        _registry.MarkLive(match.Id);
        _logger.LogInformation("Starting {MatchId} from {Feed} feed at {Speed}x", match.Id, _feed.Name, request.Speed);

        try
        {
            await foreach (var item in _feed.StreamAsync(match, request.Speed, ct))
            {
                // Tracking frames land first so auto-eventing can read them when the event arrives.
                if (item.Track is { } track)
                {
                    _tracking.Append(track);
                }

                _writer.Append(item.Event);
                _mirror.Enqueue(item.Event);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Replay of {MatchId} failed", match.Id);
        }

        _registry.MarkFinished(match.Id);
    }
}
