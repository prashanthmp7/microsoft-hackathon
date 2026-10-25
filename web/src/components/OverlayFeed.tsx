import { Club, Overlay } from '../api';

interface Props {
  overlays: Overlay[];
  home: Club;
  away: Club;
}

/** Personalised overlays as they arrive, newest first. Card weight follows the overlay template. */
export function OverlayFeed({ overlays, home, away }: Props) {
  const visible = overlays.filter(o => o.template !== 'prediction-card' || o.kind === 'PredictionResolved');

  if (visible.length === 0) {
    return (
      <section className="feed feed-empty" aria-live="polite">
        <p>Moments that matter will appear here as the match unfolds: goals, swings, shifts in pressure and the numbers behind them.</p>
      </section>
    );
  }

  return (
    <section className="feed" aria-live="polite" aria-label="Match moments">
      {visible.map(o => {
        const club = o.team === 'Home' ? home : o.team === 'Away' ? away : null;
        return (
          <article
            key={o.id}
            className={`moment moment-${o.template}${o.forYourClub ? ' is-your-club' : ''}${o.forYourPlayer ? ' is-your-player' : ''}`}
            style={club ? ({ '--team': club.primaryColor } as React.CSSProperties) : undefined}
          >
            <span className="moment-minute">{o.minute}'</span>
            <div className="moment-body">
              <h3>{o.title}</h3>
              <p>{o.text}</p>
              {(o.forYourClub || o.forYourPlayer || o.translated || o.tags.cause) && (
                <p className="moment-tags">
                  {o.forYourClub && <span>Your club</span>}
                  {o.forYourPlayer && <span>Your player</span>}
                  {o.tags.cause && <span>Why: {o.tags.cause.replace('-', ' ')}</span>}
                  {o.translated && <span>Translated</span>}
                </p>
              )}
            </div>
          </article>
        );
      })}
    </section>
  );
}
