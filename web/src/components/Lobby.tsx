import { useEffect, useState } from 'react';
import { api, ApiError, Club, MatchSummary, Meta } from '../api';

interface Props {
  meta: Meta | null;
}

const SPEEDS = [10, 20, 40];

export function Lobby({ meta }: Props) {
  const [matches, setMatches] = useState<MatchSummary[]>([]);
  const [clubs, setClubs] = useState<Club[]>([]);
  const [home, setHome] = useState('');
  const [away, setAway] = useState('');
  const [speed, setSpeed] = useState(20);
  const [operatorKey, setOperatorKey] = useState<string | null>(null);
  const [needsKey, setNeedsKey] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  const refresh = () => api.matches().then(setMatches).catch(() => undefined);

  useEffect(() => {
    void refresh();
    api.clubs().then(c => { setClubs(c); setHome(c[0]?.id ?? ''); setAway(c[1]?.id ?? ''); }).catch(() => undefined);
    const timer = window.setInterval(refresh, 3000);
    return () => window.clearInterval(timer);
  }, []);

  async function operate(action: () => Promise<unknown>, done: string) {
    try {
      await action();
      setMessage(done);
      setNeedsKey(false);
      await refresh();
    } catch (e) {
      const text = e instanceof ApiError ? e.message : 'Something went wrong.';
      if (/operator key/i.test(text)) setNeedsKey(true);
      setMessage(text);
    }
  }

  return (
    <main className="lobby">
      <header className="lobby-head">
        <h1>Second Half Mind</h1>
        <p className="tagline">See the game before it turns.</p>
        <p className="lede">Pick a match. Call the score against our model and AI pundit, then watch every swing explained as it happens, at your depth and in your language.</p>
      </header>

      <section aria-labelledby="fixtures-title">
        <h2 id="fixtures-title">Matches</h2>
        <ul className="fixtures">
          {matches.map(m => (
            <li key={m.id} className={`fixture fixture-${m.status.toLowerCase()}`}>
              <a href={`#/match/${m.id}`} className="fixture-link">
                <span className="fixture-team"><i style={{ background: m.home.primaryColor }} />{m.home.name}</span>
                <span className="fixture-score">{m.status === 'Scheduled' ? 'v' : `${m.homeGoals}–${m.awayGoals}`}</span>
                <span className="fixture-team fixture-away">{m.away.name}<i style={{ background: m.away.primaryColor }} /></span>
                <span className="fixture-status">{m.status === 'Live' ? `${m.minute}'` : m.status === 'Finished' ? 'Full time' : 'Not started'}</span>
              </a>
              {m.status === 'Scheduled' && (
                <button type="button" onClick={() => operate(() => api.start(m.id, speed, operatorKey), `Kicked off ${m.home.shortName} v ${m.away.shortName}.`)}>
                  Kick off
                </button>
              )}
            </li>
          ))}
        </ul>
      </section>

      <section className="operator" aria-labelledby="operator-title">
        <h2 id="operator-title">Match control</h2>
        <form className="operator-form" onSubmit={e => { e.preventDefault(); void operate(() => api.createMatch(home, away, operatorKey), 'Fixture added.'); }}>
          <label className="field-inline">
            <span>Home</span>
            <select value={home} onChange={e => setHome(e.target.value)}>{clubs.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}</select>
          </label>
          <label className="field-inline">
            <span>Away</span>
            <select value={away} onChange={e => setAway(e.target.value)}>{clubs.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}</select>
          </label>
          <button type="submit">Add fixture</button>
          <label className="field-inline">
            <span>Replay speed</span>
            <select value={speed} onChange={e => setSpeed(Number(e.target.value))}>
              {SPEEDS.map(s => <option key={s} value={s}>{s}x ({Math.round(90 / s * 10) / 10} min match)</option>)}
            </select>
          </label>
          {needsKey && (
            <label className="field-inline">
              <span>Operator key</span>
              <input type="password" autoComplete="off" onChange={e => setOperatorKey(e.target.value || null)} />
            </label>
          )}
        </form>
        {message && <p className="notice" role="status">{message}</p>}
      </section>

      <footer className="lobby-foot">
        <p>
          All clubs, players and matches are synthetic. {meta?.calibration.isDefault === false
            ? `League behaviour is calibrated from ${meta.calibration.matchCount} real historical results, reduced to anonymous averages.`
            : 'League behaviour uses default top-flight averages until calibration is run.'}
          {' '}Free to play: no money, no odds.
        </p>
      </footer>
    </main>
  );
}
