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

        var rows = new List<SessionSummaryRow>(sessions.Count);
        foreach (var s in sessions)
        {
            telemetry.TryGetValue(s.Id, out var tele);
            var usage = tele?.Usage ?? new List<AssistantUsageEvent>();
            rows.Add(new SessionSummaryRow
            {
                Session = s,
                TurnCount = turnCounts.TryGetValue(s.Id, out var tc) ? tc : 0,
                ApiCallCount = usage.Count,
                TotalTokens = usage.Sum(u => u.TotalTokens),
                TotalAiu = usage.Sum(u => u.Aiu),
                TotalCost = usage.Sum(u => u.Cost),
                TotalApiDurationMs = usage.Sum(u => u.DurationMs),
                HasTelemetry = usage.Count > 0,
            });
        }

        return rows
            // Hide empty sessions (no recorded turns and no token telemetry) — these are
            // usually aborted/never-used sessions that only carry a folder-name label.
            .Where(r => r.TurnCount > 0 || r.HasTelemetry)
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

        var analysis = new SessionAnalysis { Session = session };
        analysis.Checkpoints.AddRange(checkpoints);
        analysis.ContextSamples.AddRange(tele.ContextSamples.OrderBy(s => s.Timestamp ?? DateTimeOffset.MaxValue));

        // Prefer the authoritative session-state event stream (real conversation text).
        // Fall back to telemetry-only reconstruction when events.jsonl is unavailable.
        if (events.Count > 0)
            BuildTimelineFromEvents(analysis, events, tele);
        else
            BuildTimeline(analysis, turns, tele);
        analysis.Suggestions.AddRange(GenerateSuggestions(analysis));
        return analysis;
    }

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
                current = new TimelineTurn { TurnIndex = analysis.Timeline.Count, StartTime = time };
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
                        UserMessage = ev.Content,
                    };
                    analysis.Timeline.Add(current);
                    break;

                case "assistant.message":
                {
                    var turn = EnsureTurn(ev.Timestamp);
                    turn.Steps.Add(new TurnStep
                    {
                        Time = ev.Timestamp,
                        IsTool = false,
                        Content = ev.Content,
                        ReasoningText = ev.ReasoningText,
                        Model = ev.Model,
                        ApiCallId = ev.ApiCallId,
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
                        Timestamp = ev.Timestamp,
                        ToolName = ev.ToolName,
                        Model = ev.Model,
                    };
                    tc.ResultType = ev.Success == false ? "FAILURE" : "SUCCESS";
                    tc.DurationMs = ev.DurationMs;
                    tc.ResultLength = ev.ResultLength;
                    tc.IsMcp = ev.IsMcp;
                    pendingTools.Remove(ev.ToolCallId!);

                    var turn = EnsureTurn(ev.Timestamp);

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
                        DurationMs = ev.DurationMs,
                        ResultLength = ev.ResultLength,
                        IsMcp = ev.IsMcp,
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
            BucketTurn(b).Actions.Add(u);
        }
        foreach (var tc in tele.ToolCalls.OrderBy(t => t.Timestamp ?? DateTimeOffset.MaxValue))
        {
            int b = ResolveBucket(tc.Timestamp, boundaries);
            if (b < 0) continue;
            BucketTurn(b).ToolCalls.Add(tc);
        }
        var msgsByBucket = new Dictionary<int, List<AssistantMessageEvent>>();
        foreach (var m in tele.Messages.OrderBy(m => m.Timestamp ?? DateTimeOffset.MaxValue))
        {
            int b = ResolveBucket(m.Timestamp, boundaries);
            if (b < 0) continue;
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
            analysis.Timeline.Add(tl);
        }
    }

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
