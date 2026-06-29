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
    private readonly Dictionary<string, SessionEventSummary> _summaryCache = new(StringComparer.Ordinal);
    private Dictionary<string, string>? _agencyEventsIndex;

    public SessionEventsParser(CopilotPaths paths, ILogger<SessionEventsParser> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public void Reload()
    {
        lock (_gate)
        {
            _cache.Clear();
            _summaryCache.Clear();
            _agencyEventsIndex = null;
        }
    }

    /// <summary>Returns the ordered events for a session, or an empty list if none exist.</summary>
    public List<SessionEvent> GetForSession(string sessionId)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(sessionId, out var cached))
                return cached;
        }

        var parsed = ParseSession(sessionId);

        lock (_gate)
        {
            if (_cache.TryGetValue(sessionId, out var cached))
                return cached;
            _cache[sessionId] = parsed;
            return parsed;
        }
    }

    public bool HasEvents(string sessionId) => GetForSession(sessionId).Count > 0;

    public SessionEventSummary GetSummaryForSession(string sessionId)
    {
        lock (_gate)
        {
            if (_summaryCache.TryGetValue(sessionId, out var cached))
                return cached;
        }

        var summary = ParseSessionSummary(sessionId);

        lock (_gate)
        {
            if (_summaryCache.TryGetValue(sessionId, out var cached))
                return cached;
            _summaryCache[sessionId] = summary;
            return summary;
        }
    }

    private List<SessionEvent> ParseSession(string sessionId)
    {
        var result = new List<SessionEvent>();
        var file = FindEventsFile(sessionId);
        if (!File.Exists(file)) return result;
        var source = IsAgencyPath(file) ? "Agency events" : "Copilot session-state";

        try
        {
            var info = new FileInfo(file);
            if (info.Length is <= 0 or > MaxFileBytes) return result;

            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line) || line[0] != '{') continue;
                var ev = ParseLine(line);
                if (ev is not null)
                {
                    ev.Source = source;
                    result.Add(ev);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed parsing events.jsonl for session {SessionId}", sessionId);
        }

        result.Sort((a, b) => Nullable.Compare(a.Timestamp, b.Timestamp));
        return result;
    }

    private SessionEventSummary ParseSessionSummary(string sessionId)
    {
        var file = FindEventsFile(sessionId);
        if (!File.Exists(file)) return new SessionEventSummary();
        var source = IsAgencyPath(file) ? "Agency events" : "Copilot session-state";
        var summary = new SessionEventSummary { Source = source, HasEvents = true };

        DateTimeOffset? turnStart = null;
        DateTimeOffset? lastInTurn = null;
        double activeDurationMs = 0;

        try
        {
            var info = new FileInfo(file);
            if (info.Length is <= 0 or > MaxFileBytes) return new SessionEventSummary();

            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line) || line[0] != '{') continue;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch { continue; }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    var type = Str(root, "type");
                    if (string.IsNullOrWhiteSpace(type)) continue;
                    var timestamp = ParseTime(root, "timestamp");
                    root.TryGetProperty("data", out var data);

                    if (type == "session.resume"
                        && data.ValueKind == JsonValueKind.Object
                        && data.TryGetProperty("context", out var context)
                        && context.ValueKind == JsonValueKind.Object)
                    {
                        summary.Cwd ??= Str(context, "cwd");
                        summary.Repository ??= Str(context, "repository");
                        summary.Branch ??= Str(context, "branch");
                        summary.HostType ??= Str(context, "hostType");
                    }
                    else if (type == "user.message")
                    {
                        var content = Str(data, "content");
                        if (string.IsNullOrWhiteSpace(content)) continue;
                        if (turnStart.HasValue && lastInTurn.HasValue)
                        {
                            var duration = (lastInTurn.Value - turnStart.Value).TotalMilliseconds;
                            if (duration > 0) activeDurationMs += duration;
                        }
                        summary.UserMessageCount++;
                        turnStart = timestamp;
                        lastInTurn = timestamp;
                    }
                    else if (type == "assistant.message")
                    {
                        summary.AssistantMessageCount++;
                        summary.OutputTokens += Long(data, "outputTokens");
                        if (turnStart.HasValue && timestamp.HasValue)
                            lastInTurn = timestamp.Value;
                    }
                    else if (type == "tool.execution_complete")
                    {
                        summary.ToolExecutionCount++;
                        if (turnStart.HasValue && timestamp.HasValue)
                            lastInTurn = timestamp.Value;
                    }
                    else if (type == "skill.invoked")
                    {
                        if (turnStart.HasValue && timestamp.HasValue)
                            lastInTurn = timestamp.Value;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed parsing events.jsonl summary for session {SessionId}", sessionId);
            return new SessionEventSummary();
        }

        if (turnStart.HasValue && lastInTurn.HasValue)
        {
            var duration = (lastInTurn.Value - turnStart.Value).TotalMilliseconds;
            if (duration > 0) activeDurationMs += duration;
        }
        summary.SessionDurationMs = activeDurationMs;
        return summary;
    }

    private string FindEventsFile(string sessionId)
    {
        var copilotFile = Path.Combine(_paths.SessionStateDir, sessionId, "events.jsonl");
        if (File.Exists(copilotFile)) return copilotFile;

        var agencyIndex = GetAgencyEventsIndex();
        return agencyIndex.TryGetValue(sessionId, out var agencyFile) ? agencyFile : copilotFile;
    }

    private Dictionary<string, string> GetAgencyEventsIndex()
    {
        lock (_gate)
        {
            if (_agencyEventsIndex is not null)
                return _agencyEventsIndex;

            _agencyEventsIndex = BuildAgencyEventsIndex();
            return _agencyEventsIndex;
        }
    }

    private Dictionary<string, string> BuildAgencyEventsIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(_paths.AgencyLogsDir)) return index;

        foreach (var file in Directory.EnumerateFiles(_paths.AgencyLogsDir, "events.jsonl", SearchOption.AllDirectories))
        {
            var sessionId = TryReadSessionId(file);
            if (!string.IsNullOrWhiteSpace(sessionId))
                index.TryAdd(sessionId, file);
        }

        return index;
    }

    private static string? TryReadSessionId(string file)
    {
        try
        {
            foreach (var line in File.ReadLines(file).Take(40))
            {
                if (string.IsNullOrWhiteSpace(line) || line[0] != '{') continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Object)
                    continue;

                var sessionId = Str(data, "sessionId");
                if (!string.IsNullOrWhiteSpace(sessionId)) return sessionId;

                if (data.TryGetProperty("context", out _))
                {
                    sessionId = Str(data, "session_id");
                    if (!string.IsNullOrWhiteSpace(sessionId)) return sessionId;
                }
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return null;
    }

    private bool IsAgencyPath(string file)
        => Path.GetFullPath(file).StartsWith(Path.GetFullPath(_paths.AgencyLogsDir), StringComparison.OrdinalIgnoreCase);

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
                case "session.resume":
                    if (data.ValueKind == JsonValueKind.Object
                        && data.TryGetProperty("context", out var context)
                        && context.ValueKind == JsonValueKind.Object)
                    {
                        ev.Cwd = Str(context, "cwd");
                        ev.Repository = Str(context, "repository");
                        ev.Branch = Str(context, "branch");
                        ev.HostType = Str(context, "hostType");
                    }
                    break;

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

                case "skill.invoked":
                    ev.SkillName = Str(data, "name");
                    ev.SkillPath = Str(data, "path");
                    ev.SkillSource = Str(data, "source");
                    ev.PluginName = Str(data, "pluginName");
                    ev.SkillDescription = Str(data, "description");
                    ev.SkillTrigger = Str(data, "trigger");
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
