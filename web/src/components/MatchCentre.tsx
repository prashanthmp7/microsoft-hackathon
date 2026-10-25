import { useEffect, useState } from 'react';
import { api, Briefing, Meta, PlayerLive, PlayerView, Profile, pct, store } from '../api';
import { useMatchStream } from '../useMatchStream';
import { SwingChart } from './SwingChart';
import { OverlayFeed } from './OverlayFeed';
import { HighlightsReel } from './HighlightsReel';
import { PredictionPanel } from './PredictionPanel';
import { StatsPanel } from './StatsPanel';
import { ProfileBar } from './ProfileBar';
import { GuardrailsPanel } from './GuardrailsPanel';
import { AgentTeamPanel } from './AgentTeamPanel';

interface Props {
  matchId: string;
  meta: Meta | null;
}

type Squads = { home: { player: PlayerView; live: PlayerLive | null }[]; away: { player: PlayerView; live: PlayerLive | null }[] };

function loadProfile(): Profile {
  try {
    const saved = store.get('shm.profile');
    if (saved) return { mode: 'casual', language: 'en', favoriteClubId: null, focusPlayerId: null, focusMetric: null, ...JSON.parse(saved) };
  } catch { /* ignore malformed */ }
  return { mode: 'casual', language: 'en', favoriteClubId: null, focusPlayerId: null, focusMetric: null };
}

export function MatchCentre({ matchId, meta }: Props) {
  const [profile, setProfile] = useState<Profile>(() => {
    const p = loadProfile();
    // Club and player choices only make sense within one match.
    return { ...p, favoriteClubId: null, focusPlayerId: null };
  });
  const { match, winPoints, overlays, leaderboard, connected, error } = useMatchStream(matchId, profile);
  const [briefing, setBriefing] = useState<Briefing | null>(null);
  const [squads, setSquads] = useState<Squads | null>(null);

  useEffect(() => { api.briefing(matchId).then(setBriefing).catch(() => setBriefing(null)); }, [matchId]);

  useEffect(() => {
    let stop = false;
    const load = () => api.squads(matchId).then(s => !stop && setSquads(s)).catch(() => undefined);
    void load();
    const timer = window.setInterval(load, profile.focusPlayerId ? 4000 : 30000);
    return () => { stop = true; window.clearInterval(timer); };
  }, [matchId, profile.focusPlayerId]);

  function changeProfile(p: Profile) {
    setProfile(p);
    store.set('shm.profile', JSON.stringify({ mode: p.mode, language: p.language, focusMetric: p.focusMetric }));
  }

  if (!match) {
    return <main className="match"><p className="loading">{error ?? 'Loading match…'}</p></main>;
  }

  const wp = match.winProbability;
  const focus = profile.focusPlayerId && squads
    ? [...squads.home, ...squads.away].find(s => s.player.id === profile.focusPlayerId) ?? null
    : null;
  const clock = match.phase === 'PreMatch' ? 'Pre-match' : match.phase === 'HalfTime' ? 'Half-time' : match.phase === 'FullTime' ? 'Full time' : `${match.minute}'`;

  return (
    <main className="match" style={{ '--home': match.home.primaryColor, '--away': match.away.primaryColor } as React.CSSProperties}>
      <header className="scoreboard">
        <a className="back" href="#/">All matches</a>
        <div className="score">
          <span className="team team-home"><i style={{ background: match.home.primaryColor }} />{match.home.name}</span>
          <span className="goals">{match.homeStats.goals}<span aria-hidden="true">–</span>{match.awayStats.goals}</span>
          <span className="team team-away">{match.away.name}<i style={{ background: match.away.primaryColor }} /></span>
        </div>
        <p className="clock" aria-live="polite">
          {clock}
          {wp && match.phase !== 'FullTime' && <span className="clock-wp"> {match.home.shortName} {pct(wp.home)}, draw {pct(wp.draw)}, {match.away.shortName} {pct(wp.away)}</span>}
        </p>
        <p className={`link-state ${connected ? 'is-live' : ''}`}>{connected ? 'Live' : 'Connecting'}</p>
      </header>

      <SwingChart points={winPoints} goals={match.goals} home={match.home} away={match.away} minute={match.minute} />

      <ProfileBar
        profile={profile}
        onChange={changeProfile}
        meta={meta}
        home={match.home}
        away={match.away}
        squads={{ home: squads?.home.map(s => s.player) ?? [], away: squads?.away.map(s => s.player) ?? [] }}
      />

      <div className="match-grid">
        <div className="match-main">
          {match.status === 'Scheduled' && briefing && (
            <section className="panel briefing" aria-labelledby="briefing-title">
              <h2 id="briefing-title">What to watch</h2>
              <ol className="storylines">
                {briefing.storylines.map(s => (
                  <li key={s.id}>
                    <h3>{s.title}</h3>
                    <p>{s.description}</p>
                    <p className="watch-for">Watch for: {s.watchFor.charAt(0).toLowerCase() + s.watchFor.slice(1)}</p>
                  </li>
                ))}
              </ol>
            </section>
          )}
          {(match.phase === 'HalfTime' || match.phase === 'FullTime') && (
            <HighlightsReel matchId={matchId} profile={profile} phase={match.phase} home={match.home} away={match.away} />
          )}
          <OverlayFeed overlays={overlays} home={match.home} away={match.away} />
        </div>
        <aside className="match-side">
          <PredictionPanel match={match} briefing={briefing} leaderboard={leaderboard} />
          <StatsPanel match={match} analyst={profile.mode === 'analyst'} focus={focus} />
          <AgentTeamPanel matchId={matchId} />
          <GuardrailsPanel matchId={matchId} meta={meta} />
          <p className="hint overlay-link">
            Broadcast graphics: <a href={`#/overlay/${matchId}?mode=${profile.mode}&lang=${profile.language}`}>open the overlay view</a>, or read the{' '}
            <a href={`/api/matches/${matchId}/overlays?mode=${profile.mode}&language=${profile.language}`}>overlay JSON</a>.
          </p>
        </aside>
      </div>
    </main>
  );
}
