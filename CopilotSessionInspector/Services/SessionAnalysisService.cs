using CopilotSessionInspector.Models;

namespace CopilotSessionInspector.Services;

/// <summary>Combines session-store data with telemetry into per-session analyses and suggestions.</summary>
public sealed class SessionAnalysisService
{
    private readonly SessionStoreService _store;
    private readonly TelemetryLogParser _telemetry;
    private readonly SessionEventsParser _events;

    public SessionAnalysisService(SessionStoreService store, TelemetryLogParser telemetry, SessionEventsParser events)
    {
        _store = store;
        _telemetry = telemetry;
        _events = events;
    }

    public bool DataAvailable => _store.DatabaseExists;

    public DateTimeOffset TelemetryParsedAt => _telemetry.ParsedAt;

    public void Reload()
    {
        _telemetry.Reload();
        _events.Reload();
    }

    /// <summary>Rows for the sessions list, with telemetry totals rolled up.</summary>
    public List<SessionSummaryRow> GetSessionRows()
    {
        var sessions = _store.GetSessions();
        var turnCounts = _store.GetTurnCounts();
        var telemetry = _telemetry.GetAll();

        var rows = sessions
            .AsParallel()
            .AsOrdered()
            .WithDegreeOfParallelism(Math.Max(1, Environment.ProcessorCount - 1))
            .Select(s =>
            {
                telemetry.TryGetValue(s.Id, out var tele);
                var usage = tele?.Usage ?? new List<AssistantUsageEvent>();
                int turnCount = turnCounts.TryGetValue(s.Id, out var tc) ? tc : 0;
                var eventSummary = _events.GetSummaryForSession(s.Id);
                EnrichSessionFromSummary(s, eventSummary);
                double sessionDurationMs = SessionDurationMs(eventSummary, tele, s);
                var row = new SessionSummaryRow
                {
                    Session = s,
                    TurnCount = turnCount,
                    ApiCallCount = usage.Count,
                    TotalTokens = usage.Sum(u => u.TotalTokens),
                    TotalAiu = usage.Sum(u => u.Aiu),
                    TotalCost = usage.Sum(u => u.Cost),
                    TotalApiDurationMs = usage.Sum(u => u.DurationMs),
                    SessionDurationMs = sessionDurationMs,
                    HasSessionDuration = sessionDurationMs > 0,
                    HasTelemetry = usage.Count > 0,
                };

                // Telemetry logs rotate/expire, but events.jsonl is the authoritative record. Fall
                // back to it so the list mirrors what the detail page derives (turns, API calls,
                // output tokens). AIU/cost/API-time need telemetry, so leave those blank when absent.
                if (!row.HasTelemetry && eventSummary.HasEvents)
                {
                    if (turnCount == 0)
                        row.TurnCount = eventSummary.UserMessageCount;
                    row.ApiCallCount = eventSummary.AssistantMessageCount;
                    row.TotalTokens = eventSummary.OutputTokens;
                }

                row.HasStats = row.HasTelemetry || row.ApiCallCount > 0 || row.TotalTokens > 0;
                return row;
            })
            .ToList();

        return rows
            // Hide empty sessions (no recorded turns and no token telemetry) — these are
            // usually aborted/never-used sessions that only carry a folder-name label.
            .Where(r => r.TurnCount > 0 || r.HasStats)
            .OrderByDescending(r => r.Session.UpdatedAt ?? r.Session.CreatedAt ?? DateTimeOffset.MinValue)
            .ToList();
    }

    public SessionAnalysis? Analyze(string sessionId)
    {
        var session = _store.GetSession(sessionId);
        if (session is null) return null;

        var turns = _store.GetTurns(sessionId);
        var checkpoints = _store.GetCheckpoints(sessionId);
        var tele = _telemetry.GetForSession(sessionId);
        var events = _events.GetForSession(sessionId);
        var metadataSources = EnrichSessionFromEvents(session, events);

        var analysis = new SessionAnalysis { Session = session };
        analysis.Checkpoints.AddRange(checkpoints);
        analysis.ContextSamples.AddRange(tele.ContextSamples.OrderBy(s => s.Timestamp ?? DateTimeOffset.MaxValue));

        // Prefer the authoritative session-state event stream (real conversation text).
        // Fall back to telemetry-only reconstruction when events.jsonl is unavailable.
        if (events.Count > 0)
            BuildTimelineFromEvents(analysis, events, tele);
        else
            BuildTimeline(analysis, turns, tele);
        analysis.SessionDurationMs = TimelineSessionDurationMs(analysis);
        if (analysis.SessionDurationMs <= 0)
            analysis.SessionDurationMs = SessionDurationMs(events, tele, session);
        analysis.DataSources = BuildDataSources(events, tele, metadataSources);
        analysis.Suggestions.AddRange(GenerateSuggestions(analysis));
        return analysis;
    }

    private static List<string> EnrichSessionFromEvents(SessionInfo session, List<SessionEvent> events)
    {
        var sources = new List<string>();
        var context = events.FirstOrDefault(e => e.Type == "session.resume"
            && (!string.IsNullOrWhiteSpace(e.Cwd)
                || !string.IsNullOrWhiteSpace(e.Repository)
                || !string.IsNullOrWhiteSpace(e.Branch)
                || !string.IsNullOrWhiteSpace(e.HostType)));
        if (context is null) return sources;

        var source = context.Source ?? "session.resume context";
        if (string.IsNullOrWhiteSpace(session.Cwd) && !string.IsNullOrWhiteSpace(context.Cwd))
        {
            session.Cwd = context.Cwd;
            sources.Add(source);
        }
        if (string.IsNullOrWhiteSpace(session.Repository) && !string.IsNullOrWhiteSpace(context.Repository))
        {
            session.Repository = context.Repository;
            sources.Add(source);
        }
        if (string.IsNullOrWhiteSpace(session.Branch) && !string.IsNullOrWhiteSpace(context.Branch))
        {
            session.Branch = context.Branch;
            sources.Add(source);
        }
        if (string.IsNullOrWhiteSpace(session.HostType) && !string.IsNullOrWhiteSpace(context.HostType))
        {
            session.HostType = context.HostType;
            sources.Add(source);
        }
        return sources.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void EnrichSessionFromSummary(SessionInfo session, SessionEventSummary summary)
    {
        if (!summary.HasEvents) return;
        if (string.IsNullOrWhiteSpace(session.Cwd) && !string.IsNullOrWhiteSpace(summary.Cwd))
            session.Cwd = summary.Cwd;
        if (string.IsNullOrWhiteSpace(session.Repository) && !string.IsNullOrWhiteSpace(summary.Repository))
            session.Repository = summary.Repository;
        if (string.IsNullOrWhiteSpace(session.Branch) && !string.IsNullOrWhiteSpace(summary.Branch))
            session.Branch = summary.Branch;
        if (string.IsNullOrWhiteSpace(session.HostType) && !string.IsNullOrWhiteSpace(summary.HostType))
            session.HostType = summary.HostType;
    }

    private static SessionDataSources BuildDataSources(List<SessionEvent> events, SessionTelemetry tele, List<string> metadataSources)
    {
        var eventApiIds = events
            .Where(e => e.Type == "assistant.message" && !string.IsNullOrWhiteSpace(e.ApiCallId))
            .Select(e => e.ApiCallId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var telemetryApiIds = tele.Usage
            .Where(u => !string.IsNullOrWhiteSpace(u.ApiCallId))
            .Select(u => u.ApiCallId!)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        return new SessionDataSources
        {
            ConversationSources = Sources(events.Select(e => e.Source)),
            TelemetrySources = Sources(tele.Usage.Select(u => u.Source)
                .Concat(tele.ToolCalls.Select(t => t.Source))
                .Concat(tele.ContextSamples.Select(c => c.Source))),
            MetadataSources = Sources(metadataSources),
            ApiCallsInEvents = eventApiIds.Count,
            ApiCallsWithTelemetry = eventApiIds.Count > 0
                ? eventApiIds.Count(telemetryApiIds.Contains)
                : tele.Usage.Count,
            ToolCallsInEvents = events.Count(e => e.Type == "tool.execution_complete"),
            ToolCallsWithTelemetry = tele.ToolCalls.Count,
        };
    }

    private static List<string> Sources(IEnumerable<string?> sources) =>
        sources
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static double TimelineSessionDurationMs(SessionAnalysis analysis)
    {
        double total = 0;
        foreach (var turn in analysis.Timeline)
        {
            if (!turn.StartTime.HasValue) continue;
            var end = turn.EndTime
                ?? turn.Steps.Select(s => s.Time).OfType<DateTimeOffset>().DefaultIfEmpty(turn.StartTime.Value).Max();
            var duration = (end - turn.StartTime.Value).TotalMilliseconds;
            if (duration > 0) total += duration;
        }
        return total;
    }

    private static double SessionDurationMs(List<SessionEvent> events, SessionTelemetry? tele, SessionInfo session)
    {
        var turnDuration = SumTurnDurationMs(events);
        if (turnDuration > 0) return turnDuration;

        var eventTimes = events
            .Select(e => e.Timestamp)
            .OfType<DateTimeOffset>()
            .Order()
            .ToList();
        if (eventTimes.Count >= 2)
        {
            var duration = (eventTimes[^1] - eventTimes[0]).TotalMilliseconds;
            if (duration > 0) return duration;
        }

        if (tele?.SessionDurationMs is > 0)
            return tele.SessionDurationMs.Value;

        if (session.CreatedAt.HasValue && session.UpdatedAt.HasValue)
        {
            var duration = (session.UpdatedAt.Value - session.CreatedAt.Value).TotalMilliseconds;
            if (duration > 0) return duration;
        }

        return 0;
    }

    private static double SessionDurationMs(SessionEventSummary summary, SessionTelemetry? tele, SessionInfo session)
    {
        if (summary.SessionDurationMs > 0) return summary.SessionDurationMs;

        if (tele?.SessionDurationMs is > 0)
            return tele.SessionDurationMs.Value;

        if (session.CreatedAt.HasValue && session.UpdatedAt.HasValue)
        {
            var duration = (session.UpdatedAt.Value - session.CreatedAt.Value).TotalMilliseconds;
            if (duration > 0) return duration;
        }

        return 0;
    }

    private static double SumTurnDurationMs(List<SessionEvent> events)
    {
        DateTimeOffset? turnStart = null;
        DateTimeOffset? lastInTurn = null;
        double total = 0;

        foreach (var ev in events.OrderBy(e => e.Timestamp ?? DateTimeOffset.MaxValue))
        {
            if (ev.Timestamp is null) continue;

            if (ev.Type == "user.message" && !string.IsNullOrWhiteSpace(ev.Content))
            {
                if (turnStart.HasValue && lastInTurn.HasValue)
                {
                    var duration = (lastInTurn.Value - turnStart.Value).TotalMilliseconds;
                    if (duration > 0) total += duration;
                }

                turnStart = ev.Timestamp.Value;
                lastInTurn = ev.Timestamp.Value;
                continue;
            }

            if (turnStart.HasValue && IsTurnActivityEvent(ev))
                lastInTurn = ev.Timestamp.Value;
        }

        if (turnStart.HasValue && lastInTurn.HasValue)
        {
            var duration = (lastInTurn.Value - turnStart.Value).TotalMilliseconds;
            if (duration > 0) total += duration;
        }

        return total;
    }

    private static bool IsTurnActivityEvent(SessionEvent ev) =>
        ev.Type is "assistant.message" or "tool.execution_complete";

    /// <summary>
    /// Reconstructs the timeline from <c>events.jsonl</c> — the authoritative, fully-ordered
    /// event stream that carries real conversation text. Each turn is delimited by a non-empty
    /// <c>user.message</c>. Per-call token/AIU/cost detail is joined from telemetry
    /// <c>assistant_usage</c> by <c>apiCallId == api_call_id</c> so charts and cost rollups
    /// continue to work unchanged.
    /// </summary>
    private static void BuildTimelineFromEvents(SessionAnalysis analysis, List<SessionEvent> events, SessionTelemetry tele)
    {
        var usageByApiCall = tele.Usage
            .Where(u => !string.IsNullOrEmpty(u.ApiCallId))
            .GroupBy(u => u.ApiCallId!)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // Pending tool invocations (from assistant.message.toolRequests or tool.execution_start),
        // keyed by toolCallId, so we can attach the matching result on tool.execution_complete.
        var pendingTools = new Dictionary<string, ToolCallEvent>(StringComparer.Ordinal);

        TimelineTurn? current = null;
        TimelineTurn EnsureTurn(DateTimeOffset? time)
        {
            if (current is null)
            {
                // Telemetry/events that precede the first user.message (rare) get a turn 0.
                current = new TimelineTurn { TurnIndex = analysis.Timeline.Count, StartTime = time, EndTime = time };
                analysis.Timeline.Add(current);
            }
            return current;
        }

        foreach (var ev in events)
        {
            switch (ev.Type)
            {
                case "user.message":
                    if (string.IsNullOrWhiteSpace(ev.Content)) break; // skip empty continuations
                    current = new TimelineTurn
                    {
                        TurnIndex = analysis.Timeline.Count,
                        StartTime = ev.Timestamp,
                        EndTime = ev.Timestamp,
                        UserMessage = ev.Content,
                    };
                    analysis.Timeline.Add(current);
                    break;

                case "assistant.message":
                {
                    var turn = EnsureTurn(ev.Timestamp);
                    if (ev.Timestamp is not null) turn.EndTime = ev.Timestamp;
                    turn.Steps.Add(new TurnStep
                    {
                        Time = ev.Timestamp,
                        IsTool = false,
                        Content = ev.Content,
                        ReasoningText = ev.ReasoningText,
                        Model = ev.Model,
                        ApiCallId = ev.ApiCallId,
                        Source = ev.Source,
                        OutputTokens = ev.OutputTokens,
                        ToolRequestCount = ev.ToolRequests.Count,
                        ContentLength = ev.Content?.Length ?? 0,
                    });

                    // Join per-call cost detail from telemetry; this is what feeds token/AIU/
                    // cost rollups and the charts. Fall back to a minimal usage record (with
                    // the message's own outputTokens) when no telemetry match exists.
                    if (ev.ApiCallId is not null && usageByApiCall.TryGetValue(ev.ApiCallId, out var usage))
                    {
                        turn.Actions.Add(usage);
                    }
                    else if (ev.OutputTokens > 0 || ev.Model is not null)
                    {
                        turn.Actions.Add(new AssistantUsageEvent
                        {
                            SessionId = analysis.Session.Id,
                            Source = ev.Source,
                            Timestamp = ev.Timestamp,
                            ApiCallId = ev.ApiCallId,
                            Model = ev.Model,
                            OutputTokens = ev.OutputTokens,
                        });
                    }

                    // Seed pending tool calls from the requests embedded in this message.
                    foreach (var tr in ev.ToolRequests)
                    {
                        if (string.IsNullOrEmpty(tr.ToolCallId)) continue;
                        pendingTools[tr.ToolCallId!] = new ToolCallEvent
                        {
                            SessionId = analysis.Session.Id,
                            Source = ev.Source,
                            Timestamp = ev.Timestamp,
                            ApiCallId = ev.ApiCallId,
                            ToolName = tr.Name,
                            Arguments = tr.Arguments,
                            Model = ev.Model,
                        };
                    }
                    break;
                }

                case "tool.execution_start":
                {
                    if (string.IsNullOrEmpty(ev.ToolCallId)) break;
                    if (!pendingTools.TryGetValue(ev.ToolCallId!, out var tc))
                    {
                        tc = new ToolCallEvent
                        {
                            SessionId = analysis.Session.Id,
                            Source = ev.Source,
                            ApiCallId = ev.ApiCallId,
                            ToolName = ev.ToolName,
                            Model = ev.Model,
                        };
                        pendingTools[ev.ToolCallId!] = tc;
                    }
                    tc.Timestamp ??= ev.Timestamp;
                    tc.ToolName ??= ev.ToolName;
                    if (string.IsNullOrEmpty(tc.Arguments)) tc.Arguments = ev.Arguments;
                    break;
                }

                case "tool.execution_complete":
                {
                    if (string.IsNullOrEmpty(ev.ToolCallId)) break;
                    pendingTools.TryGetValue(ev.ToolCallId!, out var tc);
                    tc ??= new ToolCallEvent
                    {
                        SessionId = analysis.Session.Id,
                        Source = ev.Source,
                        Timestamp = ev.Timestamp,
                        ToolName = ev.ToolName,
                        Model = ev.Model,
                    };
                    tc.ResultType = ev.Success == false ? "FAILURE" : "SUCCESS";
                    // Prefer the telemetry-reported duration; many tools (e.g. shell) omit it,
                    // so fall back to the wall-clock delta between start and complete events.
                    double durationMs = ev.DurationMs;
                    if (durationMs <= 0 && tc.Timestamp.HasValue && ev.Timestamp.HasValue)
                    {
                        var delta = (ev.Timestamp.Value - tc.Timestamp.Value).TotalMilliseconds;
                        if (delta > 0) durationMs = delta;
                    }
                    tc.DurationMs = durationMs;
                    tc.ResultLength = ev.ResultLength;
                    tc.IsMcp = ev.IsMcp;
                    pendingTools.Remove(ev.ToolCallId!);

                    var turn = EnsureTurn(ev.Timestamp);
                    if (ev.Timestamp is not null) turn.EndTime = ev.Timestamp;

                    // The `task_complete` tool's result IS the turn's final reply — surface it
                    // as the agent's concluding message rather than burying it in the op stream.
                    if (string.Equals(tc.ToolName, "task_complete", StringComparison.OrdinalIgnoreCase))
                    {
                        var summary = !string.IsNullOrWhiteSpace(ev.ResultContent)
                            ? ev.ResultContent
                            : ExtractSummaryArg(tc.Arguments);
                        if (!string.IsNullOrWhiteSpace(summary))
                            turn.AssistantResponse = summary;
                        break;
                    }

                    turn.ToolCalls.Add(tc);
                    turn.Steps.Add(new TurnStep
                    {
                        Time = ev.Timestamp ?? tc.Timestamp,
                        IsTool = true,
                        ToolName = tc.ToolName,
                        Arguments = tc.Arguments,
                        Command = tc.Command,
                        ResultType = tc.ResultType,
                        ResultContent = ev.ResultContent,
                        Success = ev.Success,
                        ErrorMessage = ev.ErrorMessage,
                        ErrorCode = ev.ErrorCode,
                        DurationMs = durationMs,
                        ResultLength = ev.ResultLength,
                        IsMcp = ev.IsMcp,
                        Source = ev.Source,
                        Failed = ev.Success == false,
                        Model = tc.Model,
                    });
                    break;
                }
            }
        }

        foreach (var turn in analysis.Timeline)
        {
            turn.Actions.Sort((x, y) => Nullable.Compare(x.Timestamp, y.Timestamp));
            turn.ToolCalls.Sort((x, y) => Nullable.Compare(x.Timestamp, y.Timestamp));
            turn.Steps.Sort((x, y) => Nullable.Compare(x.Time, y.Time));
        }
    }

    private static void BuildTimeline(SessionAnalysis analysis, List<SessionTurn> turns, SessionTelemetry tele)
    {
        // Build time-ordered turn boundaries. We bucket telemetry by ABSOLUTE timestamp
        // because the telemetry "turn_id" is an assistant-turn counter that resets per CLI
        // process and is unrelated to the global DB turn_index (a session can span several
        // processes/resumes). Timestamps are absolute and bucket cleanly across processes.
        //
        // Each boundary has a sequential Bucket (its order) and a Label shown in the UI.
        var dbTurns = turns
            .Where(t => t.Timestamp is not null)
            .OrderBy(t => t.Timestamp)
            .ToList();

        // True prompt-send times come from the `user_message` telemetry events. The DB turn
        // timestamp can be a finalization time (when the assistant response was flushed), which
        // misplaces a turn's work into the previous turn. We therefore prefer telemetry prompt
        // times for the boundaries and align them by ORDER to the DB turns (for text + labels).
        var prompts = tele.UserPrompts
            .Where(p => p.Timestamp is not null && p.ContentLength > 0)
            .OrderBy(p => p.Timestamp)
            .ToList();

        List<(DateTimeOffset Start, int Bucket, int Label, SessionTurn? Turn)> boundaries;
        bool hasRealTurns = false;
        if (prompts.Count > 0)
        {
            hasRealTurns = true;
            // Primary: real user prompts (accurate send times) aligned by order to DB turns.
            boundaries = prompts
                .Select((p, i) =>
                {
                    var turn = i < dbTurns.Count ? dbTurns[i] : null;
                    return (Start: p.Timestamp!.Value, Bucket: i, Label: turn?.TurnIndex ?? i, Turn: turn);
                })
                .ToList();
        }
        else if (dbTurns.Count > 0)
        {
            hasRealTurns = true;
            // Fallback: real user-prompt turns from the session store (timestamps less precise).
            boundaries = dbTurns
                .Select((t, i) => (Start: t.Timestamp!.Value, Bucket: i, Label: t.TurnIndex, Turn: (SessionTurn?)t))
                .ToList();
        }
        else
        {
            // Last resort: derive pseudo-turns from assistant_turn_start markers, renumbered
            // sequentially in time order (never trust the raw, per-process turn_id here).
            boundaries = tele.TurnStarts
                .Where(b => b.Timestamp is not null)
                .OrderBy(b => b.Timestamp)
                .Select((b, i) => (Start: b.Timestamp!.Value, Bucket: i, Label: i, Turn: (SessionTurn?)null))
                .ToList();
        }

        var byBucket = new Dictionary<int, TimelineTurn>();
        TimelineTurn BucketTurn(int bucket)
        {
            if (!byBucket.TryGetValue(bucket, out var tl))
            {
                var b = boundaries[bucket];
                tl = new TimelineTurn
                {
                    TurnIndex = b.Label,
                    StartTime = b.Start,
                    EndTime = b.Start,
                    UserMessage = b.Turn?.UserMessage,
                    AssistantResponse = b.Turn?.AssistantResponse,
                };
                byBucket[bucket] = tl;
            }
            return tl;
        }

        // Ensure every real turn boundary appears, even if it has no telemetry of its own.
        if (hasRealTurns)
            for (int i = 0; i < boundaries.Count; i++) BucketTurn(i);

        foreach (var u in tele.Usage.OrderBy(u => u.Timestamp ?? DateTimeOffset.MaxValue))
        {
            int b = ResolveBucket(u.Timestamp, boundaries);
            if (b < 0) continue;
            var turn = BucketTurn(b);
            turn.Actions.Add(u);
            if (u.Timestamp is not null) turn.EndTime = MaxTime(turn.EndTime, u.Timestamp);
        }
        foreach (var tc in tele.ToolCalls.OrderBy(t => t.Timestamp ?? DateTimeOffset.MaxValue))
        {
            int b = ResolveBucket(tc.Timestamp, boundaries);
            if (b < 0) continue;
            var turn = BucketTurn(b);
            turn.ToolCalls.Add(tc);
            if (tc.Timestamp is not null)
                turn.EndTime = MaxTime(turn.EndTime, tc.Timestamp.Value.AddMilliseconds(Math.Max(0, tc.DurationMs)));
        }
        var msgsByBucket = new Dictionary<int, List<AssistantMessageEvent>>();
        foreach (var m in tele.Messages.OrderBy(m => m.Timestamp ?? DateTimeOffset.MaxValue))
        {
            int b = ResolveBucket(m.Timestamp, boundaries);
            if (b < 0) continue;
            var turn = BucketTurn(b);
            if (m.Timestamp is not null) turn.EndTime = MaxTime(turn.EndTime, m.Timestamp);
            (msgsByBucket.TryGetValue(b, out var l) ? l : msgsByBucket[b] = new()).Add(m);
        }

        foreach (var bucket in byBucket.Keys.OrderBy(k => k))
        {
            var tl = byBucket[bucket];
            tl.Actions.Sort((x, y) => Nullable.Compare(x.Timestamp, y.Timestamp));
            tl.ToolCalls.Sort((x, y) => Nullable.Compare(x.Timestamp, y.Timestamp));
            var msgs = msgsByBucket.TryGetValue(bucket, out var ml) ? ml : new List<AssistantMessageEvent>();
            BuildSteps(tl, msgs);
            tl.StartTime ??= tl.Steps.FirstOrDefault()?.Time
                ?? tl.Actions.FirstOrDefault()?.Timestamp
                ?? tl.ToolCalls.FirstOrDefault()?.Timestamp;
            tl.EndTime ??= tl.Steps.LastOrDefault()?.Time
                ?? tl.Actions.LastOrDefault()?.Timestamp
                ?? tl.ToolCalls.LastOrDefault()?.Timestamp
                ?? tl.StartTime;
            analysis.Timeline.Add(tl);
        }
    }

    private static DateTimeOffset? MaxTime(DateTimeOffset? current, DateTimeOffset? candidate)
        => current is null || (candidate is not null && candidate > current) ? candidate : current;

    /// <summary>Pulls the <c>summary</c> field out of a task_complete tool's JSON arguments.</summary>
    private static string? ExtractSummaryArg(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("summary", out var s)
                && s.ValueKind == System.Text.Json.JsonValueKind.String)
                return s.GetString();
        }
        catch { /* arguments may not be valid JSON */ }
        return null;
    }

    private static void BuildSteps(TimelineTurn tl, List<AssistantMessageEvent> messages)
    {
        foreach (var m in messages)
        {
            tl.Steps.Add(new TurnStep
            {
                Time = m.Timestamp,
                IsTool = false,
                Phase = m.Phase,
                ToolRequestCount = m.ToolRequestCount,
                ContentLength = m.ContentLength,
                Model = m.Model,
                Source = m.Source,
            });
        }
        foreach (var c in tl.ToolCalls)
        {
            tl.Steps.Add(new TurnStep
            {
                Time = c.Timestamp,
                IsTool = true,
                ToolName = c.ToolName,
                Command = c.Command,
                Arguments = c.Arguments,
                ResultType = c.ResultType,
                DurationMs = c.DurationMs,
                ResultLength = c.ResultLength,
                IsMcp = c.IsMcp,
                Failed = c.Failed,
                Source = c.Source,
            });
        }
        tl.Steps.Sort((x, y) => Nullable.Compare(x.Time, y.Time));
    }

    // Returns the index of the latest boundary whose start time is <= the event time.
    // Events before the first boundary fall into bucket 0. Returns -1 if no boundaries.
    private static int ResolveBucket(DateTimeOffset? time,
        List<(DateTimeOffset Start, int Bucket, int Label, SessionTurn? Turn)> boundaries)
    {
        if (boundaries.Count == 0) return -1;
        if (time is null) return boundaries.Count - 1;
        int chosen = 0;
        for (int i = 0; i < boundaries.Count; i++)
        {
            if (boundaries[i].Start <= time.Value) chosen = i;
            else break;
        }
        return chosen;
    }

    private static IEnumerable<Suggestion> GenerateSuggestions(SessionAnalysis a)
    {
        var suggestions = new List<Suggestion>();

        if (!a.HasTelemetry)
        {
            suggestions.Add(new Suggestion
            {
                Severity = "info",
                Title = "No token telemetry for this session",
                Detail = "Detailed token/AI-unit data was not found in the local logs (logs may have rotated). " +
                         "Only the conversation could be reconstructed. Cost suggestions require telemetry.",
            });
            return suggestions;
        }

        long totalInput = a.TotalInputTokens;
        long totalOutput = a.TotalOutputTokens;
        long totalReasoning = a.TotalReasoningTokens;
        double avgCallsPerTurn = a.TurnCount > 0 ? (double)a.ApiCallCount / a.TurnCount : a.ApiCallCount;
        double avgDuration = a.ApiCallCount > 0 ? a.TotalApiDurationMs / a.ApiCallCount : 0;

        // 1. Context window pressure.
        if (a.ContextLimit > 0)
        {
            double ctxPct = (double)a.PeakContextTokens / a.ContextLimit;
            if (ctxPct >= 0.8)
            {
                suggestions.Add(new Suggestion
                {
                    Severity = "high",
                    Title = $"Context window peaked at {ctxPct:P0} of the limit",
                    Detail = $"Peak context was {a.PeakContextTokens:N0} / {a.ContextLimit:N0} tokens. The full context is " +
                             "re-sent on every model call, so a large window multiplies cost across the whole session. " +
                             "Start a fresh session for unrelated work, use /compact, and avoid pasting large files.",
                });
            }
            else if (ctxPct >= 0.6)
            {
                suggestions.Add(new Suggestion
                {
                    Severity = "warning",
                    Title = $"Context window grew to {ctxPct:P0} of the limit",
                    Detail = $"Peak context was {a.PeakContextTokens:N0} tokens. Consider splitting long sessions to keep " +
                             "the re-sent context (and per-call cost) smaller.",
                });
            }
        }

        // 2. Input/output ratio (context re-send dominates cost).
        if (totalOutput > 0)
        {
            double ratio = (double)totalInput / totalOutput;
            if (ratio > 25)
            {
                suggestions.Add(new Suggestion
                {
                    Severity = "warning",
                    Title = $"Input tokens dominate ({ratio:N0}x output)",
                    Detail = $"{totalInput:N0} input vs {totalOutput:N0} output tokens. Most spend is re-sent context, not new " +
                             "generation. Break large tasks into focused sessions and avoid loading big files/whole directories.",
                });
            }
        }

        // 3. Prompt cache reuse.
        if (totalInput > 200_000)
        {
            double cacheRatio = (double)a.CacheReadTokens / totalInput;
            if (cacheRatio < 0.15)
            {
                suggestions.Add(new Suggestion
                {
                    Severity = "warning",
                    Title = $"Low prompt-cache reuse ({cacheRatio:P0})",
                    Detail = $"Only {a.CacheReadTokens:N0} of {totalInput:N0} input tokens were served from cache. Rapidly changing " +
                             "context defeats caching. Keep related steps contiguous and avoid editing earlier context to benefit " +
                             "from cached prefixes.",
                });
            }
        }

        // 4. Reasoning token share.
        if (totalOutput > 0)
        {
            double reasonShare = (double)totalReasoning / totalOutput;
            bool highEffort = a.Timeline.SelectMany(t => t.Actions)
                .Any(x => x.ReasoningEffort is "high" or "xhigh");
            if (reasonShare > 0.5 && highEffort)
            {
                suggestions.Add(new Suggestion
                {
                    Severity = "info",
                    Title = $"Reasoning tokens are {reasonShare:P0} of output",
                    Detail = "High reasoning effort accounts for a large share of generated tokens. For routine edits and lookups, " +
                             "a lower reasoning effort or a lighter model reduces cost with little quality loss.",
                });
            }
        }

        // 5. Expensive model usage.
        var models = a.ModelBreakdowns.ToList();
        var heavy = models.FirstOrDefault(m =>
            (m.Model.Contains("opus", StringComparison.OrdinalIgnoreCase) ||
             m.Model.Contains("gpt-5.5", StringComparison.OrdinalIgnoreCase)) &&
            m.Aiu > a.TotalAiu * 0.5 && m.Calls >= 5);
        if (heavy is not null)
        {
            suggestions.Add(new Suggestion
            {
                Severity = "info",
                Title = $"Premium model '{heavy.Model}' drives most of the cost",
                Detail = $"{heavy.Model} used {heavy.Calls} calls and {heavy.Aiu:N1} AIU. Route routine steps (search, simple edits, " +
                         "summaries) to a lighter model such as a Haiku/Mini/Flash tier, reserving the premium model for hard reasoning.",
            });
        }

        // 6. Many tool iterations per turn.
        if (avgCallsPerTurn > 15)
        {
            suggestions.Add(new Suggestion
            {
                Severity = "info",
                Title = $"High tool-iteration count (~{avgCallsPerTurn:N0} calls/turn)",
                Detail = "The agent makes many model round-trips per turn. Clearer, more specific instructions and providing the " +
                         "right files up front can reduce exploratory iterations and total tokens.",
            });
        }

        // 7. Latency note.
        if (avgDuration > 20_000)
        {
            suggestions.Add(new Suggestion
            {
                Severity = "info",
                Title = $"High average call latency (~{avgDuration / 1000:N0}s)",
                Detail = "Large prompts and high reasoning effort increase latency. Reducing context size and reasoning effort also " +
                         "speeds up responses.",
            });
        }

        // 8. Tool failures (each retry re-sends context and burns tokens).
        if (a.TotalToolCalls >= 15)
        {
            var failures = a.ToolBreakdowns.Sum(t => t.Failures);
            double failRate = (double)failures / a.TotalToolCalls;
            if (failRate > 0.2)
            {
                var worst = a.ToolBreakdowns.Where(t => t.Failures > 0)
                    .OrderByDescending(t => t.Failures).FirstOrDefault();
                suggestions.Add(new Suggestion
                {
                    Severity = "warning",
                    Title = $"{failRate:P0} of tool calls failed ({failures}/{a.TotalToolCalls})",
                    Detail = $"Failed operations are retried, and every retry re-sends the whole context. " +
                             (worst is not null ? $"'{worst.ToolName}' failed most often ({worst.Failures}x). " : "") +
                             "Giving correct paths/commands up front avoids these wasted round-trips.",
                });
            }
        }

        if (suggestions.Count == 0)
        {
            suggestions.Add(new Suggestion
            {
                Severity = "info",
                Title = "No major cost concerns detected",
                Detail = "This session's token, context-window and model usage look efficient. Keep sessions focused to maintain this.",
            });
        }

        return suggestions
            .OrderBy(s => s.Severity switch { "high" => 0, "warning" => 1, _ => 2 })
            .ToList();
    }
}
