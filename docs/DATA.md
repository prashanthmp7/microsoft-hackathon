# Data: synthetic by design, calibrated by real history

## What the app uses

Everything a viewer sees is synthetic:

- **League:** 20 fictional clubs with invented names and colours, 23 fictional players each (names are generated from invented syllables).
- **History:** three generated seasons of results (double round robin), which the prediction model is trained on.
- **Matches:** an event-level simulator producing passes (with coordinates, length, pressure and ball speed), shots (xG, speed, on/off target), saves, tackles, interceptions, pressure, possession changes, fouls, corners, cards, substitutions and sprints. Matches are deterministic for a seed.

No real Premier League match data, club names or player names are included in the repository, the app, its outputs or the demo.

## How real history is used: calibration only

So that the synthetic league *behaves* like the real one, the calibration tool reduces real historical results to anonymous league-level averages:

| Prior | Used for |
|---|---|
| Home win, draw and away win rates | Sanity-checking the simulator |
| Mean home and away goals; home advantage | Expected goals per match |
| Spread of team attack and defence strengths (standard deviation of fitted log strengths) | How unequal the fictional clubs are |
| Shots per team, share on target, goals per shot | Shot volume and conversion |
| Corners, yellow and red cards per team | Set-piece and discipline rates |

**Source:** results for the last three completed English top-flight seasons (2023-24, 2024-25 and 2025-26) from football-data.co.uk.

**Handling:**

1. Files are downloaded into memory, or read from paths you supply. They are never written to disk by the tool.
2. Team strengths are fitted per season, then only their *spread* is kept.
3. The output `calibration.json` contains only the numbers above, the season codes, a match count and the source description. It contains no club or player names and no individual results.
4. `*.csv` is in `.gitignore` so raw files can't be committed by mistake.

**Players:** because all players are synthetic, no real squad data (current or past) is used. Player attributes are drawn around club quality, and the simulator's sprint, distance and shot-speed ranges are set to realistic values.

Until you run `calibrate`, the app uses approximate default priors and labels them as such (`isDefault: true`, shown in the UI footer and under **How this was made**).

## Data the app creates

| Dataset | Where | Contents |
|---|---|---|
| Event streams | `GET /api/matches/{id}/events.jsonl`, `tools generate` | Every synthetic event as JSON Lines |
| League and history | `tools generate` (`league.json`, `history.csv`) | Fictional clubs, squads, three seasons of results |
| Labelled moments | `GET /api/matches/{id}/insights` | Each insight with its kind, cause tag (goal, substitution, pressure, territory, shots…) and the verified facts behind it |
| Crowd predictions | Prediction ledger | Fan picks and answers vs model and pundit, by match |
| Eventhouse | Fabric, via Event Hubs | The live event stream for KQL analytics (`fabric/match-events.kql`) |

Fan data is limited to a random fan ID generated in the browser and a validated nickname. No accounts, e-mail addresses or tracking.
