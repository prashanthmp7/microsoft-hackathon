import { useEffect, useState } from 'react';
import { api, Club, Overlay, Profile } from '../api';

interface Props {
  matchId: string;
  profile: Profile;
  phase: string;
  home: Club;
  away: Club;
}

/** The top moments so far, in match order. Shown at half-time and full time. */
export function HighlightsReel({ matchId, profile, phase, home, away }: Props) {
  const [items, setItems] = useState<Overlay[] | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let stop = false;
    setFailed(false);
    api.highlights(matchId, profile)
      .then(h => { if (!stop) setItems(h); })
      .catch(() => { if (!stop) setFailed(true); });
    return () => { stop = true; };
  }, [matchId, profile, phase]);

  const title = phase === 'FullTime' ? 'Match highlights' : 'First-half highlights';

  return (
    <section className="panel highlights" aria-labelledby="highlights-title">
      <h2 id="highlights-title">{title}</h2>
      {failed && <p className="hint">Highlights aren't available right now.</p>}
      {!failed && items === null && <p className="hint">Loading highlights…</p>}
      {!failed && items?.length === 0 && <p className="hint">No standout moments yet.</p>}
      {items && items.length > 0 && (
        <ol className="highlights-list">
          {items.map(o => {
            const club = o.team === 'Home' ? home : o.team === 'Away' ? away : null;
            return (
              <li
                key={o.id}
                className={`highlight${o.forYourClub ? ' is-your-club' : ''}${o.forYourPlayer ? ' is-your-player' : ''}`}
                style={club ? ({ '--team': club.primaryColor } as React.CSSProperties) : undefined}
              >
                <span className="moment-minute">{o.minute}'</span>
                <div>
                  <h3>{o.title}</h3>
                  <p>{o.text}</p>
                </div>
              </li>
            );
          })}
        </ol>
      )}
    </section>
  );
}
