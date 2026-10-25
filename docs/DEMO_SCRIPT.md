# Demo video script (target 1:55, hard limit under 2:00)

Rules: show the working app (not mock-ups), no copyrighted music or third-party logos, all data synthetic. Record at 1920x1080, browser zoom 110%, hide bookmarks and notifications. Voice-over is optional; on-screen captions are enough, but voice-over helps judges.

## Prep (before recording)

- Deployed URL (or `dotnet run --project src/SecondHalfMind.Api` + `npm run dev`), with the language model, Translator and Content Safety configured so `/api/meta` reports `true`.
- Pre-record **two** matches so you never wait on screen:
  - Match A started at **60x** speed shortly before recording (live footage: swings, tracked shots, prediction questions).
  - Match B already at **full time** (highlights reel, recap, model vs pundit result).
- Pick a match where a goal and a swing explanation land early; note seeds that work.
- Tabs open in order: lobby, Match A (casual), Match A (analyst, second window for split screen), overlay view, `/api/matches/{A}/overlays?mode=analyst&language=en`, Match B, `/api/matches/{B}/highlights`, `/api/matches/{A}/tracking/{seq}`.
- Architecture diagram as a single slide (event stream, agent team, supervisor, guard, sink, overlays/personalisation; Azure services labelled).

## Shot list

| # | Time | Screen | Caption / voice-over |
|---|---|---|---|
| 1 | 0:00-0:10 | Opener from the pitch video (title, "See the game before it turns.") | "Commentary tells you what happened. Second Half Mind tells you why, for every fan." |
| 2 | 0:10-0:25 | Architecture slide | "Synthetic events stream in through Event Hubs. A team of agents (stats, vision, explainer, referee, narrator) interprets them; a supervisor paces them; a guard checks every number; Microsoft Foundry writes the big moments." |
| 3 | 0:25-0:45 | Match A, casual mode: scoreboard, swing chart moving, a key moment card with "Why: pressure" | "Live: win probability moves with every event, and when it swings, the explainer says why." |
| 4 | 0:45-1:00 | Split screen, casual vs analyst on the same moment; point at the analyst-only tracked shot card ("Tracked shot: N km/h, measured from N frames") | "Same intelligence, two experiences. Analysts get pass difficulty, xG, and speeds measured automatically from ball-tracking frames." |
| 5 | 1:00-1:12 | Profile bar: pick favourite club, then a focus player; the player's card appears highlighted | "Follow your club or one player all match." |
| 6 | 1:12-1:22 | Switch language to Spanish then Japanese; cards re-render translated | "Eleven languages through Azure AI Translator." |
| 7 | 1:22-1:35 | Prediction panel: answer a micro question, leaderboard with fan vs model vs AI pundit | "Fans predict against a Poisson model and an AI pundit. Free to play, every result explained." |
| 8 | 1:35-1:47 | Overlay view (transparent lower-third), then flick to the overlay JSON tab | "For studios: timed, machine-readable overlays a graphics engine can render." |
| 9 | 1:47-1:55 | Match B at full time: highlights reel and recap; flash `/highlights` JSON | "At full time: an automatic highlights reel and recap, ready for studio or streaming." |
| 10 | 1:55-1:58 | End card: name, GitHub URL, "Built on Microsoft Foundry, Azure AI Services, Event Hubs, Fabric, GitHub" | (silence) |

## Wording to keep honest

- "Auto-eventing from synthetic ball-tracking frames" (not "from video").
- "Agents orchestrated in C#; language generation on Microsoft Foundry."
- Only show Fabric if the Eventhouse is actually receiving events; otherwise drop it from the end card.
- Only say "Built with GitHub Copilot" if true.

## After recording

- Check length is under 2:00 in the upload, not just the editor.
- Upload to YouTube as Public or Unlisted (public URL), title "Second Half Mind: Inside the Game hackathon demo".
- Put the URL in the README and on the project page.
