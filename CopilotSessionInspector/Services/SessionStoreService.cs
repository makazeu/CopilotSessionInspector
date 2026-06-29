using CopilotSessionInspector.Models;
using Microsoft.Data.Sqlite;

namespace CopilotSessionInspector.Services;

/// <summary>Reads session metadata, turns and checkpoints from ~/.copilot/session-store.db.</summary>
public sealed class SessionStoreService
{
    private readonly string _dbPath;

    public SessionStoreService(CopilotPaths paths) => _dbPath = paths.SessionStoreDb;

    public bool DatabaseExists => File.Exists(_dbPath);

    public string DatabasePath => _dbPath;

    private SqliteConnection OpenReadOnly()
    {
        // Open a copy-free read-only connection. ReadOnly mode coexists with the
        // CLI's WAL writer without taking a write lock.
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        var con = new SqliteConnection(cs);
        con.Open();
        return con;
    }

    private static DateTimeOffset? ParseTime(object? value)
    {
        if (value is null || value is DBNull) return null;
        var s = value.ToString();
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTimeOffset.TryParse(s, null,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var dto) ? dto : null;
    }

    private static string? GetString(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public List<SessionInfo> GetSessions()
    {
        var list = new List<SessionInfo>();
        if (!DatabaseExists) return list;
        using var con = OpenReadOnly();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT id, cwd, repository, branch, summary, created_at, updated_at, host_type FROM sessions";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new SessionInfo
            {
                Id = r.GetString(0),
                Cwd = GetString(r, 1),
                Repository = GetString(r, 2),
                Branch = GetString(r, 3),
                Summary = GetString(r, 4),
                CreatedAt = ParseTime(r.GetValue(5)),
                UpdatedAt = ParseTime(r.GetValue(6)),
                HostType = GetString(r, 7),
            });
        }
        return list;
    }

    public SessionInfo? GetSession(string id)
    {
        if (!DatabaseExists) return null;
        using var con = OpenReadOnly();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT id, cwd, repository, branch, summary, created_at, updated_at, host_type FROM sessions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new SessionInfo
        {
            Id = r.GetString(0),
            Cwd = GetString(r, 1),
            Repository = GetString(r, 2),
            Branch = GetString(r, 3),
            Summary = GetString(r, 4),
            CreatedAt = ParseTime(r.GetValue(5)),
            UpdatedAt = ParseTime(r.GetValue(6)),
            HostType = GetString(r, 7),
        };
    }

    public List<SessionTurn> GetTurns(string sessionId)
    {
        var list = new List<SessionTurn>();
        if (!DatabaseExists) return list;
        using var con = OpenReadOnly();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT turn_index, user_message, assistant_response, timestamp FROM turns WHERE session_id = $id ORDER BY turn_index";
        cmd.Parameters.AddWithValue("$id", sessionId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new SessionTurn
            {
                TurnIndex = r.GetInt32(0),
                UserMessage = GetString(r, 1),
                AssistantResponse = GetString(r, 2),
                Timestamp = ParseTime(r.GetValue(3)),
            });
        }
        return list;
    }

    /// <summary>Turn counts per session in a single query (for the list page).</summary>
    public Dictionary<string, int> GetTurnCounts()
    {
        var map = new Dictionary<string, int>();
        if (!DatabaseExists) return map;
        using var con = OpenReadOnly();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT session_id, COUNT(*) FROM turns GROUP BY session_id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            map[r.GetString(0)] = r.GetInt32(1);
        return map;
    }

    public Dictionary<string, int> GetContentTurnCounts()
    {
        var map = new Dictionary<string, int>();
        if (!DatabaseExists) return map;
        using var con = OpenReadOnly();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
            SELECT session_id, COUNT(*)
            FROM turns
            WHERE NULLIF(TRIM(COALESCE(assistant_response, '')), '') IS NOT NULL
            GROUP BY session_id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            map[r.GetString(0)] = r.GetInt32(1);
        return map;
    }

    public List<SessionCheckpoint> GetCheckpoints(string sessionId)
    {
        var list = new List<SessionCheckpoint>();
        if (!DatabaseExists) return list;
        using var con = OpenReadOnly();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT checkpoint_number, title, overview, created_at FROM checkpoints WHERE session_id = $id ORDER BY checkpoint_number";
        cmd.Parameters.AddWithValue("$id", sessionId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new SessionCheckpoint
            {
                Number = r.GetInt32(0),
                Title = GetString(r, 1),
                Overview = GetString(r, 2),
                CreatedAt = ParseTime(r.GetValue(3)),
            });
        }
        return list;
    }
}
