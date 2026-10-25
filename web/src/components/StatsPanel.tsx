import { MatchView, PlayerLive, PlayerView, pct } from '../api';

interface Props {
  match: MatchView;
  analyst: boolean;
  focus: { player: PlayerView; live: PlayerLive | null } | null;
}

/** Side-by-side numbers. Casual viewers get the essentials; analysts get the full sheet. */
export function StatsPanel({ match, analyst, focus }: Props) {
  const h = match.homeStats;
  const a = match.awayStats;
  const rows: [string, string, string, number][] = [
    ['Possession', pct(match.homePossession), pct(1 - match.homePossession), match.homePossession],
    ['Shots', `${h.shots}`, `${a.shots}`, share(h.shots, a.shots)],
    ['On target', `${h.shotsOnTarget}`, `${a.shotsOnTarget}`, share(h.shotsOnTarget, a.shotsOnTarget)],
    ['Expected goals', h.expectedGoals.toFixed(2), a.expectedGoals.toFixed(2), share(h.expectedGoals, a.expectedGoals)],
  ];
  if (analyst) {
    rows.push(
      ['Pass accuracy', pct(h.passAccuracy), pct(a.passAccuracy), share(h.passAccuracy, a.passAccuracy)],
      ['Pressing actions', `${h.pressureEvents}`, `${a.pressureEvents}`, share(h.pressureEvents, a.pressureEvents)],
      ['Ball wins', `${h.tackles + h.interceptions}`, `${a.tackles + a.interceptions}`, share(h.tackles + h.interceptions, a.tackles + a.interceptions)],
      ['Corners', `${h.corners}`, `${a.corners}`, share(h.corners, a.corners)],
      ['Fastest shot', speed(h.maxShotSpeedKmh), speed(a.maxShotSpeedKmh), share(h.maxShotSpeedKmh, a.maxShotSpeedKmh)],
      ['Cards', `${h.yellowCards}${h.redCards ? ` + ${h.redCards} red` : ''}`, `${a.yellowCards}${a.redCards ? ` + ${a.redCards} red` : ''}`, share(h.yellowCards, a.yellowCards)],
    );
  }

  return (
    <section className="panel stats" aria-labelledby="stats-title">
      <h2 id="stats-title">The numbers</h2>
      <ul className="stat-rows" style={{ '--home': match.home.primaryColor, '--away': match.away.primaryColor } as React.CSSProperties}>
        {rows.map(([label, hv, av, s]) => (
          <li className="stat-row" key={label} aria-label={`${label}: ${match.home.shortName} ${hv}, ${match.away.shortName} ${av}`}>
            <span className="stat-home" aria-hidden="true">{hv}</span>
            <span className="stat-label" aria-hidden="true">{label}</span>
            <span className="stat-away" aria-hidden="true">{av}</span>
            <span className="stat-bar" aria-hidden="true"><span style={{ width: `${Math.round(s * 100)}%` }} /></span>
          </li>
        ))}
      </ul>

      {focus && (
        <div className="focus-card">
          <h3>{focus.player.name} <span className="muted">#{focus.player.shirtNumber}, {focus.player.position.toLowerCase()}</span></h3>
          {focus.live ? (
            <dl className="focus-grid">
              <div><dt>Passes</dt><dd>{focus.live.passesCompleted}/{focus.live.passes}</dd></div>
              <div><dt>Shots</dt><dd>{focus.live.shots}</dd></div>
              <div><dt>Goals</dt><dd>{focus.live.goals}</dd></div>
              <div><dt>Tackles</dt><dd>{focus.live.tackles}</dd></div>
              <div><dt>Distance</dt><dd>{focus.live.distanceKm.toFixed(1)} km</dd></div>
              <div><dt>Top speed</dt><dd>{focus.live.topSpeedKmh ? `${focus.live.topSpeedKmh.toFixed(1)} km/h` : '–'}</dd></div>
            </dl>
          ) : <p className="hint">Live numbers start at kick-off.</p>}
          <p className="hint">{focus.live && !focus.live.onPitch ? 'On the bench or substituted.' : 'Following every action of this player.'}</p>
        </div>
      )}
    </section>
  );
}

const share = (h: number, a: number) => (h + a === 0 ? 0.5 : h / (h + a));
const speed = (kmh: number) => (kmh ? `${Math.round(kmh)} km/h` : '–');
