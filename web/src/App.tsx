import { useEffect, useState } from 'react';
import { api, Meta, Profile } from './api';
import { Lobby } from './components/Lobby';
import { MatchCentre } from './components/MatchCentre';
import { BroadcastOverlay } from './components/BroadcastOverlay';

type Route =
  | { view: 'lobby' }
  | { view: 'match'; id: string }
  | { view: 'overlay'; id: string; profile: Profile };

function parse(hash: string): Route {
  const [path, query = ''] = hash.replace(/^#/, '').split('?');
  const parts = path.split('/').filter(Boolean);
  if (parts[0] === 'match' && parts[1]) return { view: 'match', id: decodeURIComponent(parts[1]) };
  if (parts[0] === 'overlay' && parts[1]) {
    const q = new URLSearchParams(query);
    return {
      view: 'overlay',
      id: decodeURIComponent(parts[1]),
      profile: {
        mode: q.get('mode') === 'analyst' ? 'analyst' : 'casual',
        language: q.get('lang') ?? 'en',
        favoriteClubId: q.get('club'),
        focusPlayerId: q.get('player'),
        focusMetric: q.get('metric'),
      },
    };
  }
  return { view: 'lobby' };
}

export function App() {
  const [route, setRoute] = useState<Route>(() => parse(window.location.hash));
  const [meta, setMeta] = useState<Meta | null>(null);

  useEffect(() => {
    const onHash = () => { setRoute(parse(window.location.hash)); window.scrollTo(0, 0); };
    window.addEventListener('hashchange', onHash);
    api.meta().then(setMeta).catch(() => undefined);
    return () => window.removeEventListener('hashchange', onHash);
  }, []);

  switch (route.view) {
    case 'match':
      return <MatchCentre key={route.id} matchId={route.id} meta={meta} />;
    case 'overlay':
      return <BroadcastOverlay key={route.id} matchId={route.id} profile={route.profile} />;
    default:
      return <Lobby meta={meta} />;
  }
}
