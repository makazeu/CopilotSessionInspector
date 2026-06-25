using System.Text;
using System.Text.Json;
using CopilotSessionInspector.Models;

namespace CopilotSessionInspector.Services;

/// <summary>
/// Scans ~/.copilot/logs/process-*.log and extracts pretty-printed telemetry JSON
/// blocks (assistant_usage, session_usage_info, assistant_turn_start, session_shutdown),
/// grouping them by session_id. Results are cached until <see cref="Reload"/>.
/// </summary>
public sealed class TelemetryLogParser
{
    private readonly CopilotPaths _paths;
    private readonly ILogger<TelemetryLogParser> _logger;
    private readonly object _gate = new();
    private Dictionary<string, SessionTelemetry>? _cache;
    private DateTimeOffset _parsedAt;

    private static readonly HashSet<string> InterestingKinds = new()
    {
        "assistant_usage", "session_usage_info", "assistant_turn_start", "session_shutdown",
        "tool_call_executed", "assistant_message", "user_message",
    };

    public TelemetryLogParser(CopilotPaths paths, ILogger<TelemetryLogParser> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public DateTimeOffset ParsedAt => _parsedAt;

    public Dictionary<string, SessionTelemetry> GetAll()
    {
        lock (_gate)
        {
            if (_cache is null)
                _cache = ParseAllLogs();
            return _cache;
        }
    }

    public SessionTelemetry GetForSession(string sessionId)
        => GetAll().TryGetValue(sessionId, out var t) ? t : new SessionTelemetry();

    public void Reload()
    {
        lock (_gate)
        {
            _cache = ParseAllLogs();
        }
    }

    private Dictionary<string, SessionTelemetry> ParseAllLogs()
    {
        var result = new Dictionary<string, SessionTelemetry>(StringComparer.Ordinal);
        if (!Directory.Exists(_paths.LogsDir))
        {
            _parsedAt = DateTimeOffset.Now;
            return result;
        }

        var files = Directory.EnumerateFiles(_paths.LogsDir, "*.log");
        foreach (var file in files)
        {
            try
            {
                ParseFile(file, result);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed parsing telemetry log {File}", file);
            }
        }
        _parsedAt = DateTimeOffset.Now;
        return result;
    }

    private static void ParseFile(string file, Dictionary<string, SessionTelemetry> result)
    {
        using var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 1 << 16);

        DateTimeOffset? lastTimestamp = null;
        StringBuilder? block = null;
        int depth = 0;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (block is null)
            {
                // Track the most recent timestamped log line for event ordering.
                var ts = TryParseLeadingTimestamp(line);
                if (ts is not null) lastTimestamp = ts;

                // A telemetry payload starts with a bare "{" line.
                if (line.Length > 0 && line.Trim() == "{")
                {
                    block = new StringBuilder(256);
                    block.Append('{').Append('\n');
                    depth = 1;
                }
                continue;
            }

            block.Append(line).Append('\n');
            depth += NetBraceDelta(line);
            if (depth <= 0)
            {
                HandleBlock(block.ToString(), lastTimestamp, result);
                block = null;
                depth = 0;
            }
            else if (block.Length > 4_000_000)
            {
                // Not a telemetry block; abandon.
                block = null;
                depth = 0;
            }
        }
    }

    /// <summary>
    /// Net change in object-nesting depth for a JSON line, ignoring braces that appear
    /// inside string literals (tool arguments/commands routinely embed unbalanced braces).
    /// </summary>
    private static int NetBraceDelta(string s)
    {
        int delta = 0;
        bool inStr = false;
        bool esc = false;
        foreach (var ch in s)
        {
            if (inStr)
            {
                if (esc) esc = false;
                else if (ch == '\\') esc = true;
                else if (ch == '"') inStr = false;
            }
            else if (ch == '"') inStr = true;
            else if (ch == '{') delta++;
            else if (ch == '}') delta--;
        }
        return delta;
    }

    private static DateTimeOffset? TryParseLeadingTimestamp(string line)
    {
        // Lines look like: 2026-06-25T07:38:20.175Z [DEBUG] ...
        if (line.Length < 20) return null;
        if (!(char.IsDigit(line[0]) && line[4] == '-' && line[10] == 'T')) return null;
        int sp = line.IndexOf(' ');
        var token = sp > 0 ? line[..sp] : line;
        return DateTimeOffset.TryParse(token, null,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var dto) ? dto : null;
    }

    private static void HandleBlock(string json, DateTimeOffset? timestamp,
        Dictionary<string, SessionTelemetry> result)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { return; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("kind", out var kindEl) || kindEl.ValueKind != JsonValueKind.String) return;
            var kind = kindEl.GetString()!;
            if (!InterestingKinds.Contains(kind)) return;
            if (!root.TryGetProperty("session_id", out var sidEl) || sidEl.ValueKind != JsonValueKind.String) return;
            var sessionId = sidEl.GetString();
            if (string.IsNullOrEmpty(sessionId)) return;

            root.TryGetProperty("properties", out var props);
            root.TryGetProperty("metrics", out var metrics);

            // Prefer the event's own created_at; fall back to the preceding log line timestamp.
            var time = ParseRootTime(root) ?? timestamp;

            if (!result.TryGetValue(sessionId, out var tele))
            {
                tele = new SessionTelemetry();
                result[sessionId] = tele;
            }

            switch (kind)
            {
                case "assistant_usage":
                    tele.Usage.Add(new AssistantUsageEvent
                    {
                        SessionId = sessionId,
                        Timestamp = time,
                        ApiCallId = Str(props, "api_call_id"),
                        Model = Str(props, "model"),
                        Initiator = Str(props, "initiator"),
                        ReasoningEffort = Str(props, "reasoning_effort"),
                        FinishReason = Str(props, "finish_reason"),
                        InputTokens = Long(metrics, "input_tokens"),
                        InputTokensUncached = Long(metrics, "input_tokens_uncached"),
                        OutputTokens = Long(metrics, "output_tokens"),
                        CacheReadTokens = Long(metrics, "cache_read_tokens"),
                        CacheWriteTokens = Long(metrics, "cache_write_tokens"),
                        ReasoningTokens = Long(metrics, "reasoning_tokens"),
                        TotalNanoAiu = Long(metrics, "total_nano_aiu"),
                        Cost = Dbl(metrics, "cost"),
                        DurationMs = Dbl(metrics, "duration"),
                        TtftMs = Dbl(metrics, "ttft_ms"),
                    });
                    break;

                case "tool_call_executed":
                    tele.ToolCalls.Add(new ToolCallEvent
                    {
                        SessionId = sessionId,
                        Timestamp = time,
                        TurnId = IntFromString(Str(props, "turn_id")),
                        ApiCallId = Str(props, "api_call_id"),
                        ToolName = Str(props, "tool_name"),
                        Command = Str(props, "command"),
                        Arguments = Str(props, "arguments"),
                        ResultType = Str(props, "result_type"),
                        Model = Str(props, "model"),
                        IsMcp = string.Equals(Str(props, "is_mcp_tool"), "true", StringComparison.OrdinalIgnoreCase),
                        DurationMs = Dbl(metrics, "duration_ms"),
                        ResultLength = Long(metrics, "result_length"),
                    });
                    break;

                case "assistant_message":
                    tele.Messages.Add(new AssistantMessageEvent
                    {
                        SessionId = sessionId,
                        Timestamp = time,
                        TurnId = IntFromString(Str(props, "turn_id")),
                        ApiCallId = Str(props, "api_call_id"),
                        Model = Str(props, "model"),
                        Phase = Str(props, "phase"),
                        HasToolRequests = string.Equals(Str(props, "has_tool_requests"), "true", StringComparison.OrdinalIgnoreCase),
                        ToolRequestCount = (int)Long(metrics, "tool_request_count"),
                        ContentLength = (int)Long(metrics, "content_length"),
                    });
                    break;

                case "user_message":
                    tele.UserPrompts.Add(new UserPromptEvent
                    {
                        SessionId = sessionId,
                        Timestamp = time,
                        ContentLength = (int)Long(metrics, "content_length"),
                        AgentMode = Str(props, "agent_mode"),
                    });
                    break;

                case "session_usage_info":
                    tele.ContextSamples.Add(new SessionUsageSample
                    {
                        SessionId = sessionId,
                        Timestamp = time,
                        TokenLimit = Long(metrics, "token_limit"),
                        CurrentTokens = Long(metrics, "current_tokens"),
                        SystemTokens = Long(metrics, "system_tokens"),
                        ConversationTokens = Long(metrics, "conversation_tokens"),
                        ToolDefinitionsTokens = Long(metrics, "tool_definitions_tokens"),
                        IsInitial = string.Equals(Str(props, "is_initial"), "true", StringComparison.OrdinalIgnoreCase),
                    });
                    break;

                case "assistant_turn_start":
                    tele.TurnStarts.Add(new TurnBoundary
                    {
                        SessionId = sessionId,
                        TurnId = IntFromString(Str(props, "turn_id")),
                        Timestamp = time,
                    });
                    break;

                case "session_shutdown":
                    var dur = Dbl(metrics, "session_duration_ms");
                    if (dur > 0) tele.SessionDurationMs = (tele.SessionDurationMs ?? 0) + dur;
                    break;
            }
        }
    }

    private static string? Str(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() : null;

    private static long Long(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var e)) return 0;
        return e.ValueKind switch
        {
            JsonValueKind.Number when e.TryGetInt64(out var l) => l,
            JsonValueKind.Number when e.TryGetDouble(out var d) => (long)d,
            JsonValueKind.String when long.TryParse(e.GetString(), out var l) => l,
            _ => 0,
        };
    }

    private static double Dbl(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var e)) return 0;
        return e.ValueKind switch
        {
            JsonValueKind.Number when e.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(e.GetString(), out var d) => d,
            _ => 0,
        };
    }

    private static int IntFromString(string? s)
        => int.TryParse(s, out var i) ? i : -1;

    private static DateTimeOffset? ParseRootTime(JsonElement root)
    {
        if (!root.TryGetProperty("created_at", out var e) || e.ValueKind != JsonValueKind.String)
            return null;
        return DateTimeOffset.TryParse(e.GetString(), null,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var dto) ? dto : null;
    }
}
