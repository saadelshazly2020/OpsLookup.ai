using System.Globalization;
using Microsoft.Data.Sqlite;

namespace OpsLookup.Agent.Data;

/// <summary>One stored message in a ticket thread.</summary>
public sealed record StoredMessage(string Role, string Content, string? Name = null, string? CallId = null);

/// <summary>
/// Durable conversation history, keyed by ticket thread.
/// </summary>
/// <remarks>
/// <para>
/// Stands in for LangGraph's SQLite checkpointer, which held the whole LangChain
/// message list. The shape is simpler on purpose: a role, the text, and the
/// function-call metadata needed to replay a tool turn coherently. Semantic
/// Kernel's message types are not serialised -- they change between versions and
/// binding the store to them would make every SK upgrade a migration.
/// </para>
/// <para>
/// A connection per call rather than a shared one. SQLite serialises writes
/// anyway, and a pooled connection held across an await is a classic way to hit
/// "database is locked" from concurrent turns on different tickets.
/// </para>
/// </remarks>
public sealed class ConversationStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private volatile bool _initialised;

    public ConversationStore(IConfiguration configuration)
    {
        var path = configuration["Conversation:DatabasePath"] ?? "conversations.sqlite";
        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(AppContext.BaseDirectory, path);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            // Without WAL, a reader blocks a writer. Turns on different tickets
            // run concurrently, so without this they serialise or fail.
            Pooling = true,
        }.ToString();

        DatabasePath = path;
    }

    /// <summary>Absolute path, surfaced by /api/health so it can be mounted as a volume.</summary>
    public string DatabasePath { get; }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        if (!_initialised)
        {
            await _initGate.WaitAsync(ct);
            try
            {
                if (!_initialised)
                {
                    using var pragma = connection.CreateCommand();
                    // WAL and busy_timeout are persistent database settings, so
                    // setting them on any connection applies from then on.
                    pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
                    await pragma.ExecuteNonQueryAsync(ct);

                    using var schema = connection.CreateCommand();
                    schema.CommandText =
                        """
                        CREATE TABLE IF NOT EXISTS messages (
                            id        INTEGER PRIMARY KEY AUTOINCREMENT,
                            thread_id TEXT    NOT NULL,
                            seq       INTEGER NOT NULL,
                            role      TEXT    NOT NULL,
                            content   TEXT    NOT NULL,
                            name      TEXT    NULL,
                            call_id   TEXT    NULL
                        );
                        CREATE INDEX IF NOT EXISTS ix_messages_thread
                            ON messages (thread_id, seq);
                        """;
                    await schema.ExecuteNonQueryAsync(ct);
                    _initialised = true;
                }
            }
            finally
            {
                _initGate.Release();
            }
        }

        return connection;
    }

    /// <summary>Every message for a thread, oldest first.</summary>
    public async Task<IReadOnlyList<StoredMessage>> LoadAsync(string threadId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT role, content, name, call_id FROM messages WHERE thread_id = $t ORDER BY seq";
        command.Parameters.AddWithValue("$t", threadId);

        var messages = new List<StoredMessage>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            messages.Add(new StoredMessage(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return messages;
    }

    public async Task AppendAsync(string threadId, IEnumerable<StoredMessage> messages, CancellationToken ct = default)
    {
        var batch = messages.ToList();
        if (batch.Count == 0) return;

        await using var connection = await OpenAsync(ct);
        using var transaction = await connection.BeginTransactionAsync(ct);

        // Continue the thread's sequence rather than trusting a caller-supplied
        // one: two turns on the same ticket can overlap, and a fixed "seq" from
        // the in-memory count would interleave them.
        using (var next = connection.CreateCommand())
        {
            next.Transaction = (SqliteTransaction)transaction;
            next.CommandText =
                "SELECT COALESCE(MAX(seq), 0) FROM messages WHERE thread_id = $t";
            next.Parameters.AddWithValue("$t", threadId);
            var start = Convert.ToInt64(await next.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);

            foreach (var message in batch)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = (SqliteTransaction)transaction;
                insert.CommandText =
                    """
                    INSERT INTO messages (thread_id, seq, role, content, name, call_id)
                    VALUES ($t, $s, $r, $c, $n, $i)
                    """;
                insert.Parameters.AddWithValue("$t", threadId);
                insert.Parameters.AddWithValue("$s", start++);
                insert.Parameters.AddWithValue("$r", message.Role);
                insert.Parameters.AddWithValue("$c", message.Content);
                insert.Parameters.AddWithValue("$n", (object?)message.Name ?? DBNull.Value);
                insert.Parameters.AddWithValue("$i", (object?)message.CallId ?? DBNull.Value);
                await insert.ExecuteNonQueryAsync(ct);
            }
        }

        await transaction.CommitAsync(ct);
    }

    public async Task ClearAsync(string threadId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM messages WHERE thread_id = $t";
        command.Parameters.AddWithValue("$t", threadId);
        await command.ExecuteNonQueryAsync(ct);
    }
}