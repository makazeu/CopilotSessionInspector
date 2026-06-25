# Copilot Session Inspector

A .NET 10 Blazor Web App that loads the current user's **GitHub Copilot CLI**
sessions, reconstructs them, and analyzes their **time / token / AI-credit (AIU)**
consumption — with charts and concrete cost-reduction suggestions.

## What it does

- **Reconstructs sessions** – replays every turn: your prompts, the agent's
  replies, and the full sequence of **operations** (tool calls with name,
  arguments, result and duration) behind each turn.
- **Analyzes cost** – per turn and per model: input / output / reasoning tokens,
  AI units (AIU), premium cost, cache reuse, and latency. Per-tool breakdown too.
- **Charts** – tokens per turn, cumulative AIU & time, and context-window growth
  (rendered with Chart.js).
- **Suggestions** – rule-based recommendations to reduce token/credit usage
  (context-window pressure, input-dominated spend, low cache reuse, reasoning
  share, expensive-model routing, tool-iteration counts, latency).

## Data sources (read-only, current user only)

All data is read locally from `%USERPROFILE%\.copilot`:

| Source | Used for |
| --- | --- |
| `session-store.db` (SQLite) | session metadata, conversation turns, checkpoints |
| `logs\process-*.log` | telemetry events keyed by `session_id`: `assistant_usage` (tokens, `total_nano_aiu`, cost, duration), `tool_call_executed` (tool name, arguments, result, duration), `assistant_message`, `session_usage_info` (context window), `assistant_turn_start`, `session_shutdown`. Each event carries a `created_at` timestamp and `turn_id`/`api_call_id` used to group operations into turns. |

The SQLite database is opened **read-only**, so it can be inspected while the CLI
is running. Nothing is ever written back.

## Run

```powershell
cd CopilotSessionInspector
dotnet run
```

Then open the printed URL and go to **Sessions**. The first load parses the
telemetry logs (cached afterwards); use **Reload telemetry** to re-scan.

To point at a different `.copilot` folder, set `CopilotHome`:

```powershell
$env:CopilotHome = "D:\some\.copilot"; dotnet run
```

## Project layout

- `Services/CopilotPaths.cs` – resolves the `.copilot` data locations.
- `Services/SessionStoreService.cs` – reads `session-store.db`.
- `Services/TelemetryLogParser.cs` – extracts telemetry JSON blocks from logs.
- `Services/SessionAnalysisService.cs` – merges data and generates suggestions.
- `Models/Models.cs` – data and analysis models.
- `Components/Pages/Sessions.razor` – session list with rolled-up totals.
- `Components/Pages/SessionDetail.razor` – timeline, charts, model breakdown,
  suggestions.
- `wwwroot/js/charts.js` – Chart.js interop.
