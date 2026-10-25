using SecondHalfMind.Domain;

namespace SecondHalfMind.Agents.Orchestration;

/// <summary>
/// Read views limited to one match. The orchestrator hands these to agents instead of the global
/// read models, so an agent working on match A can't read insights or fan data from match B.
/// Requests for another match return empty results; the attempt is visible to tests and reviews.
/// </summary>
internal sealed class MatchScopedInsights(IInsightReadModel inner, string matchId) : IInsightReadModel
{
    public IReadOnlyList<Insight> GetInsights(string requestedMatchId, int skip = 0, int max = 500) =>
        requestedMatchId == matchId ? inner.GetInsights(matchId, skip, max) : [];
}

/// <inheritdoc cref="MatchScopedInsights"/>
internal sealed class MatchScopedFans(IFanPredictionReadModel inner, string matchId) : IFanPredictionReadModel
{
    public CrowdView GetCrowd(string requestedMatchId) => inner.GetCrowd(Scope(requestedMatchId));

    public Leaderboard GetLeaderboard(string requestedMatchId) => inner.GetLeaderboard(Scope(requestedMatchId));

    public MicroQuestion? GetOpenQuestion(string requestedMatchId) =>
        requestedMatchId == matchId ? inner.GetOpenQuestion(matchId) : null;

    // Another match's id is replaced by an id that never exists, so callers get the empty view.
    private string Scope(string requestedMatchId) => requestedMatchId == matchId ? matchId : "\0out-of-scope";
}
