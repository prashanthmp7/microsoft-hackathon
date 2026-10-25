import { useEffect, useState } from 'react';
import { Overlay, Profile } from '../api';
import { useMatchStream } from '../useMatchStream';

const FRESH_SECONDS = 120;

interface Props {
  matchId: string;
  profile: Profile;
}

/**
 * Render stage for studios: a transparent page (e.g. a browser source in a vision mixer) that plays
 * the timed overlay instructions as lower-thirds, one at a time, for their stated duration.
 */
export function BroadcastOverlay({ matchId, profile }: Props) {
  const { match, overlays } = useMatchStream(matchId, profile);
  const [current, setCurrent] = useState<Overlay | null>(null);
  const [shown, setShown] = useState<Set<string>>(() => new Set());

  useEffect(() => {
    document.body.classList.add('is-broadcast');
    return () => document.body.classList.remove('is-broadcast');
  }, []);

  useEffect(() => {
    if (!match) return;
    // Live graphics only: play the newest unseen moment from the last two match minutes and drop
    // anything older, so a late-joining or briefly busy renderer never falls behind the match.
    // Must-show moments (goals, red cards, full time) interrupt whatever is on screen.
    const fresh = overlays.filter(o => !shown.has(o.id) && o.priority >= 3 && o.clockSeconds >= match.clockSeconds - FRESH_SECONDS)
      .filter(o => !current || (o.priority === 5 && o.priority > current.priority));
    if (fresh.length === 0) return;
    const next = fresh.reduce((best, o) => (o.priority > best.priority || (o.priority === best.priority && o.clockSeconds > best.clockSeconds) ? o : best));
    setCurrent(next);
    setShown(s => new Set([...s, ...overlays.filter(o => o.clockSeconds <= next.clockSeconds).map(o => o.id)]));
  }, [overlays, current, shown, match]);

  // Each graphic stays up for exactly its stated duration.
  useEffect(() => {
    if (!current) return;
    const timer = window.setTimeout(() => setCurrent(null), current.durationMs);
    return () => window.clearTimeout(timer);
  }, [current]);

  const club = current && match ? (current.team === 'Home' ? match.home : current.team === 'Away' ? match.away : null) : null;

  return (
    <main className="broadcast">
      {match && (
        <div className="bug">
          <span>{match.home.shortName}</span>
          <strong>{match.homeStats.goals}–{match.awayStats.goals}</strong>
          <span>{match.away.shortName}</span>
          <em>{match.phase === 'HalfTime' ? 'HT' : match.phase === 'FullTime' ? 'FT' : `${match.minute}'`}</em>
        </div>
      )}
      {current && (
        <div key={current.id} className={`lower-third lower-third-${current.template}`} style={{ '--team': club?.primaryColor ?? 'var(--floodlight)' } as React.CSSProperties}>
          <span className="lower-third-minute">{current.minute}'</span>
          <div>
            <p className="lower-third-title">{current.title}</p>
            <p className="lower-third-text">{current.text}</p>
          </div>
        </div>
      )}
    </main>
  );
}
