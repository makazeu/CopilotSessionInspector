namespace CopilotSessionInspector.Models;

/// <summary>A tool/operation execution (tool_call_executed telemetry event).</summary>
public sealed class ToolCallEvent
{
    public string SessionId { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public int TurnId { get; set; } = -1;
    public string? ApiCallId { get; set; }
    public string? ToolName { get; set; }
    public string? Command { get; set; }
    public string? Arguments { get; set; }
    public string? ResultType { get; set; }
    public string? Model { get; set; }
    public bool IsMcp { get; set; }
    public double DurationMs { get; set; }
    public long ResultLength { get; set; }

    public bool Failed => !string.Equals(ResultType, "SUCCESS", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrEmpty(ResultType);
}

/// <summary>A user prompt (user_message telemetry event). Carries timing, not the text.</summary>
public sealed class UserPromptEvent
{
    public string SessionId { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public int ContentLength { get; set; }
    public string? AgentMode { get; set; }
}

/// <summary>An assistant message (assistant_message telemetry event). Text is not logged; metadata only.</summary>
public sealed class AssistantMessageEvent
{
    public string SessionId { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public int TurnId { get; set; } = -1;
    public string? ApiCallId { get; set; }
    public string? Model { get; set; }
    public string? Phase { get; set; }
    public bool HasToolRequests { get; set; }
    public int ToolRequestCount { get; set; }
    public int ContentLength { get; set; }
}

/// <summary>One ordered step in a reconstructed turn: an agent message or a tool execution.</summary>
public sealed class TurnStep
{
    public DateTimeOffset? Time { get; set; }
    public bool IsTool { get; set; }

    // Tool execution
    public string? ToolName { get; set; }
    public string? Command { get; set; }
    public string? Arguments { get; set; }
    public string? ResultType { get; set; }
    public double DurationMs { get; set; }
    public long ResultLength { get; set; }
    public bool IsMcp { get; set; }
    public bool Failed { get; set; }

    // Agent message
    public string? Phase { get; set; }
    public int ToolRequestCount { get; set; }
    public int ContentLength { get; set; }
    public string? Model { get; set; }

    // Real conversation text (from session-state/<id>/events.jsonl).
    public string? Content { get; set; }        // assistant reply text
    public string? ReasoningText { get; set; }  // assistant reasoning text
    public long OutputTokens { get; set; }      // tokens produced by this message
    public string? ApiCallId { get; set; }
    public string? ResultContent { get; set; }  // tool result text
    public bool? Success { get; set; }          // tool success flag
    public string? ErrorMessage { get; set; }   // failure reason (tool.execution_complete.error.message)
    public string? ErrorCode { get; set; }      // failure code (…error.code)
}

/// <summary>A request to invoke a tool, embedded in an assistant.message.</summary>
public sealed class ToolRequestInfo
{
    public string? ToolCallId { get; set; }
    public string? Name { get; set; }
    public string? Arguments { get; set; }
}

/// <summary>
/// One parsed record from <c>~/.copilot/session-state/&lt;id&gt;/events.jsonl</c> — the
/// authoritative, fully-ordered session event stream that carries real conversation text.
/// </summary>
public sealed class SessionEvent
{
    public string Type { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }

    // assistant.message / user.message
    public string? Content { get; set; }
    public string? ReasoningText { get; set; }
    public string? Model { get; set; }
    public string? ApiCallId { get; set; }
    public string? TurnId { get; set; }
    public string? AgentMode { get; set; }
    public long OutputTokens { get; set; }
    public List<ToolRequestInfo> ToolRequests { get; } = new();

    // tool.execution_start / tool.execution_complete
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
    public string? Arguments { get; set; }
    public bool? Success { get; set; }
    public string? ResultContent { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorCode { get; set; }
    public double DurationMs { get; set; }
    public long ResultLength { get; set; }
    public bool IsMcp { get; set; }

    // session.resume context metadata
    public string? Cwd { get; set; }
    public string? Repository { get; set; }
    public string? Branch { get; set; }
    public string? HostType { get; set; }
}

/// <summary>Session metadata read from session-store.db.</summary>
public sealed class SessionInfo
{
    public string Id { get; set; } = "";
    public string? Cwd { get; set; }
    public string? Repository { get; set; }
    public string? Branch { get; set; }
    public string? Summary { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? HostType { get; set; }

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Summary) ? Summary!
        : !string.IsNullOrWhiteSpace(Cwd) ? System.IO.Path.GetFileName(Cwd!.TrimEnd('\\', '/'))
        : Id;
}

/// <summary>A conversation turn (user message + assistant response) from the turns table.</summary>
public sealed class SessionTurn
{
    public int TurnIndex { get; set; }
    public string? UserMessage { get; set; }
    public string? AssistantResponse { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
}

/// <summary>A checkpoint summary from the checkpoints table.</summary>
public sealed class SessionCheckpoint
{
    public int Number { get; set; }
    public string? Title { get; set; }
    public string? Overview { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
}

/// <summary>One model API call (assistant_usage telemetry event).</summary>
public sealed class AssistantUsageEvent
{
    public string SessionId { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public string? ApiCallId { get; set; }
    public string? Model { get; set; }
    public string? Initiator { get; set; }
    public string? ReasoningEffort { get; set; }
    public string? FinishReason { get; set; }

    public long InputTokens { get; set; }
    public long InputTokensUncached { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public long ReasoningTokens { get; set; }
    public long TotalNanoAiu { get; set; }
    public double Cost { get; set; }
    public double DurationMs { get; set; }
    public double TtftMs { get; set; }

    public long TotalTokens => InputTokens + OutputTokens;
    public double Aiu => TotalNanoAiu / 1_000_000_000.0;
}

/// <summary>A context-window usage sample (session_usage_info telemetry event).</summary>
public sealed class SessionUsageSample
{
    public string SessionId { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public long TokenLimit { get; set; }
    public long CurrentTokens { get; set; }
    public long SystemTokens { get; set; }
    public long ConversationTokens { get; set; }
    public long ToolDefinitionsTokens { get; set; }
    public bool IsInitial { get; set; }
}

/// <summary>Marks the start of an assistant turn (assistant_turn_start telemetry event).</summary>
public sealed class TurnBoundary
{
    public string SessionId { get; set; } = "";
    public int TurnId { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
}

/// <summary>All telemetry extracted from logs for a single session.</summary>
public sealed class SessionTelemetry
{
    public List<AssistantUsageEvent> Usage { get; } = new();
    public List<SessionUsageSample> ContextSamples { get; } = new();
    public List<TurnBoundary> TurnStarts { get; } = new();
    public List<ToolCallEvent> ToolCalls { get; } = new();
    public List<AssistantMessageEvent> Messages { get; } = new();
    public List<UserPromptEvent> UserPrompts { get; } = new();
    public double? SessionDurationMs { get; set; }
}

/// <summary>A reconstructed timeline entry combining a conversation turn with its telemetry.</summary>
public sealed class TimelineTurn
{
    public int TurnIndex { get; set; }
    public string? UserMessage { get; set; }
    public string? AssistantResponse { get; set; }
    public DateTimeOffset? StartTime { get; set; }
    public DateTimeOffset? EndTime { get; set; }
    public List<AssistantUsageEvent> Actions { get; } = new();
    public List<ToolCallEvent> ToolCalls { get; } = new();
    public List<TurnStep> Steps { get; } = new();

    public long InputTokens => Actions.Sum(a => a.InputTokens);
    public long OutputTokens => Actions.Sum(a => a.OutputTokens);
    public long ReasoningTokens => Actions.Sum(a => a.ReasoningTokens);
    public long TotalTokens => Actions.Sum(a => a.TotalTokens);
    public double Aiu => Actions.Sum(a => a.Aiu);
    public double Cost => Actions.Sum(a => a.Cost);
    public double DurationMs => Actions.Sum(a => a.DurationMs);
    public double ToolDurationMs => ToolCalls.Sum(t => t.DurationMs);
    public int ToolCallCount => ToolCalls.Count;
    public int AgentMessageCount => Steps.Count(s => !s.IsTool);
    public IEnumerable<string> Models => Actions.Select(a => a.Model ?? "?").Distinct();
    public IEnumerable<IGrouping<string, ToolCallEvent>> ToolsByName =>
        ToolCalls.GroupBy(t => t.ToolName ?? "?").OrderByDescending(g => g.Count());
}

public sealed class Suggestion
{
    public string Severity { get; set; } = "info"; // info | warning | high
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
}

/// <summary>Full per-session analysis used by the detail page.</summary>
public sealed class SessionAnalysis
{
    public SessionInfo Session { get; set; } = new();
    public List<TimelineTurn> Timeline { get; } = new();
    public List<SessionUsageSample> ContextSamples { get; } = new();
    public List<SessionCheckpoint> Checkpoints { get; } = new();
    public List<Suggestion> Suggestions { get; } = new();

    public bool HasTelemetry => Timeline.Any(t => t.Actions.Count > 0);

    public long TotalInputTokens => Timeline.Sum(t => t.InputTokens);
    public long TotalOutputTokens => Timeline.Sum(t => t.OutputTokens);
    public long TotalReasoningTokens => Timeline.Sum(t => t.ReasoningTokens);
    public long TotalTokens => Timeline.Sum(t => t.TotalTokens);
    public double TotalAiu => Timeline.Sum(t => t.Aiu);
    public double TotalCost => Timeline.Sum(t => t.Cost);
    public double SessionDurationMs { get; set; }
    public double TotalApiDurationMs => Timeline.Sum(t => t.DurationMs);
    public int ApiCallCount => Timeline.Sum(t => t.Actions.Count);
    public int TurnCount => Timeline.Count;

    public long CacheReadTokens => Timeline.SelectMany(t => t.Actions).Sum(a => a.CacheReadTokens);
    public long PeakContextTokens => ContextSamples.Count > 0 ? ContextSamples.Max(s => s.CurrentTokens) : 0;
    public long ContextLimit => ContextSamples.Count > 0 ? ContextSamples.Max(s => s.TokenLimit) : 0;

    public int TotalToolCalls => Timeline.Sum(t => t.ToolCallCount);
    public double TotalToolDurationMs => Timeline.Sum(t => t.ToolDurationMs);

    public IEnumerable<ToolBreakdown> ToolBreakdowns =>
        Timeline.SelectMany(t => t.ToolCalls)
            .GroupBy(c => c.ToolName ?? "?")
            .Select(g => new ToolBreakdown
            {
                ToolName = g.Key,
                Calls = g.Count(),
                Failures = g.Count(c => c.Failed),
                DurationMs = g.Sum(c => c.DurationMs),
            })
            .OrderByDescending(t => t.Calls);

    public IEnumerable<ModelBreakdown> ModelBreakdowns =>
        Timeline.SelectMany(t => t.Actions)
            .GroupBy(a => a.Model ?? "?")
            .Select(g => new ModelBreakdown
            {
                Model = g.Key,
                Calls = g.Count(),
                InputTokens = g.Sum(a => a.InputTokens),
                OutputTokens = g.Sum(a => a.OutputTokens),
                Aiu = g.Sum(a => a.Aiu),
                Cost = g.Sum(a => a.Cost),
                DurationMs = g.Sum(a => a.DurationMs),
            })
            .OrderByDescending(m => m.Aiu);
}

public sealed class ModelBreakdown
{
    public string Model { get; set; } = "";
    public int Calls { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public double Aiu { get; set; }
    public double Cost { get; set; }
    public double DurationMs { get; set; }
}

public sealed class ToolBreakdown
{
    public string ToolName { get; set; } = "";
    public int Calls { get; set; }
    public int Failures { get; set; }
    public double DurationMs { get; set; }
}

/// <summary>Row in the sessions list with rolled-up totals.</summary>
public sealed class SessionSummaryRow
{
    public SessionInfo Session { get; set; } = new();
    public int TurnCount { get; set; }
    public int ApiCallCount { get; set; }
    public long TotalTokens { get; set; }
    public double TotalAiu { get; set; }
    public double TotalCost { get; set; }
    public double TotalApiDurationMs { get; set; }
    public double SessionDurationMs { get; set; }
    public bool HasSessionDuration { get; set; }
    public bool HasTelemetry { get; set; }
    public bool HasStats { get; set; }
}
