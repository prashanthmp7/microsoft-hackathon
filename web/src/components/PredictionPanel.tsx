import { useEffect, useState } from 'react';
import { api, Briefing, Leaderboard, MatchView, MicroQuestion, fanId, pct, store } from '../api';

interface Props {
  match: MatchView;
  briefing: Briefing | null;
  leaderboard: Leaderboard | null;
}

/** Free-to-play predictions: the fan's pick against the model and the AI pundit, plus live questions. */
export function PredictionPanel({ match, briefing, leaderboard }: Props) {
  const [nickname, setNickname] = useState(() => store.get('shm.nickname') ?? '');
  const [homeGoals, setHomeGoals] = useState(1);
  const [awayGoals, setAwayGoals] = useState(1);
  const [message, setMessage] = useState<string | null>(null);
  const [question, setQuestion] = useState<MicroQuestion | null>(null);
  const [answered, setAnswered] = useState<Record<string, boolean>>({});
  const live = match.status === 'Live';
  const scheduled = match.status === 'Scheduled';
  const me = fanId();

  useEffect(() => {
    if (!live) return;
    let stop = false;
    const poll = async () => {
      try {
        const q = await api.question(match.id);
        if (!stop) setQuestion(q ?? null);
      } catch { /* transient */ }
    };
    void poll();
    const timer = window.setInterval(poll, 2500);
    return () => { stop = true; window.clearInterval(timer); };
  }, [live, match.id]);

  const outcome = homeGoals > awayGoals ? 'Home' : homeGoals === awayGoals ? 'Draw' : 'Away';

  async function submitPick(e: React.FormEvent) {
    e.preventDefault();
    try {
      store.set('shm.nickname', nickname);
      await api.pick(match.id, { nickname, outcome, homeGoals, awayGoals });
      setMessage(`Locked in: ${match.home.shortName} ${homeGoals}-${awayGoals} ${match.away.shortName}.`);
    } catch (err) {
      setMessage(err instanceof Error ? err.message : 'Could not save your pick.');
    }
  }

  async function answer(yes: boolean) {
    if (!question) return;
    try {
      await api.answer(match.id, { nickname: nickname || 'Fan', questionId: question.id, answerYes: yes });
      setAnswered(a => ({ ...a, [question.id]: yes }));
    } catch (err) {
      setMessage(err instanceof Error ? err.message : 'Too late for that one.');
    }
  }

  const canAnswer = question && match.clockSeconds < question.windowStartClock && !(question.id in answered);
  const myRow = leaderboard?.fans.find(f => f.fanId === me);

  return (
    <section className="panel predictions" aria-labelledby="predictions-title">
      <h2 id="predictions-title">Call it</h2>

      {scheduled && (
        <form className="pick" onSubmit={submitPick}>
          <p className="hint">Predict the score before kick-off. No money, just bragging rights.</p>
          <div className="pick-score">
            <label>
              <span>{match.home.shortName}</span>
              <input type="number" min={0} max={9} value={homeGoals} onChange={e => setHomeGoals(Number(e.target.value))} />
            </label>
            <span aria-hidden="true">–</span>
            <label>
              <span>{match.away.shortName}</span>
              <input type="number" min={0} max={9} value={awayGoals} onChange={e => setAwayGoals(Number(e.target.value))} />
            </label>
          </div>
          <label className="field">
            <span>Your name on the leaderboard</span>
            <input value={nickname} onChange={e => setNickname(e.target.value)} minLength={2} maxLength={20} pattern="[A-Za-z0-9 _\-]{2,20}" required />
          </label>
          <button type="submit" className="button-primary">Lock in my prediction</button>
        </form>
      )}

      {briefing && (
        <div className="rivals">
          <Rival label="The model" call={briefing.model.prediction} reason={briefing.model.rationale}
            probs={briefing.model.probabilities} home={match.home.shortName} away={match.away.shortName} />
          <Rival label="The AI pundit" call={briefing.pundit.prediction} reason={briefing.pundit.rationale}
            probs={null} home={match.home.shortName} away={match.away.shortName} />
        </div>
      )}

      {live && question && (
        <div className="question" aria-live="polite">
          <p className="question-text">{question.text}</p>
          <p className="hint">Model says {pct(question.modelProbabilityYes)} yes. Pundit says {question.punditSaysYes ? 'yes' : 'no'}.</p>
          {canAnswer ? (
            <div className="question-actions">
              <button type="button" onClick={() => answer(true)}>Yes</button>
              <button type="button" onClick={() => answer(false)}>No</button>
            </div>
          ) : (
            <p className="hint">{question.id in answered ? `You said ${answered[question.id] ? 'yes' : 'no'}. Watching…` : 'Answers closed. Watching…'}</p>
          )}
        </div>
      )}

      {message && <p className="notice" role="status">{message}</p>}

      {leaderboard && (leaderboard.model.answered > 0 || leaderboard.fans.length > 0) && (
        <table className="board">
          <caption>Who's calling it best</caption>
          <thead><tr><th scope="col">Who</th><th scope="col">Correct</th><th scope="col">Points</th></tr></thead>
          <tbody>
            {[
              { key: 'model', name: 'Model', correct: leaderboard.model.correct, answered: leaderboard.model.answered, points: leaderboard.model.points, mine: false },
              { key: 'pundit', name: 'AI pundit', correct: leaderboard.pundit.correct, answered: leaderboard.pundit.answered, points: leaderboard.pundit.points, mine: false },
              ...leaderboard.fans.slice(0, 8).map(f => ({ key: f.fanId, name: f.nickname, correct: f.correct, answered: f.answered, points: f.points, mine: f.fanId === me })),
            ].sort((a, b) => b.points - a.points).map(r => (
              <tr key={r.key} className={r.mine ? 'is-me' : undefined}>
                <th scope="row">{r.name}{r.mine ? ' (you)' : ''}</th>
                <td>{r.correct}/{r.answered}</td>
                <td>{r.points}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {myRow === undefined && !scheduled && <p className="hint">Answer a live question to join the leaderboard.</p>}
    </section>
  );
}

function Rival({ label, call, reason, probs, home, away }: {
  label: string; call: { homeGoals: number; awayGoals: number }; reason: string;
  probs: { home: number; draw: number; away: number } | null; home: string; away: string;
}) {
  return (
    <div className="rival">
      <p className="rival-label">{label}</p>
      <p className="rival-score">{home} {call.homeGoals}–{call.awayGoals} {away}</p>
      {probs && <p className="rival-probs">{home} {pct(probs.home)}, draw {pct(probs.draw)}, {away} {pct(probs.away)}</p>}
      <p className="rival-reason">{reason}</p>
    </div>
  );
}
