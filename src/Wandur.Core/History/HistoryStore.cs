using System.Text;
using Microsoft.Data.Sqlite;
using Wandur.Core.Storage;

namespace Wandur.Core.History;

public sealed record HistorySession(string Id, string WorldKey, string WorldName, string CharacterName,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt = null);
public sealed record HistoryEntry(long Sequence, DateTimeOffset At, string Kind, string Text);
public sealed record HistoryHit(HistorySession Session, HistoryEntry Entry);
public sealed record HistoryFilter(string? World = null, string? Character = null,
    DateTimeOffset? From = null, DateTimeOffset? Until = null);

public interface IHistoryStore
{
    void Append(HistorySession session, IReadOnlyList<HistoryEntry> entries);
    IReadOnlyList<HistorySession> Sessions(HistoryFilter filter, int offset = 0, int limit = 100);
    IReadOnlyList<HistoryHit> Search(string query, HistoryFilter filter, int offset = 0, int limit = 100);
    IReadOnlyList<HistoryEntry> Entries(string sessionId, long fromSequence = 0, int limit = 200);
    void Delete(string sessionId);
    void Prune(DateTimeOffset before);
}

/// <summary>Short-lived connections; callers schedule all work off the UI thread.</summary>
public sealed class SqliteHistoryStore : IHistoryStore
{
    private const int MaximumPageSize = 500;
    private const int MaximumQueryLength = 4096;
    private const string SessionColumns = "s.id,s.world_key,s.world_name,s.character_name,s.started_at,s.ended_at";
    private readonly ClientDatabase _database;

    public SqliteHistoryStore(ClientDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public void Append(HistorySession session, IReadOnlyList<HistoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(entries);
        ValidateId(session.Id, nameof(session));
        ValidateId(session.WorldKey, nameof(session));
        ArgumentNullException.ThrowIfNull(session.WorldName);
        ArgumentNullException.ThrowIfNull(session.CharacterName);
        if (session.EndedAt < session.StartedAt) throw new ArgumentException(null, nameof(session));
        // Validate the complete batch before taking the write lock or changing metadata.
        foreach (var entry in entries)
        {
            if (entry is null || entry.Sequence < 0 || entry.Text is null ||
                entry.Kind is not ("received" or "sent" or "script" or "private"))
                throw new ArgumentException(null, nameof(entries));
        }
        _database.Write((connection, transaction) =>
        {
            using var sessionCommand = connection.CreateCommand();
            sessionCommand.Transaction = transaction;
            sessionCommand.CommandText = "SELECT 1 FROM history_deletions WHERE session_id=$id";
            sessionCommand.Parameters.AddWithValue("$id", session.Id);
            // Delete and append serialize under the same SQLite write lock, across instances too.
            if (sessionCommand.ExecuteScalar() is not null) return 0;
            sessionCommand.CommandText = """
                INSERT INTO history_sessions(id,world_key,world_name,character_name,started_at,ended_at)
                VALUES($id,$worldKey,$worldName,$character,$started,$ended)
                ON CONFLICT(id) DO UPDATE SET
                    world_key=excluded.world_key,world_name=excluded.world_name,
                    character_name=CASE WHEN excluded.character_name<>'' AND
                        (history_sessions.ended_at IS NULL OR excluded.ended_at IS NOT NULL)
                        THEN excluded.character_name ELSE history_sessions.character_name END,
                    ended_at=CASE WHEN history_sessions.ended_at IS NULL THEN excluded.ended_at
                        WHEN excluded.ended_at IS NULL THEN history_sessions.ended_at
                        ELSE MAX(history_sessions.ended_at,excluded.ended_at) END
                """;
            sessionCommand.Parameters.AddWithValue("$worldKey", session.WorldKey);
            sessionCommand.Parameters.AddWithValue("$worldName", session.WorldName);
            sessionCommand.Parameters.AddWithValue("$character", session.CharacterName);
            sessionCommand.Parameters.AddWithValue("$started", session.StartedAt.UtcTicks);
            sessionCommand.Parameters.AddWithValue("$ended", (object?)session.EndedAt?.UtcTicks ?? DBNull.Value);
            sessionCommand.ExecuteNonQuery();

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO history_entries(session_id,sequence,at,kind,text) VALUES($id,$sequence,$at,$kind,$text)
                ON CONFLICT(session_id,sequence) DO NOTHING
                """;
            insert.Parameters.AddWithValue("$id", session.Id);
            var sequence = insert.Parameters.Add("$sequence", SqliteType.Integer);
            var at = insert.Parameters.Add("$at", SqliteType.Integer);
            var kind = insert.Parameters.Add("$kind", SqliteType.Text);
            var text = insert.Parameters.Add("$text", SqliteType.Text);
            foreach (var entry in entries)
            {
                sequence.Value = entry.Sequence;
                at.Value = entry.At.UtcTicks;
                kind.Value = entry.Kind;
                text.Value = entry.Text;
                insert.ExecuteNonQuery();
            }
            return 0;
        });
    }

    /// <summary>Newest starts first, then session id; dates filter the session's start time.</summary>
    public IReadOnlyList<HistorySession> Sessions(HistoryFilter filter, int offset = 0, int limit = 100)
    {
        ValidateFilter(filter);
        limit = PageSize(offset, limit);
        return _database.Read<IReadOnlyList<HistorySession>>(connection =>
        {
            using var command = connection.CreateCommand();
            var where = BindFilter(command, filter, "s.started_at");
            command.CommandText = $"SELECT {SessionColumns} FROM history_sessions s WHERE {where} ORDER BY s.started_at DESC,s.id LIMIT $limit OFFSET $offset";
            BindPage(command, offset, limit);
            using var rows = command.ExecuteReader();
            var sessions = new List<HistorySession>();
            while (rows.Read()) sessions.Add(ReadSession(rows));
            return sessions;
        });
    }

    /// <summary>Literal AND words/phrases, newest entries first. Dates filter entry receipt times.</summary>
    public IReadOnlyList<HistoryHit> Search(string query, HistoryFilter filter, int offset = 0, int limit = 100)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateFilter(filter);
        limit = PageSize(offset, limit);
        if (query.Length > MaximumQueryLength) throw new ArgumentException(null, nameof(query));
        var match = LiteralMatch(query);
        if (match.Length == 0) return [];
        return _database.Read<IReadOnlyList<HistoryHit>>(connection =>
        {
            using var command = connection.CreateCommand();
            var where = BindFilter(command, filter, "e.at");
            command.CommandText = $"""
                SELECT {SessionColumns},e.sequence,e.at,e.kind,e.text
                FROM history_entries_fts
                JOIN history_entries e ON e.id=history_entries_fts.rowid
                JOIN history_sessions s ON s.id=e.session_id
                WHERE history_entries_fts MATCH $match AND {where}
                ORDER BY e.at DESC,e.id DESC LIMIT $limit OFFSET $offset
                """;
            command.Parameters.AddWithValue("$match", match);
            BindPage(command, offset, limit);
            using var rows = command.ExecuteReader();
            var hits = new List<HistoryHit>();
            while (rows.Read()) hits.Add(new(ReadSession(rows), ReadEntry(rows, 6)));
            return hits;
        });
    }

    /// <summary>Sequence-ordered context, inclusive of fromSequence. Every read is capped at 500 rows.</summary>
    public IReadOnlyList<HistoryEntry> Entries(string sessionId, long fromSequence = 0, int limit = 200)
    {
        ValidateId(sessionId, nameof(sessionId));
        if (fromSequence < 0) throw new ArgumentException(null, nameof(fromSequence));
        limit = PageSize(0, limit);
        return _database.Read<IReadOnlyList<HistoryEntry>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT sequence,at,kind,text FROM history_entries WHERE session_id=$id AND sequence>=$sequence ORDER BY sequence LIMIT $limit";
            command.Parameters.AddWithValue("$id", sessionId);
            command.Parameters.AddWithValue("$sequence", fromSequence);
            command.Parameters.AddWithValue("$limit", limit);
            using var rows = command.ExecuteReader();
            var entries = new List<HistoryEntry>();
            while (rows.Read()) entries.Add(ReadEntry(rows, 0));
            return entries;
        });
    }

    public void Delete(string sessionId)
    {
        ValidateId(sessionId, nameof(sessionId));
        _database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO history_deletions(session_id) VALUES($id) ON CONFLICT DO NOTHING;
                DELETE FROM history_sessions WHERE id=$id;
                """;
            command.Parameters.AddWithValue("$id", sessionId);
            // The FK cascades entry deletion and its FTS trigger in the same transaction.
            return command.ExecuteNonQuery();
        });
    }

    public void Prune(DateTimeOffset before)
    {
        _database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM history_entries WHERE at<$before;
                DELETE FROM history_sessions WHERE COALESCE(ended_at,started_at)<$before
                    AND NOT EXISTS(SELECT 1 FROM history_entries WHERE session_id=history_sessions.id);
                """;
            command.Parameters.AddWithValue("$before", before.UtcTicks);
            return command.ExecuteNonQuery();
        });
    }

    private static string LiteralMatch(string query)
    {
        // NUL must not truncate the MATCH expression inside SQLite's tokenizer.
        query = query.Replace('\0', ' ');
        var terms = new List<string>();
        for (var index = 0; index < query.Length;)
        {
            if (char.IsWhiteSpace(query[index])) { index++; continue; }
            var quoted = query[index] == '"';
            if (quoted) index++;
            var builder = new StringBuilder();
            while (index < query.Length)
            {
                if (query[index] == '"')
                {
                    if (!quoted) break;
                    index++;
                    if (index == query.Length || query[index] != '"') break;
                    // Two quotes within a phrase represent one literal quotation mark.
                    builder.Append('"');
                    index++;
                }
                else
                {
                    if (!quoted && char.IsWhiteSpace(query[index])) break;
                    builder.Append(query[index++]);
                }
            }
            var term = builder.ToString();
            // Empty punctuation has no searchable tokens. FTS operators are always quoted data.
            if (term.EnumerateRunes().Any(Rune.IsLetterOrDigit)) terms.Add("\"" + term.Replace("\"", "\"\"") + "\"");
        }
        return string.Join(" AND ", terms);
    }

    private static string BindFilter(SqliteCommand command, HistoryFilter filter, string timeColumn)
    {
        // Ordinal Unicode matching avoids SQLite LIKE wildcards and its ASCII-only case folding.
        command.Connection!.CreateFunction<string, string, bool>("history_contains",
            (value, fragment) => value.Contains(fragment, StringComparison.OrdinalIgnoreCase), isDeterministic: true);
        var conditions = new List<string> { "1=1" };
        if (!string.IsNullOrWhiteSpace(filter.World))
        {
            conditions.Add("(history_contains(s.world_key,$world) OR history_contains(s.world_name,$world))");
            command.Parameters.AddWithValue("$world", filter.World.Trim());
        }
        if (!string.IsNullOrWhiteSpace(filter.Character))
        {
            conditions.Add("history_contains(s.character_name,$character)");
            command.Parameters.AddWithValue("$character", filter.Character.Trim());
        }
        if (filter.From is { } from)
        {
            conditions.Add(timeColumn + ">=$from");
            command.Parameters.AddWithValue("$from", from.UtcTicks);
        }
        if (filter.Until is { } until)
        {
            conditions.Add(timeColumn + "<$until");
            command.Parameters.AddWithValue("$until", until.UtcTicks);
        }
        return string.Join(" AND ", conditions);
    }

    private static void ValidateFilter(HistoryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.From > filter.Until || filter.World?.Length > MaximumQueryLength || filter.Character?.Length > MaximumQueryLength)
            throw new ArgumentException(null, nameof(filter));
    }

    private static void ValidateId(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumQueryLength || value.Any(char.IsControl))
            throw new ArgumentException(null, parameter);
    }

    private static int PageSize(int offset, int limit)
    {
        if (offset < 0) throw new ArgumentException(null, nameof(offset));
        if (limit < 1) throw new ArgumentException(null, nameof(limit));
        return Math.Min(limit, MaximumPageSize);
    }

    private static void BindPage(SqliteCommand command, int offset, int limit)
    {
        command.Parameters.AddWithValue("$offset", offset);
        command.Parameters.AddWithValue("$limit", limit);
    }

    private static HistorySession ReadSession(SqliteDataReader rows) => new(rows.GetString(0), rows.GetString(1),
        rows.GetString(2), rows.GetString(3), new(rows.GetInt64(4), TimeSpan.Zero),
        rows.IsDBNull(5) ? null : new DateTimeOffset(rows.GetInt64(5), TimeSpan.Zero));

    private static HistoryEntry ReadEntry(SqliteDataReader rows, int start) => new(rows.GetInt64(start),
        new(rows.GetInt64(start + 1), TimeSpan.Zero), rows.GetString(start + 2), rows.GetString(start + 3));
}
