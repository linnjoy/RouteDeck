using System.Text.Json;
using Microsoft.Data.Sqlite;
using RouteDeck.Core.Models;
using RouteDeck.Core.Serialization;

namespace RouteDeck.Core.Journal;

public enum OperationStatus
{
    /// <summary>Written before the first step; still running or interrupted by a crash.</summary>
    Running,
    /// <summary>Applied, waiting for the user to confirm before the deadline.</summary>
    PendingConfirm,
    Applied,
    /// <summary>A step failed and the completed steps were rolled back.</summary>
    Failed,
    /// <summary>A step failed and rollback did not fully succeed.</summary>
    RollbackFailed,
    /// <summary>Restored automatically (confirmation timeout or crash recovery).</summary>
    Reverted,
    /// <summary>Undone by a later operation.</summary>
    Undone,
}

public sealed record OperationRecord(
    long Id,
    DateTimeOffset Time,
    ChangeSet ChangeSet,
    int AppliedSteps,
    OperationStatus Status,
    string? Error,
    DateTimeOffset? ConfirmDeadline,
    long? UndoOf,
    long? UndoneBy)
{
    public bool IsUnfinished => Status is OperationStatus.Running or OperationStatus.PendingConfirm;

    /// <summary>Steps that actually ran (the journal is written ahead, so a crash leaves a prefix).</summary>
    public IEnumerable<RouteOperation> AppliedOperations => ChangeSet.Operations.Take(AppliedSteps);
}

/// <summary>What RouteDeck owns: a route it created, with the values it last wrote.</summary>
public sealed record OwnedRoute(RouteSpec Route, long OperationId);

/// <summary>
/// Durable record of every change, ownership of created routes, and the write-ahead state that
/// lets the service recover after a crash. Backed by SQLite with synchronous=FULL.
/// </summary>
public sealed class OperationJournal : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _lock = new();
    private readonly TimeProvider _time;

    public OperationJournal(string path, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS operations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                time TEXT NOT NULL,
                origin TEXT NOT NULL,
                change_set TEXT NOT NULL,
                applied_steps INTEGER NOT NULL DEFAULT 0,
                status TEXT NOT NULL,
                error TEXT,
                confirm_deadline TEXT,
                undo_of INTEGER,
                undone_by INTEGER);
            CREATE TABLE IF NOT EXISTS ownership (
                route_key TEXT PRIMARY KEY,
                route TEXT NOT NULL,
                operation_id INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
            INSERT OR IGNORE INTO meta (key, value) VALUES ('schemaVersion', '1');
            """);
    }

    public long Begin(ChangeSet changeSet, DateTimeOffset? confirmDeadline = null, long? undoOf = null)
    {
        lock (_lock)
        {
            using SqliteCommand cmd = Command("""
                INSERT INTO operations (time, origin, change_set, status, confirm_deadline, undo_of)
                VALUES ($time, $origin, $set, $status, $deadline, $undoOf);
                SELECT last_insert_rowid();
                """);
            cmd.Parameters.AddWithValue("$time", _time.GetUtcNow().ToString("O"));
            cmd.Parameters.AddWithValue("$origin", changeSet.Origin);
            cmd.Parameters.AddWithValue("$set", JsonSerializer.Serialize(changeSet, RouteDeckJson.Options));
            cmd.Parameters.AddWithValue("$status", OperationStatus.Running.ToString());
            cmd.Parameters.AddWithValue("$deadline", (object?)confirmDeadline?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$undoOf", (object?)undoOf ?? DBNull.Value);
            return (long)cmd.ExecuteScalar()!;
        }
    }

    /// <summary>Records that step <paramref name="index"/> completed and updates ownership in the same transaction.</summary>
    public void StepApplied(long id, int index, RouteOperation op)
    {
        lock (_lock)
        {
            using SqliteTransaction tx = _db.BeginTransaction();
            using (SqliteCommand cmd = Command("UPDATE operations SET applied_steps = $n WHERE id = $id", tx))
            {
                cmd.Parameters.AddWithValue("$n", index + 1);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            UpdateOwnership(op, id, tx);
            tx.Commit();
        }
    }

    public void SetStatus(long id, OperationStatus status, string? error = null)
    {
        lock (_lock)
        {
            using SqliteCommand cmd = Command("UPDATE operations SET status = $s, error = COALESCE($e, error) WHERE id = $id");
            cmd.Parameters.AddWithValue("$s", status.ToString());
            cmd.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Rolled-back steps no longer happened: reset progress and ownership to what is actually in place.</summary>
    public void StepsRolledBack(long id, IEnumerable<RouteOperation> rolledBack)
    {
        lock (_lock)
        {
            using SqliteTransaction tx = _db.BeginTransaction();
            foreach (RouteOperation op in rolledBack.Reverse()) UpdateOwnership(op.Inverse(), id, tx);
            using (SqliteCommand cmd = Command("UPDATE operations SET applied_steps = 0 WHERE id = $id", tx))
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void MarkUndone(long id, long undoneBy)
    {
        lock (_lock)
        {
            using SqliteCommand cmd = Command("UPDATE operations SET undone_by = $by, status = CASE WHEN status IN ('Applied','PendingConfirm') THEN 'Undone' ELSE status END WHERE id = $id");
            cmd.Parameters.AddWithValue("$by", undoneBy);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public OperationRecord? Get(long id) => Query("WHERE id = $id", ("$id", id)).FirstOrDefault();

    public IReadOnlyList<OperationRecord> Recent(int count) => Query("ORDER BY id DESC LIMIT $n", ("$n", count));

    public IReadOnlyList<OperationRecord> Unfinished() => Query("WHERE status IN ('Running','PendingConfirm') ORDER BY id");

    /// <summary>Later operations that are still in effect, to detect undo conflicts.</summary>
    public IReadOnlyList<OperationRecord> EffectiveAfter(long id) =>
        Query("WHERE id > $id AND undone_by IS NULL AND status IN ('Applied','PendingConfirm','Running') ORDER BY id", ("$id", id));

    public IReadOnlyDictionary<RouteKey, OwnedRoute> Owned()
    {
        lock (_lock)
        {
            var result = new Dictionary<RouteKey, OwnedRoute>();
            using SqliteCommand cmd = Command("SELECT route, operation_id FROM ownership");
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                RouteSpec spec = JsonSerializer.Deserialize<RouteSpec>(reader.GetString(0), RouteDeckJson.Options)!;
                result[spec.Key] = new OwnedRoute(spec, reader.GetInt64(1));
            }
            return result;
        }
    }

    public void Dispose() => _db.Dispose();

    private void UpdateOwnership(RouteOperation op, long id, SqliteTransaction tx)
    {
        switch (op.Kind)
        {
            case RouteOperationKind.Add:
                using (SqliteCommand cmd = Command("INSERT OR REPLACE INTO ownership (route_key, route, operation_id) VALUES ($k, $r, $id)", tx))
                {
                    cmd.Parameters.AddWithValue("$k", KeyText(op.Route.Key));
                    cmd.Parameters.AddWithValue("$r", JsonSerializer.Serialize(op.Route, RouteDeckJson.Options));
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.ExecuteNonQuery();
                }
                break;
            case RouteOperationKind.Remove:
                using (SqliteCommand cmd = Command("DELETE FROM ownership WHERE route_key = $k", tx))
                {
                    cmd.Parameters.AddWithValue("$k", KeyText(op.Route.Key));
                    cmd.ExecuteNonQuery();
                }
                break;
            case RouteOperationKind.SetMetric:
                using (SqliteCommand cmd = Command("UPDATE ownership SET route = $r WHERE route_key = $k", tx))
                {
                    cmd.Parameters.AddWithValue("$k", KeyText(op.Route.Key));
                    cmd.Parameters.AddWithValue("$r", JsonSerializer.Serialize(op.Route with { Metric = op.NewMetric!.Value }, RouteDeckJson.Options));
                    cmd.ExecuteNonQuery();
                }
                break;
        }
    }

    private List<OperationRecord> Query(string where, params (string Name, object Value)[] parameters)
    {
        lock (_lock)
        {
            using SqliteCommand cmd = Command(
                "SELECT id, time, change_set, applied_steps, status, error, confirm_deadline, undo_of, undone_by FROM operations " + where);
            foreach ((string name, object value) in parameters) cmd.Parameters.AddWithValue(name, value);
            using SqliteDataReader r = cmd.ExecuteReader();
            var list = new List<OperationRecord>();
            while (r.Read())
            {
                list.Add(new OperationRecord(
                    r.GetInt64(0),
                    DateTimeOffset.Parse(r.GetString(1)),
                    JsonSerializer.Deserialize<ChangeSet>(r.GetString(2), RouteDeckJson.Options)!,
                    r.GetInt32(3),
                    Enum.Parse<OperationStatus>(r.GetString(4)),
                    r.IsDBNull(5) ? null : r.GetString(5),
                    r.IsDBNull(6) ? null : DateTimeOffset.Parse(r.GetString(6)),
                    r.IsDBNull(7) ? null : r.GetInt64(7),
                    r.IsDBNull(8) ? null : r.GetInt64(8)));
            }
            return list;
        }
    }

    private static string KeyText(RouteKey key) => $"{key.Destination}|{key.InterfaceLuid}|{key.NextHop}";

    private SqliteCommand Command(string sql, SqliteTransaction? tx = null)
    {
        SqliteCommand cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    private void Exec(string sql)
    {
        using SqliteCommand cmd = Command(sql);
        cmd.ExecuteNonQuery();
    }
}
