# Copilot instructions for Second Half Mind

Full background, decisions and backlog: `docs/PROJECT_CONTEXT.md`. Read it before large changes.

## What this is

A .NET 8 + React (TypeScript, Vite) hackathon entry for *Inside the Game* (Microsoft × Premier League). It is a multi-agent engine that ingests **synthetic** football events, interprets them into metrics, explains why moments matter, renders timed overlay JSON, and personalises output (fan or analyst mode, club, followed player, focus metric, language). Fans predict against a Poisson model and an AI pundit. Deadline: 27 Oct 2026, 11:59 PM PT.

## Non-negotiable rules

- `src/SecondHalfMind.Agents` references **only** `SecondHalfMind.Domain`. Never add references to `.Data` or `.Api`.
- Agents receive only read-only interfaces (`IMatchReadModel`, `ILeagueReadModel`, `IInsightReadModel`, `IFanPredictionReadModel`, `IMatchEventFeed`). Their only output is `IInsightSink.Publish`. Never inject a `[DataWriteCapability]` interface into an agent.
- Write interfaces are append-only: never add Update, Delete, Remove, Clear or Replace methods. The event store rejects out-of-order sequences.
- State changes from agent output (e.g. scoring) belong in platform services such as `PredictionLedger`, triggered by insights.
- Every number in insight text must appear in that insight's `Facts`. Format with the `Fmt` helpers (invariant culture). `OutputGuard` and `PipelineTests` enforce this.
- Language model output only via `NarrativeComposer`: facts in, no tools, never fan-typed text in prompts, guard + Content Safety, template fallback, audit every decision.
- No secrets in source. Use user-secrets locally and managed identity + Key Vault in Azure. Keep `disableLocalAuth: true` in `infra/main.bicep`.
- Synthetic data only in the app, repo and demo. Real history enters only through `SecondHalfMind.Tools calibrate`, which writes anonymous aggregates. Never commit CSVs or real club or player names.
- Free to play: no odds, stakes or money; betting language is blocked.

## Conventions

- C#: nullable enabled, `TreatWarningsAsErrors` in src projects, records for immutable data, file-scoped namespaces, XML doc comments on public types explaining *why*.
- Keep the agent interface: `IMatchAgent.OnEvent(AgentContext) => IEnumerable<Insight>`, deterministic and side-effect free apart from the agent's own per-match memory.
- Insights carry two voices (`Analyst`, `Casual`). Priority 1–5: 5 must show, 3+ always passes the supervisor.
- API inputs are allow-listed in `Contracts.cs` (`Validation`). Add new inputs there, with rate limiting.
- Frontend: React function components, no UI framework, tokens in `web/src/styles.css`, sentence-case copy, accessible (labels, focus states, aria-live for live updates), works at 390 px.
- Add or update tests in `tests/SecondHalfMind.Tests` for any behaviour change; keep `ArchitectureTests` green.

## Commands

```bash
dotnet build && dotnet test
dotnet run --project src/SecondHalfMind.Api        # http://localhost:5080
cd web && npm install && npm run dev                # http://localhost:5173
dotnet run --project src/SecondHalfMind.Tools -- calibrate
```
