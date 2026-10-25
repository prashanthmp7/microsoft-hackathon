import { GoalView, WinPoint, Club } from '../api';

interface Props {
  points: WinPoint[];
  goals: GoalView[];
  home: Club;
  away: Club;
  minute: number;
}

const W = 900;
const H = 200;
const PAD_X = 28;
const MID = H / 2;

const x = (minute: number) => PAD_X + (Math.min(90, Math.max(0, minute)) / 90) * (W - 2 * PAD_X);
// Edge: home win chance minus away win chance, from -1 (away certain) to +1 (home certain).
const y = (p: WinPoint) => MID - (p.home - p.away) * (MID - 14);

/** The swing line: who the match belongs to, minute by minute. Above the line favours the home side. */
export function SwingChart({ points, goals, home, away, minute }: Props) {
  const sorted = [...points].sort((a, b) => a.minute - b.minute);
  const path = sorted.map((p, i) => `${i === 0 ? 'M' : 'L'}${x(p.minute).toFixed(1)},${y(p).toFixed(1)}`).join(' ');
  const area = sorted.length > 1
    ? `${path} L${x(sorted[sorted.length - 1].minute).toFixed(1)},${MID} L${x(sorted[0].minute).toFixed(1)},${MID} Z`
    : '';
  const last = sorted[sorted.length - 1];
  const summary = last
    ? `${home.name} ${Math.round(last.home * 100)}%, draw ${Math.round(last.draw * 100)}%, ${away.name} ${Math.round(last.away * 100)}% at minute ${last.minute}`
    : 'Win probability will appear at kick-off';

  return (
    <figure className="swing">
      <svg viewBox={`0 0 ${W} ${H}`} role="img" aria-label={summary} preserveAspectRatio="none">
        <defs>
          <clipPath id="above"><rect x="0" y="0" width={W} height={MID} /></clipPath>
          <clipPath id="below"><rect x="0" y={MID} width={W} height={MID} /></clipPath>
        </defs>
        <rect x={x(0)} y="6" width={x(90) - x(0)} height={H - 12} className="swing-pitch" />
        <line x1={x(45)} x2={x(45)} y1="6" y2={H - 6} className="swing-halfway" />
        <line x1={x(0)} x2={x(90)} y1={MID} y2={MID} className="swing-even" />
        {area && <path d={area} clipPath="url(#above)" fill={home.primaryColor} opacity="0.28" />}
        {area && <path d={area} clipPath="url(#below)" fill={away.primaryColor} opacity="0.28" />}
        {path && <path d={path} className="swing-line" />}
        {last && minute > 0 && <circle cx={x(last.minute)} cy={y(last)} r="6" className="swing-now" />}
        {goals.map(g => (
          <g key={`${g.minute}-${g.playerId}`} transform={`translate(${x(g.minute)}, ${g.team === 'Home' ? 14 : H - 14})`}>
            <circle r="7" fill={g.team === 'Home' ? home.primaryColor : away.primaryColor} stroke="var(--chalk)" strokeWidth="2" />
            <title>{`${g.minute}' ${g.playerName}`}</title>
          </g>
        ))}
      </svg>
      <figcaption>
        <span className="swing-axis" aria-hidden="true">
          <span>Kick-off</span>
          <span>Half-time</span>
          <span>Full time</span>
        </span>
        <span className="swing-legend">
          Win probability swing. Above the line, {home.name} are on top; below it, {away.name}.
        </span>
      </figcaption>
    </figure>
  );
}
