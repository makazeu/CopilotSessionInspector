using System.Text.Json;
using CopilotSessionInspector.Models;

namespace CopilotSessionInspector.Services;

/// <summary>
/// Reads <c>~/.copilot/session-state/&lt;sessionId&gt;/events.jsonl</c>, the authoritative,
/// fully-ordered event stream for a session. Unlike the telemetry logs, these events carry
/// the real conversation text (user prompts, every intermediate agent reply with its
/// reasoning and tool requests, and full tool results). Per-session results are cached.
/// </summary>
public sealed class SessionEventsParser
{
    private const long MaxFileBytes = 200_000_000;

    private readonly CopilotPaths _paths;
    private readonly ILogger<SessionEventsParser> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<SessionEvent>> _cache = new(StringComparer.Ordinal);

    public SessionEventsParser(CopilotPaths paths, ILogger<SessionEventsParser> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public void Reload()
    {
        lock (_gate) _cache.Clear();
    }

    /// <summary>Returns the ordered events for a session, or an empty list if none exist.</summary>
    public List<SessionEvent> GetForSession(string sessionId)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(sessionId, out var cached))
                return cached;
            var parsed = ParseSession(sessionId);
            _cache[sessionId] = parsed;
            return parsed;
        }
    }

    public bool HasEvents(string sessionId) => GetForSession(sessionId).Count > 0;

    private List<SessionEvent> ParseSession(string sessionId)
    {
        var result = new List<SessionEvent>();
        var file = Path.Combine(_paths.SessionStateDir, sessionId, "events.jsonl");
        if (!File.Exists(file)) return result;

        try
        {
            var info = new FileInfo(file);
            if (info.Length is <= 0 or > MaxFileBytes) return result;

            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line) || line[0] != '{') continue;
                var ev = ParseLine(line);
                if (ev is not null) result.Add(ev);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed parsing events.jsonl for session {SessionId}", sessionId);
        }

        result.Sort((a, b) => Nullable.Compare(a.Timestamp, b.Timestamp));
        return result;
    }

    private static SessionEvent? ParseLine(string line)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var type = Str(root, "type");
            if (string.IsNullOrEmpty(type)) return null;

            root.TryGetProperty("data", out var data);
            var ev = new SessionEvent
            {
                Type = type!,
                Timestamp = ParseTime(root, "timestamp"),
            };

            switch (type)
            {
                case "user.message":
                    ev.Content = Str(data, "content");
                    ev.AgentMode = Str(data, "agentMode");
                    break;

                case "assistant.message":
                    ev.Content = Str(data, "content");
                    ev.ReasoningText = Str(data, "reasoningText");
                    ev.Model = Str(data, "model");
                    ev.ApiCallId = Str(data, "apiCallId");
                    ev.TurnId = Str(data, "turnId");
                    ev.OutputTokens = Long(data, "outputTokens");
                    if (data.ValueKind == JsonValueKind.Object
                        && data.TryGetProperty("toolRequests", out var trs)
                        && trs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tr in trs.EnumerateArray())
                        {
                            ev.ToolRequests.Add(new ToolRequestInfo
                            {
                                ToolCallId = Str(tr, "toolCallId"),
                                Name = Str(tr, "name"),
                                Arguments = RawArguments(tr),
                            });
                        }
                    }
                    break;

                case "tool.execution_start":
                    ev.ToolCallId = Str(data, "toolCallId");
                    ev.ToolName = Str(data, "toolName");
                    ev.Model = Str(data, "model");
                    ev.TurnId = Str(data, "turnId");
                    ev.Arguments = RawArguments(data);
                    break;

                case "tool.execution_complete":
                    ev.ToolCallId = Str(data, "toolCallId");
                    ev.Model = Str(data, "model");
                    ev.TurnId = Str(data, "turnId");
                    ev.Success = Bool(data, "success");
                    if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("result", out var res))
                    {
                        ev.ResultContent = Str(res, "content");
                        ev.ResultLength = ev.ResultContent?.Length ?? 0;
                    }
                    if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("error", out var err))
                    {
                        // Failures carry no `result`; the reason lives in error.message/error.code.
                        if (err.ValueKind == JsonValueKind.Object)
                        {
                            ev.ErrorMessage = Str(err, "message");
                            ev.ErrorCode = Str(err, "code");
                        }
                        else if (err.ValueKind == JsonValueKind.String)
                        {
                            ev.ErrorMessage = err.GetString();
                        }
                    }
                    ev.DurationMs = ToolDurationMs(data);
                    ev.IsMcp = Bool(data, "isMcp") ?? false;
                    break;

                case "assistant.turn_start":
                case "assistant.turn_end":
                    ev.TurnId = Str(data, "turnId");
                    break;

                default:
                    // Other event types (session.*, hook.*) are kept only for completeness.
                    break;
            }

            return ev;
        }
    }

    private static string? RawArguments(JsonElement parent)
    {
        if (parent.ValueKind != JsonValueKind.Object) return null;
        if (!parent.TryGetProperty("arguments", out var args)) return null;
        return args.ValueKind switch
        {
            JsonValueKind.String => args.GetString(),
            JsonValueKind.Object or JsonValueKind.Array => args.GetRawText(),
            _ => null,
        };
    }

    private static double ToolDurationMs(JsonElement data)
    {
        // tool.execution_complete -> data.toolTelemetry.metrics.durationMs
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("toolTelemetry", out var tt) && tt.ValueKind == JsonValueKind.Object
            && tt.TryGetProperty("metrics", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            return Dbl(m, "durationMs");
        }
        return 0;
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

    private static bool? Bool(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var e)) return null;
        return e.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(e.GetString(), out var b) => b,
            _ => null,
        };
    }

    private static DateTimeOffset? ParseTime(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var e)) return null;
        if (e.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(e.GetString(), null,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dto))
            return dto;
        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var num))
            return num > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(num) : DateTimeOffset.FromUnixTimeSeconds(num);
        return null;
    }
}
