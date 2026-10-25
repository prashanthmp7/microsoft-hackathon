# Second Half Mind: full project context

This document is the complete handover of everything decided and built so far, so work can continue in GitHub Copilot (or with any developer). Read this first, then `README.md`, `docs/SECURITY.md` and `docs/DATA.md`.

Owner: Prashanth MP (Tech Lead / Solutions Architect, .NET + Azure).
Status as of 24 Oct 2026: working end to end locally; not yet pushed, calibrated with real data or deployed.

---

## 1. The hackathon

**Inside the Game: Developer Hackathon** (Microsoft × Premier League), challenge *"The Synthetic Match Insights Engine for Premier League Studio"*.

- **Submission deadline: 27 Oct 2026, 11:59 PM Pacific** (= 28 Oct, 12:29 PM IST). Top six notified by 10 Nov 2026.
- **Hard data rule:** projects must use synthetic, football-realistic data. No real Premier League match data is redistributed.
- **Required in the submission:**
  1. A newly built project meeting the requirements.
  2. A short pitch/description: what you built, which Microsoft/Azure technologies, what it does, what problem it solves.
  3. A demo video **under 2 minutes**, showing the project *functioning*, on YouTube, Vimeo, Facebook Video or Youku (public URL). **No third-party trademarks or copyrighted music** without permission.
  4. A **public** GitHub repository URL.
- **Judging (20% each):** Technological implementation (code quality, hero technologies, documentation); Agentic design and innovation (agent orchestration, multi-agent collaboration); Real-world impact (problem significance, production readiness); UX and presentation (intuitive UX, clear demo, balanced frontend and backend); Adherence to the category.
- **Hero technologies they named:** GitHub, GitHub Copilot, Microsoft Foundry, Azure AI Services, Microsoft Fabric, Azure database and analytics services, Azure cloud-native services.

### The challenge brief (what judges will check against)

One pipeline with five stages:

| Stage | Brief asks for | Status in this project |
|---|---|---|
| (a) Ingest | Synthetic events as they happen: passes, shots, tackles, possession changes, pressure, metadata | Done (simulator + runner + append-only store + Event Hubs mirror) |
| (b) Interpret | Meaningful stats, patterns and context | Done: pass distance/accuracy/difficulty, ball and shot speed, xG, distance and sprint threshold alerts, control and chaos indices, tempo |
| (c) Explain | Why a moment matters ("control vs chaos, tactical pressure changes, game rhythm patterns") | Done: win-probability swings with cause attribution, pressure shifts, control/chaos and tempo |
| (d) Render | On-screen insight; timed, machine-readable output for synchronised overlays | Done: overlay JSON with template, durationMs and priority; transparent broadcast view |
| (e) Personalise | Analyst vs casual as genuinely different experiences; favourite club, favourite player, player-focus mode, single-metric mode, language | Done: all of these, 11 languages (translation needs Azure AI Translator) |

Also asked: player identification with speed and distance alerts (done, as threshold alerts from simulated sprint and distance data; no video), pass quality (done), ball and shot speed (done), **auto-eventing (done on synthetic tracking frames, not video)**: `TrackingSynthesizer` renders 25 Hz ball positions with noise and dropped frames per pass and shot into an append-only `InMemoryTrackingStore`; the `vision` agent (`AutoEventingAgent` + `BallTrackAnalyzer`) recovers distance and speed from frames only and reports agreement with tagged data. A real video model would plug in behind the same `ITrackingReadModel`. Narratives and recaps (done, plus a highlights reel at `/api/matches/{id}/highlights`), data innovation (done, tracking frames at `/api/matches/{id}/tracking/{sequence}`), multi-language (done).

The brief's key line: *"a project that only generates metrics has no audience, and a solution that only personalises has nothing to personalise."* This project does both.

---

## 2. How the idea evolved

1. The original title was **"Prediction is addiction"**. Its premise: today's pre-shows and commentary have gaps. Pundits predict without evidence and are never held to account; commentary says *what* happened, not *why*; every fan gets one broadcast at one depth in one language; fans are passive.
2. I flagged that "addiction" plus "prediction" reads like a betting product, so it is framed as **free-to-play, no money, no odds**.
3. Renamed to **Second Half Mind**, tagline **"See the game before it turns."**
4. After reading the full brief, the project was **reframed**: the core is the five-stage explainable match intelligence pipeline. Prediction is the fan engagement hook on top, not the product.
5. Stack chosen by the owner: **.NET 8 / C#**, **React** (TypeScript), Microsoft/Azure stack. **Security and guardrails first: agents must never be able to modify or delete actual data.**
6. Data decision (owner chose **"Calibrate only"**): real results from the last three completed English top-flight seasons are used *only offline* to compute anonymous league averages that tune the synthetic league. The app, repo and demo stay fully synthetic. The owner originally asked to use real current-squad players. That was dropped to comply with the rules, so all players are fictional and "current squad" filtering doesn't apply.

### Submission text (current versions)

**Elevator pitch**
> Second Half Mind is a multi-agent match intelligence engine built on Microsoft Foundry, Microsoft Fabric Real-Time Intelligence and Azure Event Hubs. It ingests synthetic football events live, computes metrics such as pass difficulty, shot speed and a control-vs-chaos index, and explains why key moments matter. Output goes out as timed, machine-readable overlays for broadcasters and as personalised experiences for fans: analyst or casual mode, favourite club, Player Focus, and multiple languages via Azure AI Translator. Fans make predictions against an AI pundit and a model, and every result is explained. That solves a gap in today's coverage, where commentary reports what happened but rarely why, and every fan gets the same broadcast. Built with GitHub Copilot.

**Wording to fix before submitting:** agents are C# components orchestrated in code; the language model runs in Microsoft Foundry. Prefer *"a multi-agent team, with language generation on Microsoft Foundry"*. Only say "Built with GitHub Copilot" if that's true by submission time, and mention it in the README too. Remove anything from the description that isn't built (e.g. Cosmos DB is mentioned in older drafts but is not implemented).

**Keywords (short set):** Explainable AI, Win Probability, Real-Time Analytics, Fan Engagement, Match Recaps, Multilingual, AI Agents, Microsoft Foundry, Microsoft Fabric, GitHub Copilot.

**Assets already produced (outside the repo):**
- Banner PNGs (1600×900 and 1920×560 at 2x): dark pitch, lime accent, swing line crossing half-time.
- A 57-second motion-graphics pitch video (no audio). It is **not** product footage. Use at most 15–20 seconds of it as the opener, because the demo must show the working app.

### Planned 2-minute demo structure

| Time | Show |
|---|---|
| 0:00–0:15 | Opener (title + the gap) from the pitch video |
| 0:15–0:30 | Architecture: event stream → agent team → guard → overlays |
| 0:30–1:05 | Live match: events, stats, a swing explained, overlay JSON in the browser |
| 1:05–1:30 | Fan vs Analyst side by side; Player Focus; a language switch (needs Translator) |
| 1:30–1:45 | Micro prediction resolves; full-time "Who called it?" and recap |
| 1:45–2:00 | "How this was made" audit panel, repo, Azure stack, closing title |

---

## 3. Architecture

### Solution layout

```
SecondHalfMind.sln
Directory.Build.props          net8.0, nullable, TreatWarningsAsErrors=true, InvariantGlobalization
src/
  SecondHalfMind.Domain        Contracts only, no I/O, no dependencies
  SecondHalfMind.Simulation    Fictional league, history generator, event-level match simulator
  SecondHalfMind.Data          Platform-owned stores and write paths (agents must NOT reference this)
  SecondHalfMind.Agents        Agents, models, guardrails, Azure AI REST clients (references Domain only)
  SecondHalfMind.Api           ASP.NET Core host, SignalR, security, MatchRunner, EventHubsMirror
  SecondHalfMind.Tools         CLI: calibrate (real history to priors) and generate (synthetic datasets)
tests/SecondHalfMind.Tests     xUnit: architecture, guard, pipeline, models, validation
web/                           React 18 + TypeScript + Vite; builds into src/SecondHalfMind.Api/wwwroot
infra/main.bicep               Azure resources with least-privilege RBAC
fabric/match-events.kql        Eventhouse table, JSON mapping, analytics queries
.github/workflows/             ci.yml (build, test, npm audit), deploy.yml (OIDC, manual)
Dockerfile                     node build → dotnet publish → aspnet runtime, non-root, port 8080
docs/                          SECURITY.md, DATA.md, PROJECT_CONTEXT.md (this file)
```

Project references: Domain ← Simulation ← Data ← Api; Domain ← Agents ← Api; Tools → Domain, Simulation, Data. **Agents → Domain only.**

### Runtime flow

1. `POST /api/ops/matches/{id}/start {speed}` → `MatchRunner` streams the match from the registered `IMatchFeed`. Today that is `SyntheticMatchFeed`: `MatchSimulator.Simulate` (deterministic per seed) plus 25 Hz `TrackingSynthesizer` frames, replayed in accelerated real time (`clock / speed`, plus a 120-second half-time break in match time). A broadcaster adapter implementing `IMatchFeed` can replace it without agent changes; see `docs/LIVE_FEEDS.md`.
2. Each event → `IMatchEventWriter.Append` (`InMemoryMatchStore`). The store enforces strictly increasing sequence numbers, runs `StateProjector` to produce an immutable `MatchSnapshot`, writes to an internal channel (`IMatchEventFeed`) and raises `EventAppended`. It is also mirrored to Event Hubs if configured.
3. `AgentHost` (BackgroundService) reads the feed and calls `MatchOrchestrator.HandleAsync` per event.
exceptions are isolated and audited. **Agent isolation:** every proposed insight is checked against its author's `AgentCharter` (`Orchestration/AgentCharter.cs`). The agent name must match, the id must sit in the agent's own namespace, the insight must be about the current event, its kind and priority must be within the agent's role, `GeneratedBy` must be `template`, and an agent may propose at most 10 insights per event. Violations are dropped and audited as `agent.rejected`. Insight `Facts` and `Tags` are frozen copies, and `Upstream` is a read-only list, so no agent can change another agent's output. Any insight that fails `OutputGuard` is blocked, not published. Per-agent counters
5. `SupervisorPolicy.Filter` dedupes and paces: priority 3+ always passes; same-metric low-priority insights need a 90-second gap; player action tags need a 45-second gap per player.
6. `OutputGuard` checks every template. Violations are audited, and the target is zero. Priority 4+ KeyMoment and Recap insights may go to `NarrativeComposer` (LLM) in the background if configured.
7. `IInsightSink.Publish` → `InsightStore` (append-only, separate from events) raises `Published`.
8. Subscribers to `Published`:
   - `PredictionLedger` (platform) registers questions and applies scores.
   - `RealtimeBroadcaster` (Api) groups SignalR connections by viewer profile, calls `PersonalizationAgent.ShapeAsync` once per profile and pushes `overlay`. It also pushes `winprob` and `leaderboard`, and throttles `snapshot` to every 400 ms (immediately on goals, kick-off, HT, FT and red cards).

### Domain model (key types)

- `MatchEvent`: Id, MatchId, Sequence, ClockSeconds (0..5400, no stoppage time), Type (KickOff, Pass, Shot, Goal, Save, Tackle, Interception, PossessionChange, Pressure, Foul, Corner, YellowCard, RedCard, Substitution, Sprint, HalfTime, FullTime), Team (Home/Away), PlayerId, TargetPlayerId (receiver, assister or outgoing sub), X/Y/EndX/EndY (metres on 105×68, home attacks towards X=105), Success, UnderPressure, SpeedKmh, ExpectedGoals, OnTarget. `Minute` = clock/60+1 capped at 90; HalfTime shows 45.
- `MatchSnapshot`: phase, team stats (goals, shots, on target, xG, passes, tackles, interceptions, corners, fouls, cards, pressure, subs, possession seconds, max shot speed), goals list, per-player live stats (passes, shots, goals, tackles, distance km, top speed, on pitch).
- `Insight`: Kind (Stat, PlayerAlert, KeyMoment, Milestone, WinProbability, PredictionOpened, PredictionResolved, Recap), Agent, Priority 1–5, Team, PlayerId, Metric, **two voices** (`Analyst`, `Casual` as `TextVariant(Headline, Body)`), `Facts` (verified numbers the text may use), `Tags` (ids, cause, question data), GeneratedBy (template/llm), Minute.
- `Overlay`: render instruction (Template: lower-third, stat-card, player-tag, prediction-card or recap-panel; DurationMs; Priority; Mode; Language; Translated; Title; Text; Data; Tags; ForYourClub; ForYourPlayer).
- `ViewerProfile`: Mode (Casual/Analyst), Language, FavoriteClubId, FocusPlayerId, FocusMetric. Enums and ids only, no free text.
- Predictions: `ScorePrediction`, `OutcomeProbabilities`, `PreMatchCall` (source, prediction, probabilities, factors, rationale), `PreMatchBriefing` (model, pundit, storylines, expected goals), `FanPick`, `MicroQuestion`, `Leaderboard`.

### Interfaces and the security model

- **Read-only (agents may use):** `ILeagueReadModel`, `IMatchReadModel`, `IInsightReadModel`, `IFanPredictionReadModel`, `IMatchEventFeed`. Methods are only Get/List.
- **Agent output:** `IInsightSink.Publish` only.
- **Write capabilities, `[DataWriteCapability]`, platform only:** `IMatchEventWriter.Append`, `IMatchRegistryWriter.Register/MarkLive/MarkFinished`, `IFanPredictionWriter.SubmitPick/SubmitMicroAnswer`. No update or delete exists.
- Enforced by `tests/.../ArchitectureTests.cs`: the Agents assembly must not reference Data or Api; no agent type may have a constructor parameter, field or property assignable to a write capability; write interfaces may not contain Update, Delete, Remove, Clear, Replace, Overwrite or Truncate; `IInsightSink` has only `Publish`; read models only have Get/List.

### Agents

| Agent | File | Does |
|---|---|---|
| Stats | `StatsAgent.cs` | Pass difficulty ≥ 8.2 → Stat (p2); difficulty ≥ 6.5 → player action tag (p1, focus mode only); every shot → Stat (p2, p3 if ≥ 110 km/h or xG ≥ 0.3); sprint ≥ 34 km/h above the player's previous alert → PlayerAlert; 5 km and 10 km distance alerts; pass milestones at 40/60; team shots at 10/15; every 5 minutes, control/chaos/tempo (p3 "in control" only when pass share ≥ 60% or chaos ≥ 60) |
| Explainer | `ExplainerAgent.cs` | Win probability on every event (`InPlayModel`); WinProbability insight once per minute; Goal and RedCard KeyMoments (p5, before/after WP); swing KeyMoment (p4) when the *gaining* team's win chance rises ≥ 12 points since the last announcement, not before 10', at most every 3 minutes; causes scored: substitution (pressing share after vs before ≥ 8 points), pressure (share ≥ 58%), territory (pass share ≥ 58%), shots (≥ 3 in 10 min), clock (only if leading after 60'), momentum fallback; pressure-shift KeyMoment (p3) when pressing share moves ≥ 20 points between consecutive 5-minute windows and reaches ≥ 60% |
| Referee | `PredictionRefereeAgent.cs` | Schedule of 8 micro questions (opens at clock 300, 900, 1500, 2100, 3000, 3600, 4200, 4800): shot on target / corner in 5, goal in 10, home more possession in 5. Fans have 120 match seconds to answer before the window starts. The model answers with Poisson rates from calibration × tempo; the pundit answers with a gut-feel heuristic. Resolves from the window's events and explains; at full time settles the pre-match calls (model vs pundit; outcome +3, exact score +2) |
player of the match, crowd share; analyst text builds on Vision's fastest tracked shot (`trackedShotKmh`, `buildsOn` tag) |
| Producer | `ProducerAgent.cs` | Production stage. At half-time and full time it cuts the highlights reel (`HighlightReel.Rank`) from published insights plus upstream proposals and emits a p3 Recap (`metric: highlights`, `picks` tag). `/highlights` replays the latest cut and falls back to live ranking before half-time |
| Vision | `Vision/AutoEventingAgent.cs` | Auto-eventing from synthetic ball-tracking frames (read via `ITrackingReadModel`): median start/end positions and least-squares in-flight speed; every shot gets a tracked-speed Stat (p2); every 15 minutes a summary (p2) of actions measured and mean speed/distance error vs tagged data. Analyst mode only (p2) |
| Personalization | `PersonalizationAgent.cs` | Visibility rules per profile (KeyMoment, Recap and predictions always; focus player and focus metric always; analyst ≥ p2; casual ≥ p3, or ≥ p2 for your club); chooses the voice; translates via Translator with a cache; emits `Overlay` with template and duration (p5 9 s, p4 7 s, p3 6 s, else 4.5 s) |
| Orchestrator | `Orchestration/MatchOrchestrator.cs` | Supervisor: context, fan-out, fault isolation, pacing, guard, optional LLM enrichment (max 2 concurrent), publish, audit |
| Briefing | `Briefing/BriefingService.cs` | Cached pre-match briefing: `PreMatchModel.Predict`, `PunditPersona.Predict`, `Storylines.For` (attack vs defence, form, finisher to watch) |

### Models

- `PreMatchModel`: Poisson team strengths fitted iteratively (25 iterations) on 3 synthetic seasons, with season weights 1, 0.6 and 0.36. Expected goals = league mean home/away × attack × opponent defence. Outcome grid up to 10 goals; most likely score within the most likely outcome; factors and rationale for the UI; form (last 6) and head to head.
- `InPlayModel`: remaining fraction of 90 minutes × momentum (pass share and pressing share over the last 10 minutes, shrunk by `sample/(sample+60)`, weight 1.0) × red cards (0.7 / 1.25) → Poisson from the current score.
- `PunditPersona`: lean = form PPG difference + 0.3 home + 0.3 × head-to-head balance; |lean| < 0.45 → 1-1; else 2-1, or 2-0 if > 1.2.
- `WindowMetrics`: per-side passes, completion, pressure, shots, on target, corners, ball wins, goals, possession changes; ControlIndex = 100 × (0.65 × pass share + 0.35 × completion); ChaosIndex = 100 × (0.7 × clamp((turnovers per minute − 2)/3) + 0.3 × shots-at-both-ends).
- `PassDifficulty.Rate`: 1 + 4 × length factor + 2 × progression factor + 2 if under pressure + 1 if ending in the final third, clamped to 0..10.

### Simulator (tuned; averages over 600 matches with default priors)

About 1,580 events per match. Home 1.71 / away 1.28 goals (targets 1.62 / 1.33); H/D/A .47/.20/.33 (targets .45/.23/.32); 13.1 shots per team (12.8); 37% on target; 4.7 corners; 2.0 yellows; about 430 passes per team at 82% accuracy. Mechanics: possession chains; pass success depends on passing skill, pressure, length, final third, intensity, red cards, opponent defence and relative strength; shot propensity ∝ (λ/mean)^1.6 in the final third; xG = 0.75·e^(−d/7.5) + 0.02; mean-reverting team intensity with substitution boosts (+0.22) and game-state effects after 60' (trailing pushes, leading sits deeper); 3 subs per side between 55' and 80'; fouls 3.1% per pass, with card rates from calibration. League: 20 fictional clubs (`LeagueFactory`, seed 2026), 23 players each, names from invented syllables; history from `HistoryGenerator` (seed 7, 3 double round-robin seasons with strength drift).

### Guardrails (`Agents/Guardrails`)

- `OutputGuard.Check(text, facts, matchPlayerNames, leaguePlayerNames)`: every number must equal a fact ±0.51 or fact×100 ±0.51 (0 and 1 always allowed); no league player outside this match may be named; banned betting terms; no links, emails, @mentions, `<>{}[]\` or backticks; headline ≤ 90, body ≤ 360.
- `NarrativeComposer`: only for priority ≥ 4 KeyMoment/Recap when an LLM is configured; strict system prompt; JSON `{analyst:{headline,body},casual:{headline,body}}`; per-match budget (`AI:MaxLlmCallsPerMatch`, default 40); timeout `AI:RequestTimeoutSeconds` (6); then guard, then Content Safety (fails closed); any failure keeps the template; audited as llm.accepted, llm.rejected, llm.timeout, llm.unusable or llm.budget.
- Templates must use culture-invariant formatting (`Fmt` helpers: Pct, N0, N1, N2, S, Count, Possessive) and every number in text must be in `facts`. The pipeline test asserts zero template violations.

### API (Api/Endpoints.cs)

Reads (`read` policy, 240/min/IP): `GET /api/health`, `/api/meta` (calibration, agents, languages, focus metrics, service status), `/api/clubs`, `/api/clubs/{id}/squad`, `/api/matches`, `/api/matches/{id}`, `/briefing`, `/winprob`, `/insights?skip=`, `/overlays?mode=&language=&club=&player=&metric=&afterClock=`, `/squads`, `/events.jsonl`, `/leaderboard`, `/question`, `/api/audit?matchId=&max=`.
Fan writes (`write` policy, 30/min/IP): `POST /api/matches/{id}/picks {fanId(GUID), nickname, outcome, homeGoals, awayGoals}` (closes at kick-off, one per fan), `POST /api/matches/{id}/answers {fanId, nickname, questionId, answerYes}`.
Operator (`X-Operator-Key`, constant-time compare; open in Development only if no key is set): `POST /api/ops/matches {homeClubId, awayClubId, seed?}`, `POST /api/ops/matches/{id}/start {speed 1..60}`.
SignalR `/hubs/match`: client invokes `Join(matchId, profile)` → returns an error string or null. Server events: `snapshot` (MatchView), `winprob` (minute, home, draw, away), `overlay` (Overlay), `leaderboard`.
Seeded fixtures (`Demo:Fixtures`): northvale:redcastle, ashbury:ravenport, kingsmere:harbourside, westbrook:elmfield.

### Security middleware (Api)

Kestrel body limit 16 KB, no server header; SignalR message limit 8 KB; CSP `default-src 'self'` (fonts bundled via @fontsource, no CDNs), nosniff, no-referrer, SAMEORIGIN, permissions policy, COOP; HSTS and exception handler outside Development; CORS closed unless `Security:AllowedOrigins` is set (Development allows http://localhost:5173); input validation in `Contracts.cs` (`Validation`: profile allow-lists, nickname `^[A-Za-z0-9 _\-]{2,20}$` minus reserved words, consistent picks).

### Azure AI clients (REST, no SDKs, no NuGet)

`FoundryChatModel` (Azure OpenAI chat completions at `{FoundryEndpoint}/openai/deployments/{ChatDeployment}/chat/completions`, json_object response), `AzureTranslator` (Text Translation v3), `AzureContentSafety` (text:analyze, reject severity ≥ 2). Auth: API key for local development, or `ManagedIdentityTokenProvider` (Container Apps IDENTITY_ENDPOINT/IDENTITY_HEADER, resource https://cognitiveservices.azure.com). `EventHubsMirror`: batched REST send, SAS (local) or managed identity (resource https://eventhubs.azure.net). All optional: unconfigured = templates and English, mirror off.

Config sections: `AI` (FoundryEndpoint, ChatDeployment, ChatApiVersion, FoundryApiKey, TranslatorEndpoint, TranslatorKey, TranslatorRegion, TranslatorResourceId, ContentSafetyEndpoint, ContentSafetyKey, UseManagedIdentity, ManagedIdentityClientId, RequestTimeoutSeconds, MaxLlmCallsPerMatch, LlmEnabled), `EventHubs` (Namespace, HubName, SasKeyName, SasKey), `Security` (OperatorKey, AllowedOrigins), `Calibration:Path` (default data/calibration.json), `Demo:Fixtures`.

### Infra (`infra/main.bicep`, not yet deployed or validated)

User-assigned identity; Log Analytics; ACR (admin disabled); Key Vault (RBAC, purge protection) with the `operator-key` secret; Event Hubs Standard (`disableLocalAuth`), hub `match-events`, consumer group `fabric`; AIServices "Foundry" account (`disableLocalAuth`) with a gpt-4o-mini GlobalStandard deployment; TextTranslation account (`disableLocalAuth`); roles: AcrPull, Key Vault Secrets User (on the secret), **Event Hubs Data Sender only**, Cognitive Services OpenAI User, Cognitive Services User; Container Apps environment and app (port 8080, 1 replica because state is in memory, sticky sessions, Key Vault secret reference, env vars wiring AI__* and EventHubs__*). Fabric: Eventstream from the hub's `fabric` consumer group into an Eventhouse; run `fabric/match-events.kql`.

### Tools

- `calibrate [--seasons 2324,2425,2526] [--file a.csv ...] [--out src/SecondHalfMind.Api/data/calibration.json]`: downloads football-data.co.uk `E0.csv` per season into memory and writes only aggregates (H/D/A rates, mean goals, attack and defence log-strength std, shots, on-target rate, goals per shot, corners, yellows, reds). Tested on a format-identical sample file.
- `generate [--matches 10] [--seed 42] [--out datasets] [--calibration path]`: writes league.json, history.csv and per-match JSONL.

### Frontend (`web/`)

React 18 + TS + Vite 5; `@microsoft/signalr`; fonts Barlow Condensed (display) and Inter (body), bundled. Hash routes: `#/` Lobby, `#/match/{id}` MatchCentre, `#/overlay/{id}?mode=&lang=&club=&player=&metric=` BroadcastOverlay (transparent; plays the newest priority ≥ 3 overlay from the last 120 match seconds for its durationMs; priority 5 interrupts).
Components: `SwingChart` (the signature element: home-minus-away win probability line, half-time line, goal markers, club-colour fills), `OverlayFeed`, `PredictionPanel` (pick form, model vs pundit cards, live question with Yes/No, leaderboard), `StatsPanel` (essentials, or the full sheet in analyst mode; focus player card), `ProfileBar` (Fan/Analyst, language, club, follow player, always-show metric), `GuardrailsPanel` ("How this was made": principles, service status, audit log).
`useMatchStream` loads history over REST, then joins the hub and re-joins when the profile changes. `fanId` is a GUID in localStorage (guarded). Design tokens: turf #0E1A14, pitch #142620, raised #1A2F26, line #29402F, chalk #EEF2EA, moss #9DB0A3, floodlight #C6F432, model #3DDCFF, pundit #FF8A3D; sentence-case labels; club colours carry team identity.

---

## 4. Build, run, test

```bash
dotnet run --project src/SecondHalfMind.Api            # http://localhost:5080 (API + built web if present)
cd web && npm install && npm run dev                    # http://localhost:5173, proxies /api and /hubs
cd web && npm run build                                 # outputs to src/SecondHalfMind.Api/wwwroot
dotnet test                                             # xUnit
dotnet run --project src/SecondHalfMind.Tools -- calibrate
```

Verified in the original workspace: all src projects build with **0 warnings** (warnings are errors); full matches run end to end at 40–60x with **zero guard violations** and no agent errors; two concurrent matches work; frontend type-checks and builds; desktop and 390 px mobile screenshots had no console errors; the broadcast overlay stays in sync and shows goals.

**Caveat:** that workspace had no NuGet access, so the 40 tests were compiled and run against a small local xUnit stand-in, not real xUnit. Run `dotnet test` first in your environment; analyzer warnings in the test project don't fail the build (`TreatWarningsAsErrors=false` there).

---

## 5. Not done, unverified, or worth improving (priority order for the deadline)

1. **Push to a public GitHub repo**; check that CI passes (`dotnet test`, npm build, `npm audit`). A `package-lock.json` is required for `npm ci`; it is generated by `npm install`.
2. **Run `calibrate`** on your machine (the original workspace couldn't reach football-data.co.uk). Until then the UI says "default top-flight averages". After calibrating, rerun a few matches; the simulator constants were tuned against the defaults.
3. **Use GitHub Copilot for real** during the remaining work, and mention it honestly in the README and the pitch.
4. **Azure:** `az deployment group what-if` the Bicep, deploy, configure the OIDC repo variables, run the deploy workflow. Then test Foundry, Translator, Content Safety and the Event Hubs mirror live, and set up the Fabric Eventstream and Eventhouse. None of these were exercised against real services.
5. **Language switching in the demo** needs Translator configured; without it overlays stay English (the UI says so).
6. **Record the demo** (< 2 min, real app footage, no third-party trademarks or unlicensed music), upload it publicly, and fix the pitch wording (section 2).
the AI pundit could use the LLM for its voice (behind the same guard); auto-eventing from real video (a model writing `BallTrack`s through `ITrackingWriter`) is roadmap; synthetic tracking-frame auto-eventing is built.

---

## 6. Rules for refactoring (do not break these)

1. **SecondHalfMind.Agents must reference only SecondHalfMind.Domain.** Never add a reference to Data or Api.
2. **Agents never receive `[DataWriteCapability]` interfaces.** Their only output is `IInsightSink.Publish`.
3. **No update, delete or remove methods** on write interfaces; the event store stays append-only and rejects out-of-order sequences.
4. **Scores and other state changes happen in platform services** (e.g. `PredictionLedger`), driven by insights, never by agents.
5. **Every number in insight text must exist in `Facts`**; use the `Fmt` helpers (invariant culture). Keep `PipelineTests.Every_template_insight_passes_the_output_guard` green.
6. **LLM output goes through `NarrativeComposer`** (facts only, no tools, no fan text in prompts, guard + Content Safety, template fallback, audited). Never show raw model output.
7. **No secrets in source.** Use user-secrets locally and managed identity / Key Vault in Azure. Keep `disableLocalAuth` in Bicep.
8. **Synthetic only in the app, repo and demo.** Real data may only enter via `calibrate`, which writes anonymous aggregates. `*.csv` stays git-ignored.
9. **Free to play.** No odds, stakes or money; betting terms stay blocked.
10. Keep `TreatWarningsAsErrors` on for src projects, and keep `ArchitectureTests` running in CI.
