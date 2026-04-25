using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using model_kate.Domain;

namespace model_kate.Infrastructure
{
    public sealed class KateDatabaseService : IKateDatabaseService
    {
        private readonly SqliteConnection _conn;
        private bool _disposed;

        public KateDatabaseService(string? dbPath = null)
        {
            var path = dbPath
                ?? Environment.GetEnvironmentVariable("KATE_DB_FILE")?.Trim()
                ?? Path.Combine(Directory.GetCurrentDirectory(), "kate.db");

            _conn = new SqliteConnection($"Data Source={path};Mode=ReadWriteCreate;");
            _conn.Open();
            EnableWal();
            CreateSchema();
        }

        private void EnableWal()
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
            cmd.ExecuteNonQuery();
        }

        private void CreateSchema()
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS sessions (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    started_at  TEXT    NOT NULL,
                    ended_at    TEXT,
                    summary     TEXT,
                    turn_count  INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS conversations (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id    INTEGER NOT NULL REFERENCES sessions(id),
                    timestamp     TEXT    NOT NULL,
                    user_text     TEXT    NOT NULL,
                    kate_response TEXT    NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_conv_session ON conversations(session_id);
                CREATE INDEX IF NOT EXISTS idx_conv_time    ON conversations(timestamp DESC);

                CREATE VIRTUAL TABLE IF NOT EXISTS conversations_fts
                    USING fts5(user_text, kate_response, content=conversations, content_rowid=id);

                CREATE TRIGGER IF NOT EXISTS conv_ai AFTER INSERT ON conversations BEGIN
                    INSERT INTO conversations_fts(rowid, user_text, kate_response)
                    VALUES (new.id, new.user_text, new.kate_response);
                END;

                CREATE TABLE IF NOT EXISTS user_facts (
                    id         INTEGER PRIMARY KEY AUTOINCREMENT,
                    fact_key   TEXT    NOT NULL UNIQUE,
                    fact_value TEXT    NOT NULL,
                    updated_at TEXT    NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }

        // ── Sessões ─────────────────────────────────────────────────────────

        public long CreateSession()
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO sessions (started_at) VALUES ($ts); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }

        public void CloseSession(long sessionId, string? summary)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE sessions
                   SET ended_at   = $ended,
                       summary    = $summary,
                       turn_count = (SELECT COUNT(*) FROM conversations WHERE session_id = $id)
                 WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$ended", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$summary", summary ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.ExecuteNonQuery();
        }

        public SessionRecord? GetLastSession()
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, started_at, ended_at, summary, turn_count
                  FROM sessions
                 ORDER BY id DESC
                 LIMIT 1;
                """;
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new SessionRecord(
                r.GetInt64(0),
                DateTime.Parse(r.GetString(1)),
                r.IsDBNull(2) ? null : DateTime.Parse(r.GetString(2)),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.GetInt32(4));
        }

        // ── Conversas ───────────────────────────────────────────────────────

        public void SaveTurn(long sessionId, string userText, string kateResponse)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO conversations (session_id, timestamp, user_text, kate_response)
                VALUES ($sid, $ts, $user, $kate);
                """;
            cmd.Parameters.AddWithValue("$sid", sessionId);
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$user", userText);
            cmd.Parameters.AddWithValue("$kate", kateResponse);
            cmd.ExecuteNonQuery();
        }

        public IReadOnlyList<ConversationRecord> GetRecentTurns(int count)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, session_id, timestamp, user_text, kate_response
                  FROM conversations
                 ORDER BY id DESC
                 LIMIT $count;
                """;
            cmd.Parameters.AddWithValue("$count", count);
            var result = new List<ConversationRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                result.Add(new ConversationRecord(
                    r.GetInt64(0),
                    r.GetInt64(1),
                    DateTime.Parse(r.GetString(2)),
                    r.GetString(3),
                    r.GetString(4)));
            }
            result.Reverse();
            return result;
        }

        public IReadOnlyList<ConversationRecord> SearchHistory(string keyword, int maxResults = 10)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT c.id, c.session_id, c.timestamp, c.user_text, c.kate_response
                  FROM conversations c
                  JOIN conversations_fts f ON f.rowid = c.id
                 WHERE conversations_fts MATCH $kw
                 ORDER BY rank
                 LIMIT $max;
                """;
            cmd.Parameters.AddWithValue("$kw", keyword);
            cmd.Parameters.AddWithValue("$max", maxResults);
            var result = new List<ConversationRecord>();
            try
            {
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    result.Add(new ConversationRecord(
                        r.GetInt64(0), r.GetInt64(1),
                        DateTime.Parse(r.GetString(2)),
                        r.GetString(3), r.GetString(4)));
                }
            }
            catch { /* FTS pode falhar em query malformada */ }
            return result;
        }

        // ── Fatos do usuário ────────────────────────────────────────────────

        public void UpsertFact(string key, string value)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO user_facts (fact_key, fact_value, updated_at)
                VALUES ($k, $v, $ts)
                ON CONFLICT(fact_key) DO UPDATE SET fact_value = $v, updated_at = $ts;
                """;
            cmd.Parameters.AddWithValue("$k", key.ToLowerInvariant().Trim());
            cmd.Parameters.AddWithValue("$v", value.Trim());
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }

        public IReadOnlyList<UserFact> GetAllFacts()
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT fact_key, fact_value, updated_at FROM user_facts ORDER BY fact_key;";
            var result = new List<UserFact>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                result.Add(new UserFact(r.GetString(0), r.GetString(1), DateTime.Parse(r.GetString(2))));
            }
            return result;
        }

        public string? GetFact(string key)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT fact_value FROM user_facts WHERE fact_key = $k LIMIT 1;";
            cmd.Parameters.AddWithValue("$k", key.ToLowerInvariant().Trim());
            return cmd.ExecuteScalar() as string;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _conn.Close();
            _conn.Dispose();
        }
    }
}
