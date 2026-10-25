import { useEffect, useRef, useState } from 'react';
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { api, Leaderboard, MatchView, Overlay, Profile, WinPoint } from './api';

export interface MatchStream {
  match: MatchView | null;
  winPoints: WinPoint[];
  overlays: Overlay[];
  leaderboard: Leaderboard | null;
  connected: boolean;
  error: string | null;
}

const MAX_OVERLAYS = 120;

/** Subscribes to a match over SignalR with the viewer's profile; reloads history when the profile changes. */
export function useMatchStream(matchId: string, profile: Profile): MatchStream {
  const [match, setMatch] = useState<MatchView | null>(null);
  const [winPoints, setWinPoints] = useState<WinPoint[]>([]);
  const [overlays, setOverlays] = useState<Overlay[]>([]);
  const [leaderboard, setLeaderboard] = useState<Leaderboard | null>(null);
  const [connected, setConnected] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const profileKey = JSON.stringify(profile);
  const profileRef = useRef(profile);
  profileRef.current = profile;

  useEffect(() => {
    let cancelled = false;
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/match')
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on('snapshot', (m: MatchView) => !cancelled && setMatch(m));
    connection.on('winprob', (p: WinPoint) => !cancelled && setWinPoints(prev => prev.some(x => x.minute === p.minute) ? prev.map(x => x.minute === p.minute ? p : x) : [...prev, p]));
    connection.on('overlay', (o: Overlay) => !cancelled && setOverlays(prev => prev.some(x => x.id === o.id) ? prev : [o, ...prev].slice(0, MAX_OVERLAYS)));
    connection.on('leaderboard', (l: Leaderboard) => !cancelled && setLeaderboard(l));
    connection.onreconnected(() => join());

    async function join() {
      const result: string | null = await connection.invoke('Join', matchId, profileRef.current);
      if (result) setError(result);
    }

    (async () => {
      try {
        const [m, wp, history, board] = await Promise.all([
          api.match(matchId), api.winProbability(matchId), api.overlays(matchId, profileRef.current), api.leaderboard(matchId),
        ]);
        if (cancelled) return;
        setMatch(m);
        setWinPoints(wp);
        setOverlays([...history].reverse().slice(0, MAX_OVERLAYS));
        setLeaderboard(board);
        await connection.start();
        if (cancelled) return;
        await join();
        setConnected(connection.state === HubConnectionState.Connected);
        setError(null);
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Connection failed.');
      }
    })();

    return () => {
      cancelled = true;
      setConnected(false);
      void connection.stop();
    };
  }, [matchId, profileKey]);

  return { match, winPoints, overlays, leaderboard, connected, error };
}
