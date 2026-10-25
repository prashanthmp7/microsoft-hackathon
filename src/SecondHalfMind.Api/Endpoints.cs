using System.Text;
using System.Text.Json;
using SecondHalfMind.Agents;
using SecondHalfMind.Agents.Briefing;
using SecondHalfMind.Agents.Orchestration;
using SecondHalfMind.Api.Security;
using SecondHalfMind.Api.Services;
using SecondHalfMind.Data;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Api;

public static class Endpoints
{
    public const string ReadPolicy = "read";
    public const string WritePolicy = "write";

    public static void MapSecondHalfMind(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireRateLimiting(ReadPolicy);

        api.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        api.MapGet("/meta", (ILeagueReadModel league, MatchOrchestrator orchestrator, EventHubsMirror mirror,
                Agents.Ai.IChatModel chat, Agents.Ai.ITranslator translator, Agents.Ai.IContentSafety safety) => Results.Ok(new
        {
            calibration = league.Calibration,
            agents = orchestrator.AgentNames.Append("personalization"),
            languages = PersonalizationAgent.SupportedLanguages,
            focusMetrics = PersonalizationAgent.FocusMetrics,
            services = new
            {
                languageModel = chat.IsConfigured,
                translator = translator.IsConfigured,
                contentSafety = safety.IsConfigured,
                eventHubsMirror = mirror.IsEnabled,
            },
        }));

        api.MapGet("/clubs", (ILeagueReadModel league) => league.Clubs.Select(Views.Club));

        api.MapGet("/clubs/{clubId}/squad", (string clubId, ILeagueReadModel league) =>
            league.GetClub(clubId) is { } club ? Results.Ok(club.Squad.Select(Views.Player)) : Results.NotFound());

        api.MapGet("/matches", (IMatchReadModel matches, ILeagueReadModel league) =>
            matches.ListMatches().Select(m =>
            {
                var s = matches.GetSnapshot(m.Id);
                return new MatchSummary(m.Id, Views.Club(league.GetClub(m.HomeClubId)!), Views.Club(league.GetClub(m.AwayClubId)!),
                    matches.GetStatus(m.Id).ToString(), s?.Minute ?? 0, s?.Home.Goals ?? 0, s?.Away.Goals ?? 0);
            }));

        api.MapGet("/matches/{matchId}", (string matchId, IMatchReadModel matches, ILeagueReadModel league, IInsightReadModel insights) =>
            matches.GetMatch(matchId) is { } info ? Results.Ok(Views.Match(info, matches, league, insights)) : Results.NotFound());

        api.MapGet("/matches/{matchId}/briefing", (string matchId, BriefingService briefings) =>
            briefings.Get(matchId) is { } b ? Results.Ok(b) : Results.NotFound());

        api.MapGet("/matches/{matchId}/winprob", (string matchId, IInsightReadModel insights) =>
            insights.GetInsights(matchId, 0, 1000).Where(i => i.Kind == InsightKind.WinProbability).Select(Views.WinPoint));

        api.MapGet("/matches/{matchId}/insights", (string matchId, int? skip, IInsightReadModel insights) =>
            insights.GetInsights(matchId, skip ?? 0, 500));

        // Agent team activity: what each stage proposed, published and held back for this match.
        api.MapGet("/matches/{matchId}/agents", (string matchId, IMatchReadModel matches, MatchOrchestrator orchestrator) =>
            matches.GetMatch(matchId) is null ? Results.NotFound() : Results.Ok(orchestrator.GetActivity(matchId)))
            .AddEndpointFilter<DiagnosticsFilter>();

        // Render stage: timed, machine-readable overlays for a given viewer profile.
        api.MapGet("/matches/{matchId}/overlays", async (
            string matchId, string? mode, string? language, string? club, string? player, string? metric, int? afterClock,
            IMatchReadModel matches, ILeagueReadModel league, IInsightReadModel insights, PersonalizationAgent personalization, CancellationToken ct) =>
        {
            if (matches.GetMatch(matchId) is not { } info)
            {
                return Results.NotFound();
            }

            if (!Validation.TryProfile(new ProfileDto(mode, language, club, player, metric), info, league, out var profile, out var error))
            {
                return Results.BadRequest(new { error });
            }

            var overlays = new List<Overlay>();
            foreach (var insight in insights.GetInsights(matchId, 0, 1000).Where(i => i.ClockSeconds > (afterClock ?? -1)))
            {
                if (await personalization.ShapeAsync(insight, profile, info, ct) is { } overlay)
                {
                    overlays.Add(overlay);
                }
            }

            return Results.Ok(overlays);
        });

        // Highlights reel: the top moments, personalised like overlays and returned in match order.
        api.MapGet("/matches/{matchId}/highlights", async (
            string matchId, string? mode, string? language, string? club, string? player, string? metric, int? max,
            IMatchReadModel matches, ILeagueReadModel league, IInsightReadModel insights, PersonalizationAgent personalization, CancellationToken ct) =>
        {
            if (matches.GetMatch(matchId) is not { } info)
            {
                return Results.NotFound();
            }

            if (!Validation.TryProfile(new ProfileDto(mode, language, club, player, metric), info, league, out var profile, out var error)
                || !Validation.TryHighlightCount(max, out var count, out error))
            {
                return Results.BadRequest(new { error });
            }

            // Prefer the producer agent's latest cut; before half-time there is none, so rank live.
            var published = insights.GetInsights(matchId, 0, 1000);
            var cut = published.LastOrDefault(i => i.Agent == "producer" && i.Metric == ProducerAgent.Metric);
            IEnumerable<Insight> candidates = HighlightReel.Rank(published);
            if (cut is not null && cut.Tags.TryGetValue(ProducerAgent.PicksTag, out var ids))
            {
                var byId = published.DistinctBy(i => i.Id).ToDictionary(i => i.Id, StringComparer.Ordinal);
                candidates = ids.Split(',').Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            }

            var picked = new List<(Insight Insight, Overlay Overlay)>();
            foreach (var insight in candidates)
            {
                if (picked.Count >= count)
                {
                    break;
                }

                if (await personalization.ShapeAsync(insight, profile, info, ct) is { } overlay)
                {
                    picked.Add((insight, overlay));
                }
            }

            var order = HighlightReel.Chronological(picked.Select(p => p.Insight));
            return Results.Ok(order.Select(i => picked.First(p => p.Insight.Id == i.Id).Overlay));
        });

        api.MapGet("/matches/{matchId}/squads", (string matchId, IMatchReadModel matches, ILeagueReadModel league) =>
        {
            if (matches.GetMatch(matchId) is not { } info)
            {
                return Results.NotFound();
            }

            var snapshot = matches.GetSnapshot(matchId);
            object Squad(string clubId) => league.GetClub(clubId)!.Squad.Select(p => new
            {
                player = Views.Player(p),
                live = snapshot?.Players.GetValueOrDefault(p.Id),
            });
            return Results.Ok(new { home = Squad(info.HomeClubId), away = Squad(info.AwayClubId) });
        });

        // Data innovation: the synthetic event stream as JSON Lines, ready for Fabric or notebooks.
        api.MapGet("/matches/{matchId}/events.jsonl", (string matchId, IMatchReadModel matches) =>
        {
            if (matches.GetMatch(matchId) is null)
            {
                return Results.NotFound();
            }

            var sb = new StringBuilder();
            foreach (var e in matches.GetEvents(matchId))
            {
                sb.AppendLine(JsonSerializer.Serialize(e, JsonDefaults.Options).ReplaceLineEndings(string.Empty));
            }

            return Results.Text(sb.ToString(), "application/x-ndjson");
        });

        // Auto-eventing input: the synthetic ball-tracking frames behind one pass or shot.
        api.MapGet("/matches/{matchId}/tracking/{sequence:long}", (string matchId, long sequence, ITrackingReadModel tracking) =>
            tracking.GetBallTrack(matchId, sequence) is { } track ? Results.Ok(track) : Results.NotFound());

        api.MapGet("/matches/{matchId}/leaderboard", (string matchId, IFanPredictionReadModel fans) => fans.GetLeaderboard(matchId));

        api.MapGet("/matches/{matchId}/question", (string matchId, IFanPredictionReadModel fans) =>
            fans.GetOpenQuestion(matchId) is { } q ? Results.Ok(q) : Results.NoContent());

        api.MapGet("/audit", (string? matchId, int? max, IAuditLog audit) => audit.Recent(matchId, max ?? 100))
            .AddEndpointFilter<DiagnosticsFilter>();

        // ---- Fan identity: the server issues a signed token; clients can't choose their own id. ----
        api.MapPost("/fans", (FanTokens tokens) =>
        {
            var (fanId, token) = tokens.Issue();
            return Results.Ok(new { fanId, token });
        }).RequireRateLimiting(WritePolicy);

        // ---- Fan writes: validated, rate-limited, token-checked, and limited to the prediction ledger. ----
        var fan = api.MapGroup("/matches/{matchId}").RequireRateLimiting(WritePolicy);

        fan.MapPost("/picks", (string matchId, PickRequest request, HttpRequest http, FanTokens tokens, IMatchReadModel matches, IFanPredictionWriter ledger) =>
        {
            if (!tokens.Verify(http.Headers[FanTokens.HeaderName], request.FanId))
            {
                return Results.Problem("Missing or invalid fan token.", statusCode: StatusCodes.Status401Unauthorized);
            }

            if (matches.GetMatch(matchId) is null)
            {
                return Results.NotFound();
            }

            if (!Validation.TryPick(request, matchId, out var pick, out var error))
            {
                return Results.BadRequest(new { error });
            }

            try
            {
                return Results.Ok(ledger.SubmitPick(pick));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        fan.MapPost("/answers", (string matchId, AnswerRequest request, HttpRequest http, FanTokens tokens, IFanPredictionWriter ledger) =>
        {
            if (!tokens.Verify(http.Headers[FanTokens.HeaderName], request.FanId))
            {
                return Results.Problem("Missing or invalid fan token.", statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!Validation.IsValidFanId(request.FanId) || !Validation.IsValidNickname(request.Nickname) || request.QuestionId.Length > 80)
            {
                return Results.BadRequest(new { error = "Invalid answer." });
            }

            return ledger.SubmitMicroAnswer(matchId, request.QuestionId, request.FanId, request.Nickname.Trim(), request.AnswerYes)
                ? Results.Ok(new { accepted = true })
                : Results.Conflict(new { error = "This question is closed or already answered." });
        });

        // ---- Operator controls: create and start matches. ----
        var ops = api.MapGroup("/ops").RequireRateLimiting(WritePolicy).AddEndpointFilter<OperatorKeyFilter>();

        ops.MapPost("/matches", (CreateMatchRequest request, IMatchRegistryWriter registry) =>
        {
            try
            {
                var seed = request.Seed ?? Random.Shared.Next(1, int.MaxValue);
                return Results.Ok(registry.Register(request.HomeClubId, request.AwayClubId, seed));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        ops.MapPost("/matches/{matchId}/start", (string matchId, StartMatchRequest request, MatchRunner runner) =>
            runner.TryStart(matchId, request.Speed ?? 20)
                ? Results.Accepted($"/api/matches/{matchId}")
                : Results.Conflict(new { error = "Match not found or already started." }));
    }
}
