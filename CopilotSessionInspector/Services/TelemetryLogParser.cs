using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using CopilotSessionInspector.Models;

namespace CopilotSessionInspector.Services;

/// <summary>
/// Scans ~/.copilot/logs/process-*.log and Agency's nested process logs, then extracts pretty-printed telemetry JSON
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
        var files = TelemetryLogFiles()
            .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        var parsedFiles = new ConcurrentBag<Dictionary<string, SessionTelemetry>>();
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };

        Parallel.ForEach(files, options, file =>
        {
            try
            {
                parsedFiles.Add(ParseFile(file.Path, file.Source));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed parsing telemetry log {File}", file.Path);
            }
        });

        var result = new Dictionary<string, SessionTelemetry>(StringComparer.Ordinal);
        foreach (var parsed in parsedFiles)
            MergeTelemetry(result, parsed);

        NormalizeTelemetry(result);
        _parsedAt = DateTimeOffset.Now;
        return result;
    }

    private IEnumerable<(string Path, string Source)> TelemetryLogFiles()
    {
        if (Directory.Exists(_paths.LogsDir))
        {
            foreach (var file in Directory.EnumerateFiles(_paths.LogsDir, "*.log"))
                yield return (file, "Copilot logs");
        }

        if (Directory.Exists(_paths.AgencyLogsDir))
        {
            foreach (var file in Directory.EnumerateFiles(_paths.AgencyLogsDir, "process-*.log", SearchOption.AllDirectories))
                yield return (file, "Agency logs");
        }
    }

    private static Dictionary<string, SessionTelemetry> ParseFile(string file, string source)
    {
        var result = new Dictionary<string, SessionTelemetry>(StringComparer.Ordinal);
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
                HandleBlock(block.ToString(), source, lastTimestamp, result);
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

        return result;
    }

    private static void MergeTelemetry(Dictionary<string, SessionTelemetry> target, Dictionary<string, SessionTelemetry> source)
    {
        foreach (var (sessionId, incoming) in source)
        {
            if (!target.TryGetValue(sessionId, out var existing))
            {
                target[sessionId] = incoming;
                continue;
            }

            existing.Usage.AddRange(incoming.Usage);
            existing.ContextSamples.AddRange(incoming.ContextSamples);
            existing.TurnStarts.AddRange(incoming.TurnStarts);
            existing.ToolCalls.AddRange(incoming.ToolCalls);
            existing.Messages.AddRange(incoming.Messages);
            existing.UserPrompts.AddRange(incoming.UserPrompts);
            if (incoming.SessionDurationMs is > 0)
                existing.SessionDurationMs = (existing.SessionDurationMs ?? 0) + incoming.SessionDurationMs.Value;
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

    private static void HandleBlock(string json, string source, DateTimeOffset? timestamp,
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
                        Source = source,
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
                        Source = source,
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
                        Source = source,
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
                        Source = source,
                        Timestamp = time,
                        ContentLength = (int)Long(metrics, "content_length"),
                        AgentMode = Str(props, "agent_mode"),
                    });
                    break;

                case "session_usage_info":
                    tele.ContextSamples.Add(new SessionUsageSample
                    {
                        SessionId = sessionId,
                        Source = source,
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
                        Source = source,
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

    private static void NormalizeTelemetry(Dictionary<string, SessionTelemetry> result)
    {
        foreach (var tele in result.Values)
        {
            var usage = tele.Usage
                .Select((u, i) => (Usage: u, Index: i))
                .GroupBy(x => !string.IsNullOrWhiteSpace(x.Usage.ApiCallId)
                    ? $"api:{x.Usage.ApiCallId}"
                    : $"time:{x.Usage.Timestamp:o}:{x.Usage.Model}:{x.Usage.OutputTokens}:{x.Index}")
                .Select(g =>
                {
                    var best = g.Select(x => x.Usage)
                        .OrderByDescending(UsageScore)
                        .ThenBy(u => string.Equals(u.Source, "Agency logs", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                        .First();
                    best.Source = JoinSources(g.Select(x => x.Usage.Source));
                    return best;
                })
                .OrderBy(u => u.Timestamp ?? DateTimeOffset.MaxValue)
                .ToList();
            tele.Usage.Clear();
            tele.Usage.AddRange(usage);

            var toolCalls = tele.ToolCalls
                .GroupBy(ToolKey)
                .Select(g =>
                {
                    var best = g.OrderByDescending(t => (t.DurationMs > 0 ? 1 : 0) + (t.ResultLength > 0 ? 1 : 0)).First();
                    best.Source = JoinSources(g.Select(t => t.Source));
                    return best;
                })
                .OrderBy(t => t.Timestamp ?? DateTimeOffset.MaxValue)
                .ToList();
            tele.ToolCalls.Clear();
            tele.ToolCalls.AddRange(toolCalls);
        }
    }

    private static int UsageScore(AssistantUsageEvent usage)
        => (usage.TotalNanoAiu > 0 ? 8 : 0)
            + (usage.Cost > 0 ? 4 : 0)
            + (usage.InputTokens > 0 ? 2 : 0)
            + (usage.OutputTokens > 0 ? 1 : 0)
            + (usage.DurationMs > 0 ? 1 : 0);

    private static string ToolKey(ToolCallEvent tool)
        => !string.IsNullOrWhiteSpace(tool.ApiCallId)
            ? $"api:{tool.ApiCallId}:{tool.ToolName}:{tool.Timestamp?.ToUnixTimeMilliseconds()}"
            : $"time:{tool.Timestamp?.ToUnixTimeMilliseconds()}:{tool.ToolName}:{tool.Arguments}:{tool.ResultType}";

    private static string? JoinSources(IEnumerable<string?> sources)
    {
        var distinct = sources
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return distinct.Count == 0 ? null : string.Join(" + ", distinct);
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
