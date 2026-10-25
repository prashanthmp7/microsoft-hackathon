import { useEffect, useState } from 'react';
import { AgentActivity, api } from '../api';

interface Props {
  matchId: string;
}

const stageLabels: Record<string, string> = {
  Perception: 'Perception',
  Analysis: 'Analysis',
  Interpretation: 'Interpretation',
  Narrative: 'Narrative',
  Production: 'Production',
};

function clock(seconds: number | null) {
  if (seconds === null) return '';
  return `${Math.floor(seconds / 60) + 1}'`;
}

/** The agent team as a staged pipeline: each stage builds on what the earlier ones proposed. */
export function AgentTeamPanel({ matchId }: Props) {
  const [agents, setAgents] = useState<AgentActivity[]>([]);

  useEffect(() => {
    let stop = false;
    const load = async () => {
      try {
        const list = await api.agents(matchId);
        if (!stop) setAgents(list);
      } catch { /* transient */ }
    };
    void load();
    const timer = window.setInterval(load, 3000);
    return () => { stop = true; window.clearInterval(timer); };
  }, [matchId]);

  if (agents.length === 0) return null;

  const stages = [...new Set(agents.map(a => a.stage))];

  return (
    <section className="panel agent-team" aria-labelledby="agent-team-title">
      <h2 id="agent-team-title">Agent team</h2>
      <p className="hint">Each stage sees what the earlier stages proposed for the same event.</p>
      <ol className="agent-stages" aria-live="polite">
        {stages.map(stage => (
          <li key={stage} className="agent-stage">
            <span className="agent-stage-name">{stageLabels[stage] ?? stage}</span>
            <ul>
              {agents.filter(a => a.stage === stage).map(a => (
                <li key={a.agent} className="agent-row">
                  <span className="agent-name">{a.agent}</span>
                  <span className="agent-counts">
                    {a.published} shown · {a.held} held{a.errors > 0 ? ` · ${a.errors} errors` : ''}
                  </span>
                  {a.lastHeadline && <span className="agent-last">{clock(a.lastClock)} {a.lastHeadline}</span>}
                </li>
              ))}
            </ul>
          </li>
        ))}
      </ol>
    </section>
  );
}
