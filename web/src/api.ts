// Types mirror the API's JSON (camelCase, enums as strings).

export type Side = 'Home' | 'Away';
export type Mode = 'casual' | 'analyst';

export interface Club { id: string; name: string; shortName: string; primaryColor: string; secondaryColor: string }
export interface PlayerView { id: string; name: string; shirtNumber: number; position: string }
export interface PlayerLive { playerId: string; team: Side; onPitch: boolean; passes: number; passesCompleted: number; shots: number; goals: number; tackles: number; distanceKm: number; topSpeedKmh: number }

export interface TeamStats {
  goals: number; shots: number; shotsOnTarget: number; expectedGoals: number; passesAttempted: number; passesCompleted: number;
  tackles: number; interceptions: number; corners: number; fouls: number; yellowCards: number; redCards: number;
  pressureEvents: number; substitutions: number; possessionSeconds: number; maxShotSpeedKmh: number; passAccuracy: number;
}

export interface WinPoint { minute: number; home: number; draw: number; away: number }
export interface GoalView { minute: number; team: Side; playerId: string; playerName: string }

export interface MatchSummary { id: string; home: Club; away: Club; status: string; minute: number; homeGoals: number; awayGoals: number }

export interface MatchView {
  id: string; status: string; phase: string; minute: number; clockSeconds: number; home: Club; away: Club;
  homeStats: TeamStats; awayStats: TeamStats; homePossession: number; goals: GoalView[]; winProbability: WinPoint | null;
}

export interface ScorePrediction { outcome: 'Home' | 'Draw' | 'Away'; homeGoals: number; awayGoals: number }
export interface Factor { name: string; description: string; value: number }
export interface PreMatchCall { source: string; prediction: ScorePrediction; probabilities: { home: number; draw: number; away: number } | null; factors: Factor[]; rationale: string }
export interface Storyline { id: string; title: string; description: string; watchFor: string }
export interface Briefing { matchId: string; model: PreMatchCall; pundit: PreMatchCall; storylines: Storyline[]; expectedHomeGoals: number; expectedAwayGoals: number }

export interface Overlay {
  id: string; matchId: string; insightId: string; clockSeconds: number; minute: number; template: string; durationMs: number;
  priority: number; mode: string; language: string; translated: boolean; title: string; text: string; team: Side | null;
  playerId: string | null; metric: string | null; kind: string; data: Record<string, number>; tags: Record<string, string>;
  forYourClub: boolean; forYourPlayer: boolean;
}

export interface MicroQuestion { id: string; matchId: string; type: string; text: string; openedAtClock: number; windowStartClock: number; windowEndClock: number; modelProbabilityYes: number; punditSaysYes: boolean }
export interface LeaderboardEntry { fanId: string; nickname: string; points: number; correct: number; answered: number }
export interface SourceScore { source: string; points: number; correct: number; answered: number }
export interface Leaderboard { matchId: string; fans: LeaderboardEntry[]; model: SourceScore; pundit: SourceScore }
export interface AuditEntry { at: string; actor: string; action: string; matchId: string | null; outcome: string; detail: string }
export interface AgentActivity { agent: string; stage: string; proposed: number; published: number; held: number; errors: number; lastHeadline: string | null; lastClock: number | null }

export interface Meta {
  calibration: { source: string; isDefault: boolean; seasons: string[]; matchCount: number; homeWinRate: number; drawRate: number; awayWinRate: number; meanHomeGoals: number; meanAwayGoals: number };
  agents: string[];
  languages: Record<string, string>;
  focusMetrics: string[];
  services: { languageModel: boolean; translator: boolean; contentSafety: boolean; eventHubsMirror: boolean };
}

export interface Profile { mode: Mode; language: string; favoriteClubId: string | null; focusPlayerId: string | null; focusMetric: string | null }

export class ApiError extends Error {}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...(init?.headers ?? {}) },
  });
  if (response.status === 204) return undefined as T;
  const body = await response.json().catch(() => null);
  if (!response.ok) {
    if (response.status === 401 && init?.headers && 'X-Fan-Token' in init.headers) throw new TokenRejected('Fan token rejected');
    throw new ApiError(body?.error ?? body?.detail ?? `Request failed (${response.status})`);
  }
  return body as T;
}

const operatorHeaders = (key: string | null): HeadersInit => (key ? { 'X-Operator-Key': key } : {});

export const api = {
  meta: () => request<Meta>('/api/meta'),
  matches: () => request<MatchSummary[]>('/api/matches'),
  clubs: () => request<Club[]>('/api/clubs'),
  match: (id: string) => request<MatchView>(`/api/matches/${encodeURIComponent(id)}`),
  briefing: (id: string) => request<Briefing>(`/api/matches/${encodeURIComponent(id)}/briefing`),
  winProbability: (id: string) => request<WinPoint[]>(`/api/matches/${encodeURIComponent(id)}/winprob`),
  squads: (id: string) => request<{ home: { player: PlayerView; live: PlayerLive | null }[]; away: { player: PlayerView; live: PlayerLive | null }[] }>(`/api/matches/${encodeURIComponent(id)}/squads`),
  overlays: (id: string, p: Profile) => {
    const q = new URLSearchParams({ mode: p.mode, language: p.language });
    if (p.favoriteClubId) q.set('club', p.favoriteClubId);
    if (p.focusPlayerId) q.set('player', p.focusPlayerId);
    if (p.focusMetric) q.set('metric', p.focusMetric);
    return request<Overlay[]>(`/api/matches/${encodeURIComponent(id)}/overlays?${q}`);
  },
  highlights: (id: string, p: Profile) => {
    const q = new URLSearchParams({ mode: p.mode, language: p.language });
    if (p.favoriteClubId) q.set('club', p.favoriteClubId);
    if (p.focusPlayerId) q.set('player', p.focusPlayerId);
    if (p.focusMetric) q.set('metric', p.focusMetric);
    return request<Overlay[]>(`/api/matches/${encodeURIComponent(id)}/highlights?${q}`);
  },
  question: (id: string) => request<MicroQuestion | undefined>(`/api/matches/${encodeURIComponent(id)}/question`),
  leaderboard: (id: string) => request<Leaderboard>(`/api/matches/${encodeURIComponent(id)}/leaderboard`),
  audit: (id: string) => request<AuditEntry[]>(`/api/audit?matchId=${encodeURIComponent(id)}&max=60`),
  agents: (id: string) => request<AgentActivity[]>(`/api/matches/${encodeURIComponent(id)}/agents`),
  pick: (id: string, body: { nickname: string; outcome: string; homeGoals: number; awayGoals: number }) =>
    asFan(me => request(`/api/matches/${encodeURIComponent(id)}/picks`, { method: 'POST', body: JSON.stringify({ ...body, fanId: me.fanId }), headers: { 'X-Fan-Token': me.token } })),
  answer: (id: string, body: { nickname: string; questionId: string; answerYes: boolean }) =>
    asFan(me => request(`/api/matches/${encodeURIComponent(id)}/answers`, { method: 'POST', body: JSON.stringify({ ...body, fanId: me.fanId }), headers: { 'X-Fan-Token': me.token } })),
  createMatch: (homeClubId: string, awayClubId: string, key: string | null) =>
    request<{ id: string }>('/api/ops/matches', { method: 'POST', body: JSON.stringify({ homeClubId, awayClubId }), headers: operatorHeaders(key) }),
  start: (id: string, speed: number, key: string | null) =>
    request(`/api/ops/matches/${encodeURIComponent(id)}/start`, { method: 'POST', body: JSON.stringify({ speed }), headers: operatorHeaders(key) }),
};

// Browser storage is a convenience only: it can be unavailable, so every access is guarded.
export const store = {
  get(key: string): string | null {
    try { return window.localStorage.getItem(key); } catch { return null; }
  },
  set(key: string, value: string) {
    try { window.localStorage.setItem(key, value); } catch { /* storage unavailable */ }
  },
};

/** The signed fan id, or an empty string until the server has issued one. */
export function fanId(): string {
  return store.get('shm.fanToken') ? store.get('shm.fanId') ?? '' : '';
}

let pendingIdentity: Promise<{ fanId: string; token: string }> | null = null;

class TokenRejected extends ApiError {}

/** Runs a fan write; if the server no longer accepts the stored token (key rotated), gets a new one and retries once. */
async function asFan<T>(call: (me: { fanId: string; token: string }) => Promise<T>): Promise<T> {
  try {
    return await call(await fanIdentity());
  } catch (err) {
    if (!(err instanceof TokenRejected)) throw err;
    store.set('shm.fanToken', '');
    return call(await fanIdentity());
  }
}

/** Server-issued identity: the token proves the fan id, so nobody can play as another fan. */
async function fanIdentity(): Promise<{ fanId: string; token: string }> {
  const id = store.get('shm.fanId');
  const token = store.get('shm.fanToken');
  if (id && token) return { fanId: id, token };
  pendingIdentity ??= request<{ fanId: string; token: string }>('/api/fans', { method: 'POST' }).then(me => {
    store.set('shm.fanId', me.fanId);
    store.set('shm.fanToken', me.token);
    return me;
  }).finally(() => { pendingIdentity = null; });
  return pendingIdentity;
}

export const pct = (p: number) => `${Math.round(p * 100)}%`;
