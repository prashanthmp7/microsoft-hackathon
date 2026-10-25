import { useEffect, useState } from 'react';
import { api, AuditEntry, Meta } from '../api';

interface Props {
  matchId: string;
  meta: Meta | null;
}

/** What the agents did and what the guardrails decided, straight from the audit log. */
export function GuardrailsPanel({ matchId, meta }: Props) {
  const [open, setOpen] = useState(false);
  const [entries, setEntries] = useState<AuditEntry[]>([]);

  useEffect(() => {
    if (!open) return;
    let stop = false;
    const load = async () => {
      try {
        const list = await api.audit(matchId);
        if (!stop) setEntries(list);
      } catch { /* transient */ }
    };
    void load();
    const timer = window.setInterval(load, 4000);
    return () => { stop = true; window.clearInterval(timer); };
  }, [open, matchId]);

  const rejected = entries.filter(e => e.action.includes('rejected') || e.action.includes('violation')).length;

  return (
    <section className="panel guardrails">
      <button type="button" className="disclosure" aria-expanded={open} onClick={() => setOpen(o => !o)}>
        How this was made
      </button>
      {open && (
        <div className="guardrails-body">
          <ul className="principles">
            <li>Agents read match data through read-only views. They have no path to change or delete it.</li>
            <li>Every number on screen is checked against verified facts before it is shown.</li>
            <li>Language models only rephrase facts; anything that fails the checks falls back to the template.</li>
            <li>Free to play: betting language is blocked.</li>
          </ul>
          {meta && (
            <p className="hint">
              Agents: {meta.agents.join(', ')}. Model priors: {meta.calibration.isDefault ? 'default league averages' : `calibrated from ${meta.calibration.matchCount} historical matches`}.
              {' '}Language model {meta.services.languageModel ? 'on' : 'off (templates only)'}, translator {meta.services.translator ? 'on' : 'off'}, content safety {meta.services.contentSafety ? 'on' : 'off'}.
            </p>
          )}
          <p className="hint">{rejected === 0 ? 'No guardrail rejections in this match.' : `${rejected} generated texts rejected and replaced with templates.`}</p>
          <ol className="audit">
            {entries.slice(0, 30).map((e, i) => (
              <li key={`${e.at}-${i}`}>
                <span className="audit-actor">{e.actor}</span>
                <span className="audit-action">{e.action}</span>
                <span className="audit-detail">{e.detail}</span>
              </li>
            ))}
          </ol>
        </div>
      )}
    </section>
  );
}
