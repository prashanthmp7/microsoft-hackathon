# Security and guardrails

Second Half Mind is designed so that a misbehaving agent, prompt or model cannot damage data, mislead viewers or leak secrets. This document lists each control, what it protects against, and where it is enforced.

## 1. Agents cannot modify or delete data

| Control | Threat | Enforcement |
|---|---|---|
| Agents project references `SecondHalfMind.Domain` only | Agent code reaching into stores | Project references; `ArchitectureTests.Agents_do_not_reference_the_data_layer` |
| Agents receive read-only interfaces (`IMatchReadModel`, `ILeagueReadModel`, `IInsightReadModel`, `IFanPredictionReadModel`) | Accidental or malicious writes | `ArchitectureTests.Read_models_expose_no_mutating_methods` |
| Write interfaces are marked `[DataWriteCapability]` and no agent may depend on one | Privilege creep over time | `ArchitectureTests.No_agent_type_depends_on_a_write_capability` |
| Write interfaces have no update, delete, remove or clear methods | Destructive operations | `ArchitectureTests.Write_capabilities_are_append_only` |
| Agent output is a separate, append-only insight store with a single `Publish` method | Agents overwriting history | `ArchitectureTests.Agent_output_channel_is_append_only`; `InsightStore` ignores duplicate IDs |
| Event store rejects duplicate or out-of-order sequence numbers | Replay or tampering | `InMemoryMatchStore.Append`; `PipelineTests.Event_store_rejects_replayed_or_reordered_events` |
| Scores are applied by the platform ledger, not by agents | An agent awarding points | `PredictionLedger` consumes resolution insights; the referee agent only publishes |
| A full match through all agents leaves the event log byte-for-byte identical | Hidden side effects | `PipelineTests.Agents_never_change_the_event_log` |

In Azure the same principle continues at the resource level: the app identity has the **Event Hubs Data Sender** role only (it cannot read, delete or manage the hub), and the Fabric Eventhouse is fed by Eventstream, not by the app.

### Agents cannot interfere with each other

| Control | Threat | Enforcement |
|---|---|---|
| Charters are bound to the agent's concrete type, then checked for name, ID namespace, current event, kind, priority and `GeneratedBy` | One agent speaking as, overwriting or out-ranking another | `AgentCharter`; `AgentIsolationTests` |
| Insight `Facts` and `Tags` are frozen copies; `Upstream` is a read-only list | A later agent changing an earlier agent's output | `Insight`; `AgentIsolationTests` |
| Read models handed to agents are scoped to the current match | Reading another match's insights or fans | `MatchScopedReadModels` |
| 250 ms time budget per call, at most 10 insights per event | A slow or flooding agent starving the others | `AgentGuard.Run` |
| 5 strikes (timeout, exception, flood, charter break) quarantine the agent for the match | Repeated misbehaviour | `AgentGuard`; shown as `quarantined` in agent activity |

**Limit:** agents share one process. These checks contain faulty first-party agents, not hostile code (for example reflection or unmanaged memory). Third-party agents must run in their own process or container and reach the platform only through the insight contract.

## 2. Generated text is grounded and safe

Templates produce every insight deterministically from measured facts. A language model (Microsoft Foundry) may rephrase only the highest-priority moments, and only under these rules:

- **Inputs:** verified numeric facts, the names of players already in the template, and the template text. Fan input (nicknames, answers) never reaches a prompt, and the model has no tools.
- **Output guard** (`OutputGuard`), applied to both voices:
  - every number in the text must equal a fact (or a fact expressed as a percentage);
  - no player may be named unless they are in this match;
  - no betting or gambling language (free to play);
  - no links, e-mail addresses, @mentions, markup, brackets or template braces, so nothing can inject into a renderer;
  - headline and body length limits so overlays always fit.
- **Content Safety:** Azure AI Content Safety must pass (fails closed if configured but unreachable).
- **Fallback:** any failure (timeout, malformed JSON, guard violation, unsafe content) keeps the template. Every decision is written to the audit log (`llm.accepted`, `llm.rejected`, `llm.timeout`, `llm.budget`).
- **Budgets:** at most 2 concurrent model calls, a per-match call cap (`AI:MaxLlmCallsPerMatch`) and a global kill switch (`AI:LlmEnabled`).
- **Template self-check:** the orchestrator runs the guard on every template too; violations are audited and covered by `PipelineTests.Every_template_insight_passes_the_output_guard`.

## 3. Trust boundary: the API

- **Allow-listed input only.** Viewer profiles are enums and identifiers checked against the league (language codes, clubs, players in this match, metrics). The only free text is a nickname: 2–20 letters, digits, spaces, `-` or `_`, with reserved words blocked. Picks are range-checked and must be internally consistent.
- **Rate limiting** per client IP: 240 reads and 30 writes a minute.
- **Request size limits:** 16 KB HTTP bodies, 8 KB SignalR messages.
- **Match control** (`/api/ops/*`) requires an operator key, compared in constant time. With no key configured these endpoints are open only in Development and disabled elsewhere. `Security:AdditionalOperatorKeys` allows rotation without downtime.
- **Fan identity:** `POST /api/fans` issues a fan ID and an HMAC-SHA256 token. Picks and answers need a matching `X-Fan-Token`, so nobody can play as another fan. Set `Security:FanTokenKey` in Key Vault; without it a random key is used per process.
- **Diagnostics** (`/api/audit`, `/api/matches/{id}/agents`) need the operator key outside Development unless `Security:PublicDiagnostics` is `true`.
- **Headers:** strict Content-Security-Policy (no third-party scripts or fonts; fonts are bundled), `X-Content-Type-Options`, `Referrer-Policy: no-referrer`, frame and permissions policies, HSTS outside Development, no server banner.
- **CORS** is closed by default and opened only to configured origins.
- **SignalR hub** exposes only `Join`; there is no hub method that changes data.

## 4. Secrets and identity

- No secrets in source. Local development uses `dotnet user-secrets`; Azure uses a user-assigned managed identity.
- Local (key) authentication is disabled on Event Hubs, the Foundry resource and Translator in `infra/main.bicep`, so there are no keys to leak.
- The operator key lives in Key Vault (RBAC, soft delete, purge protection) and reaches the app as a Key Vault secret reference.
- The container runs as a non-root user from the minimal ASP.NET runtime image.
- CI deploys with GitHub OIDC federation; no Azure credentials are stored in the repository.

## 5. Monitoring

- Container logs go to Log Analytics.
- The audit log records agent publishes, isolated agent errors and every guardrail decision; it is visible in the app under **How this was made** and at `GET /api/audit`.
- An agent that throws is isolated by the orchestrator: the error is audited and the match continues.
- Audit entries are SHA-256 hash-chained; `InMemoryAuditLog.Verify` detects edited, inserted or removed entries. Set `Audit:FilePath` to also append them as JSON lines.
- `.github/workflows/security.yml` runs tests, NuGet and npm vulnerability checks and CodeQL; Dependabot keeps NuGet, npm and Actions dependencies current.

## Reporting

Please report security issues privately to the repository owner rather than opening a public issue.

## Agents as separate services

Set `Agents:Mode=ServiceBus` (default `InProcess`) to run each agent in its own container (`src/SecondHalfMind.AgentService.*`, image from `Dockerfile.agent`). The platform sends a self-contained, match-scoped snapshot to queue `agent-<name>` and reads proposals from `agent-results`. Services have no reference to Data or Api.

- Authentication is managed identity only: one user-assigned identity per agent, Service Bus `disableLocalAuth: true`, no connection strings or shared keys.
- RBAC is queue-scoped: each agent may receive only from its own queue and send only to the results queue; only the platform may send requests.
- Remote output is untrusted: the platform checks request id and agent name, then applies the charter of the represented concrete type, the time budget, quarantine and every output guard.
- Agent containers have no ingress and run one replica to keep event order.
