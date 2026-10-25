import { Club, Meta, PlayerView, Profile } from '../api';

interface Props {
  profile: Profile;
  onChange: (p: Profile) => void;
  meta: Meta | null;
  home: Club;
  away: Club;
  squads: { home: PlayerView[]; away: PlayerView[] };
}

const METRIC_LABELS: Record<string, string> = {
  passDifficulty: 'Pass difficulty',
  shotSpeed: 'Shot speed',
  speed: 'Sprint speed',
  distance: 'Distance covered',
  control: 'Control vs chaos',
  pressure: 'Pressing',
};

/** How this viewer wants the match: depth, language, club, player and the one metric they care about. */
export function ProfileBar({ profile, onChange, meta, home, away, squads }: Props) {
  const set = (patch: Partial<Profile>) => onChange({ ...profile, ...patch });

  return (
    <form className="profile" aria-label="Personalise this match" onSubmit={e => e.preventDefault()}>
      <fieldset className="mode-toggle">
        <legend className="visually-hidden">Viewing mode</legend>
        {(['casual', 'analyst'] as const).map(mode => (
          <label key={mode} className={profile.mode === mode ? 'is-on' : undefined}>
            <input type="radio" name="mode" value={mode} checked={profile.mode === mode} onChange={() => set({ mode })} />
            {mode === 'casual' ? 'Fan' : 'Analyst'}
          </label>
        ))}
      </fieldset>

      <label className="field-inline">
        <span>Language</span>
        <select value={profile.language} onChange={e => set({ language: e.target.value })}>
          {Object.entries(meta?.languages ?? { en: 'English' }).map(([code, name]) => (
            <option key={code} value={code}>{name}</option>
          ))}
        </select>
      </label>

      <label className="field-inline">
        <span>Your club</span>
        <select value={profile.favoriteClubId ?? ''} onChange={e => set({ favoriteClubId: e.target.value || null })}>
          <option value="">Neutral</option>
          <option value={home.id}>{home.name}</option>
          <option value={away.id}>{away.name}</option>
        </select>
      </label>

      <label className="field-inline">
        <span>Follow a player</span>
        <select value={profile.focusPlayerId ?? ''} onChange={e => set({ focusPlayerId: e.target.value || null })}>
          <option value="">Nobody</option>
          {[{ club: home, players: squads.home }, { club: away, players: squads.away }].map(({ club, players }) => (
            <optgroup key={club.id} label={club.name}>
              {players.map(p => <option key={p.id} value={p.id}>{p.shirtNumber}. {p.name}</option>)}
            </optgroup>
          ))}
        </select>
      </label>

      <label className="field-inline">
        <span>Always show</span>
        <select value={profile.focusMetric ?? ''} onChange={e => set({ focusMetric: e.target.value || null })}>
          <option value="">Just the big moments</option>
          {(meta?.focusMetrics ?? Object.keys(METRIC_LABELS)).map(m => <option key={m} value={m}>{METRIC_LABELS[m] ?? m}</option>)}
        </select>
      </label>

      {profile.language !== 'en' && meta && !meta.services.translator && (
        <p className="hint">Translation needs Azure AI Translator configured; showing English for now.</p>
      )}
    </form>
  );
}
