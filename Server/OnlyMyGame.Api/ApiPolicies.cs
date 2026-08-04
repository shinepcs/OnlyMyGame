using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OnlyMyGame.Core;

namespace OnlyMyGame.Api;

public enum RuleRequestClaimKind
{
    Claimed,
    Replay,
    InProgress,
    DailyLimitReached,
    GlobalDailyLimitReached,
    RetryCooldown,
    ResponseUnavailable,
    KeyMismatch
}

public enum SessionIssueKind
{
    Issued,
    ActiveLimitReached
}

public sealed class SessionIssue
{
    public SessionIssueKind Kind { get; init; }
    public string? Token { get; init; }
    public int ExpiresInSeconds { get; init; }
}

public sealed class ResponseTokenUsage
{
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public long? TotalTokens { get; set; }
    public long? CachedInputTokens { get; set; }
    public long? CacheWriteTokens { get; set; }
    public long? ReasoningTokens { get; set; }

    public void Add(ResponseTokenUsage? other)
    {
        if (other == null) return;
        InputTokens = AddNullable(InputTokens, other.InputTokens);
        OutputTokens = AddNullable(OutputTokens, other.OutputTokens);
        TotalTokens = AddNullable(TotalTokens, other.TotalTokens);
        CachedInputTokens = AddNullable(CachedInputTokens, other.CachedInputTokens);
        CacheWriteTokens = AddNullable(CacheWriteTokens, other.CacheWriteTokens);
        ReasoningTokens = AddNullable(ReasoningTokens, other.ReasoningTokens);
    }

    private static long? AddNullable(long? left, long? right)
    {
        if (!left.HasValue) return right;
        if (!right.HasValue) return left;
        return left.Value > long.MaxValue - right.Value ? long.MaxValue : left.Value + right.Value;
    }
}

public sealed class DataPruneResult
{
    public bool Performed { get; init; }
    public int RequestLogsDeleted { get; init; }
    public int AttemptsDeleted { get; init; }
    public int SessionsDeleted { get; init; }
}

public sealed class RuleRequestClaim
{
    public RuleRequestClaimKind Kind { get; init; }
    public string? LeaseToken { get; init; }
    public string? ResponseJson { get; init; }
    public string? PreviousError { get; init; }
    public int RetryAfterSeconds { get; init; }
}

public static class ApiPolicies
{
    public const string ProductionWebOrigin = "https://shinepcs.github.io";
    public const string ApiVersion = "v1";
    public const string RuleCompatibilityVersion = "rules-v2-strict-2026-08";
    public static readonly TimeSpan RequestLeaseDuration = TimeSpan.FromSeconds(90);

    public static string ComputeRuleRequestHash(GameSnapshotV1 snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var canonicalGameplayState = new
        {
            compatibilityVersion = RuleCompatibilityVersion,
            snapshot.runId,
            snapshot.turn,
            snapshot.seed,
            snapshot.luck,
            snapshot.playerKills,
            snapshot.outcome,
            snapshot.completedContractId,
            snapshot.map,
            snapshot.entities,
            snapshot.buildings,
            snapshot.factions,
            snapshot.actionStats,
            snapshot.activeRules,
            snapshot.victoryContracts,
            snapshot.dynamicActions,
            snapshot.ruleState,
            snapshot.catalogHash
        };
        var options = new JsonSerializerOptions { IncludeFields = true };
        var json = JsonSerializer.Serialize(canonicalGameplayState, options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    public static void EnsureRequestLogSchema(SqliteConnection database)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (database.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("The request database must be open before migration.");

        using var transaction = database.BeginTransaction(deferred: false);
        using (var create = new SqliteCommand(
            "CREATE TABLE IF NOT EXISTS request_log (id INTEGER PRIMARY KEY, day TEXT NOT NULL, ip_hash TEXT NOT NULL, request_key TEXT NOT NULL UNIQUE, created_utc TEXT NOT NULL, completed_utc TEXT, latency_ms INTEGER, last_latency_ms INTEGER, valid INTEGER, error TEXT, response_json TEXT, request_hash TEXT, lease_token TEXT, attempt_count INTEGER NOT NULL DEFAULT 1, attempt_day TEXT, compatibility_version TEXT, input_tokens INTEGER, output_tokens INTEGER, total_tokens INTEGER, cached_input_tokens INTEGER, cache_write_tokens INTEGER, reasoning_tokens INTEGER, upstream_attempts INTEGER NOT NULL DEFAULT 0, validation_failures INTEGER NOT NULL DEFAULT 0);",
            database,
            transaction))
        {
            create.ExecuteNonQuery();
        }

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var pragma = new SqliteCommand("PRAGMA table_info(request_log)", database, transaction))
        using (var reader = pragma.ExecuteReader())
        {
            while (reader.Read()) columns.Add(reader.GetString(1));
        }

        AddColumnIfMissing(database, transaction, columns, "response_json", "TEXT");
        AddColumnIfMissing(database, transaction, columns, "request_hash", "TEXT");
        AddColumnIfMissing(database, transaction, columns, "lease_token", "TEXT");
        AddColumnIfMissing(database, transaction, columns, "attempt_count", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing(database, transaction, columns, "attempt_day", "TEXT");
        AddColumnIfMissing(database, transaction, columns, "compatibility_version", "TEXT");
        AddColumnIfMissing(database, transaction, columns, "completed_utc", "TEXT");
        AddColumnIfMissing(database, transaction, columns, "last_latency_ms", "INTEGER");
        AddColumnIfMissing(database, transaction, columns, "input_tokens", "INTEGER");
        AddColumnIfMissing(database, transaction, columns, "output_tokens", "INTEGER");
        AddColumnIfMissing(database, transaction, columns, "total_tokens", "INTEGER");
        AddColumnIfMissing(database, transaction, columns, "cached_input_tokens", "INTEGER");
        AddColumnIfMissing(database, transaction, columns, "cache_write_tokens", "INTEGER");
        AddColumnIfMissing(database, transaction, columns, "reasoning_tokens", "INTEGER");
        AddColumnIfMissing(database, transaction, columns, "upstream_attempts", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(database, transaction, columns, "validation_failures", "INTEGER NOT NULL DEFAULT 0");
        using (var attempts = new SqliteCommand(
            "CREATE TABLE IF NOT EXISTS request_attempt (id INTEGER PRIMARY KEY, day TEXT NOT NULL, ip_hash TEXT NOT NULL, request_key TEXT NOT NULL, created_utc TEXT NOT NULL); CREATE INDEX IF NOT EXISTS ix_request_attempt_daily_quota ON request_attempt(day, ip_hash); CREATE INDEX IF NOT EXISTS ix_request_attempt_retention ON request_attempt(created_utc);",
            database,
            transaction))
        {
            attempts.ExecuteNonQuery();
        }
        using (var sessions = new SqliteCommand(
            "CREATE TABLE IF NOT EXISTS game_session (token_hash TEXT PRIMARY KEY, run_id TEXT NOT NULL, ip_hash TEXT NOT NULL, created_utc TEXT NOT NULL, expires_utc TEXT NOT NULL); CREATE INDEX IF NOT EXISTS ix_game_session_active_ip ON game_session(ip_hash, expires_utc); CREATE INDEX IF NOT EXISTS ix_game_session_expiration ON game_session(expires_utc);",
            database,
            transaction))
        {
            sessions.ExecuteNonQuery();
        }
        using (var maintenance = new SqliteCommand(
            "CREATE TABLE IF NOT EXISTS service_maintenance (name TEXT PRIMARY KEY, performed_utc TEXT NOT NULL)",
            database,
            transaction))
        {
            maintenance.ExecuteNonQuery();
        }
        using var indexes = new SqliteCommand(
            "CREATE INDEX IF NOT EXISTS ix_request_log_daily_quota ON request_log(day, ip_hash); CREATE INDEX IF NOT EXISTS ix_request_log_retention ON request_log(valid, completed_utc, created_utc);",
            database,
            transaction);
        indexes.ExecuteNonQuery();
        transaction.Commit();
    }

    public static ResponseTokenUsage ParseResponseUsage(JsonElement responseRoot)
    {
        var result = new ResponseTokenUsage();
        if (!responseRoot.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return result;

        result.InputTokens = ReadNonNegativeInt64(usage, "input_tokens");
        result.OutputTokens = ReadNonNegativeInt64(usage, "output_tokens");
        result.TotalTokens = ReadNonNegativeInt64(usage, "total_tokens");
        if (usage.TryGetProperty("input_tokens_details", out var inputDetails)
            && inputDetails.ValueKind == JsonValueKind.Object)
        {
            result.CachedInputTokens = ReadNonNegativeInt64(inputDetails, "cached_tokens");
            result.CacheWriteTokens = ReadNonNegativeInt64(inputDetails, "cache_write_tokens");
        }
        if (usage.TryGetProperty("output_tokens_details", out var outputDetails)
            && outputDetails.ValueKind == JsonValueKind.Object)
            result.ReasoningTokens = ReadNonNegativeInt64(outputDetails, "reasoning_tokens");
        if (!result.TotalTokens.HasValue && result.InputTokens.HasValue && result.OutputTokens.HasValue)
            result.TotalTokens = result.InputTokens.Value > long.MaxValue - result.OutputTokens.Value
                ? long.MaxValue
                : result.InputTokens.Value + result.OutputTokens.Value;
        return result;
    }

    public static DataPruneResult PruneExpiredData(
        SqliteConnection database,
        DateTime utcNow,
        int retentionDays = 30,
        bool force = false)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (database.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("The request database must be open before pruning.");

        retentionDays = NormalizeRetentionDays(retentionDays);
        var now = utcNow.ToUniversalTime();
        var nowText = now.ToString("O", CultureInfo.InvariantCulture);
        var cutoffText = now.AddDays(-retentionDays).ToString("O", CultureInfo.InvariantCulture);
        if (!force)
        {
            using var preflight = new SqliteCommand(
                "SELECT performed_utc FROM service_maintenance WHERE name='retention_prune'",
                database);
            if (MaintenanceRanRecently(preflight.ExecuteScalar() as string, now))
                return new DataPruneResult { Performed = false };
        }

        using var transaction = database.BeginTransaction(deferred: false);
        if (!force)
        {
            using var lastRun = new SqliteCommand(
                "SELECT performed_utc FROM service_maintenance WHERE name='retention_prune'",
                database,
                transaction);
            var previousText = lastRun.ExecuteScalar() as string;
            if (MaintenanceRanRecently(previousText, now))
            {
                transaction.Commit();
                return new DataPruneResult { Performed = false };
            }
        }

        int requestLogsDeleted;
        using (var pruneLogs = new SqliteCommand(
            "DELETE FROM request_log WHERE (valid IS NOT NULL AND COALESCE(completed_utc, created_utc) < $cutoff) OR (valid IS NULL AND created_utc < $cutoff)",
            database,
            transaction))
        {
            pruneLogs.Parameters.AddWithValue("$cutoff", cutoffText);
            requestLogsDeleted = pruneLogs.ExecuteNonQuery();
        }

        int attemptsDeleted;
        using (var pruneAttempts = new SqliteCommand(
            "DELETE FROM request_attempt WHERE created_utc < $cutoff",
            database,
            transaction))
        {
            pruneAttempts.Parameters.AddWithValue("$cutoff", cutoffText);
            attemptsDeleted = pruneAttempts.ExecuteNonQuery();
        }

        int sessionsDeleted;
        using (var pruneSessions = new SqliteCommand(
            "DELETE FROM game_session WHERE expires_utc <= $now",
            database,
            transaction))
        {
            pruneSessions.Parameters.AddWithValue("$now", nowText);
            sessionsDeleted = pruneSessions.ExecuteNonQuery();
        }

        using (var mark = new SqliteCommand(
            "INSERT INTO service_maintenance(name,performed_utc) VALUES('retention_prune',$now) ON CONFLICT(name) DO UPDATE SET performed_utc=excluded.performed_utc",
            database,
            transaction))
        {
            mark.Parameters.AddWithValue("$now", nowText);
            mark.ExecuteNonQuery();
        }

        transaction.Commit();
        return new DataPruneResult
        {
            Performed = true,
            RequestLogsDeleted = requestLogsDeleted,
            AttemptsDeleted = attemptsDeleted,
            SessionsDeleted = sessionsDeleted
        };
    }

    public static int NormalizeRetentionDays(int retentionDays) => Math.Clamp(retentionDays, 30, 365);

    public static RuleRequestClaim ClaimRuleRequest(
        SqliteConnection database,
        string day,
        string ipHash,
        string requestKey,
        string requestHash,
        int dailyLimit,
        DateTime utcNow,
        int maxAttemptsPerKey = 3,
        int retryCooldownSeconds = 120,
        int globalDailyLimit = 600)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (database.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("The request database must be open before claiming a request.");

        dailyLimit = Math.Clamp(dailyLimit, 1, 1_000);
        maxAttemptsPerKey = Math.Clamp(maxAttemptsPerKey, 2, 10);
        retryCooldownSeconds = Math.Clamp(retryCooldownSeconds, 30, 900);
        globalDailyLimit = Math.Clamp(globalDailyLimit, 1, 100_000);
        var nowText = utcNow.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        using var transaction = database.BeginTransaction(deferred: false);

        int? valid = null;
        string previousError = string.Empty;
        string responseJson = string.Empty;
        string storedHash = string.Empty;
        string createdUtc = string.Empty;
        var attemptCount = 1;
        string attemptDay = string.Empty;
        string compatibilityVersion = string.Empty;
        var found = false;
        using (var lookup = new SqliteCommand(
            "SELECT valid, error, response_json, request_hash, created_utc, attempt_count, attempt_day, compatibility_version FROM request_log WHERE request_key=$key",
            database,
            transaction))
        {
            lookup.Parameters.AddWithValue("$key", requestKey);
            using var reader = lookup.ExecuteReader();
            if (reader.Read())
            {
                found = true;
                valid = reader.IsDBNull(0) ? null : Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
                previousError = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                responseJson = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                storedHash = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                createdUtc = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                attemptCount = reader.IsDBNull(5) ? 1 : Math.Max(1, reader.GetInt32(5));
                attemptDay = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);
                compatibilityVersion = reader.IsDBNull(7) ? string.Empty : reader.GetString(7);
            }
        }

        var compatibilityChanged = found && !string.Equals(compatibilityVersion, RuleCompatibilityVersion, StringComparison.Ordinal);
        if (compatibilityChanged)
        {
            valid = 0;
            previousError = "COMPATIBILITY_VERSION_CHANGED";
            responseJson = string.Empty;
            storedHash = requestHash;
            attemptCount = 0;
            attemptDay = day;
        }

        if (found && !compatibilityChanged && !string.IsNullOrWhiteSpace(storedHash)
            && !string.Equals(storedHash, requestHash, StringComparison.Ordinal))
        {
            transaction.Commit();
            return new RuleRequestClaim { Kind = RuleRequestClaimKind.KeyMismatch };
        }

        if (found && valid == 1)
        {
            if (!string.IsNullOrWhiteSpace(responseJson))
            {
                transaction.Commit();
                return new RuleRequestClaim
                {
                    Kind = RuleRequestClaimKind.Replay,
                    ResponseJson = responseJson
                };
            }

            using var invalidateLegacy = new SqliteCommand(
                "UPDATE request_log SET valid=0, error='STORED_RESPONSE_UNAVAILABLE', response_json=NULL, lease_token=NULL WHERE request_key=$key",
                database,
                transaction);
            invalidateLegacy.Parameters.AddWithValue("$key", requestKey);
            invalidateLegacy.ExecuteNonQuery();
            transaction.Commit();
            return new RuleRequestClaim
            {
                Kind = RuleRequestClaimKind.ResponseUnavailable,
                PreviousError = "STORED_RESPONSE_UNAVAILABLE"
            };
        }

        if (found && valid == null)
        {
            var leaseIsFresh = DateTime.TryParse(
                    createdUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var leaseStarted)
                && utcNow.ToUniversalTime() - leaseStarted.ToUniversalTime() < RequestLeaseDuration;
            if (leaseIsFresh)
            {
                transaction.Commit();
                return new RuleRequestClaim { Kind = RuleRequestClaimKind.InProgress };
            }
        }

        var attemptsInWindow = found && string.Equals(attemptDay, day, StringComparison.Ordinal) ? attemptCount : 0;
        if (found && !compatibilityChanged && attemptsInWindow >= maxAttemptsPerKey)
        {
            var elapsed = DateTime.TryParse(
                    createdUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var lastAttempt)
                ? utcNow.ToUniversalTime() - lastAttempt.ToUniversalTime()
                : TimeSpan.MaxValue;
            var cooldown = TimeSpan.FromSeconds(retryCooldownSeconds);
            if (elapsed < cooldown)
            {
                transaction.Commit();
                return new RuleRequestClaim
                {
                    Kind = RuleRequestClaimKind.RetryCooldown,
                    RetryAfterSeconds = Math.Max(1, (int)Math.Ceiling((cooldown - elapsed).TotalSeconds))
                };
            }
            attemptsInWindow = 0;
        }

        using (var quota = new SqliteCommand(
            "SELECT COUNT(*) FROM request_attempt WHERE day=$day AND ip_hash=$ip",
            database,
            transaction))
        {
            quota.Parameters.AddWithValue("$day", day);
            quota.Parameters.AddWithValue("$ip", ipHash);
            if ((long)(quota.ExecuteScalar() ?? 0L) >= dailyLimit)
            {
                transaction.Commit();
                return new RuleRequestClaim { Kind = RuleRequestClaimKind.DailyLimitReached };
            }
        }

        using (var globalQuota = new SqliteCommand(
            "SELECT COUNT(*) FROM request_attempt WHERE day=$day",
            database,
            transaction))
        {
            globalQuota.Parameters.AddWithValue("$day", day);
            if ((long)(globalQuota.ExecuteScalar() ?? 0L) >= globalDailyLimit)
            {
                transaction.Commit();
                return new RuleRequestClaim { Kind = RuleRequestClaimKind.GlobalDailyLimitReached };
            }
        }

        var leaseToken = Guid.NewGuid().ToString("N");
        if (found)
        {
            using var retry = new SqliteCommand(
                "UPDATE request_log SET created_utc=$utc, completed_utc=NULL, last_latency_ms=NULL, valid=NULL, error='', response_json=NULL, request_hash=$hash, lease_token=$lease, attempt_count=$attempts, attempt_day=$attemptDay, compatibility_version=$compat WHERE request_key=$key",
                database,
                transaction);
            retry.Parameters.AddWithValue("$utc", nowText);
            retry.Parameters.AddWithValue("$hash", requestHash);
            retry.Parameters.AddWithValue("$lease", leaseToken);
            retry.Parameters.AddWithValue("$attempts", attemptsInWindow + 1);
            retry.Parameters.AddWithValue("$attemptDay", day);
            retry.Parameters.AddWithValue("$compat", RuleCompatibilityVersion);
            retry.Parameters.AddWithValue("$key", requestKey);
            retry.ExecuteNonQuery();
        }
        else
        {
            using var insert = new SqliteCommand(
                "INSERT INTO request_log(day,ip_hash,request_key,created_utc,latency_ms,valid,error,response_json,request_hash,lease_token,attempt_count,attempt_day,compatibility_version) VALUES($day,$ip,$key,$utc,NULL,NULL,'',NULL,$hash,$lease,1,$day,$compat)",
                database,
                transaction);
            insert.Parameters.AddWithValue("$day", day);
            insert.Parameters.AddWithValue("$ip", ipHash);
            insert.Parameters.AddWithValue("$key", requestKey);
            insert.Parameters.AddWithValue("$utc", nowText);
            insert.Parameters.AddWithValue("$hash", requestHash);
            insert.Parameters.AddWithValue("$lease", leaseToken);
            insert.Parameters.AddWithValue("$compat", RuleCompatibilityVersion);
            insert.ExecuteNonQuery();
        }

        using (var recordAttempt = new SqliteCommand(
            "INSERT INTO request_attempt(day,ip_hash,request_key,created_utc) VALUES($day,$ip,$key,$utc)",
            database,
            transaction))
        {
            recordAttempt.Parameters.AddWithValue("$day", day);
            recordAttempt.Parameters.AddWithValue("$ip", ipHash);
            recordAttempt.Parameters.AddWithValue("$key", requestKey);
            recordAttempt.Parameters.AddWithValue("$utc", nowText);
            recordAttempt.ExecuteNonQuery();
        }

        transaction.Commit();
        return new RuleRequestClaim
        {
            Kind = RuleRequestClaimKind.Claimed,
            LeaseToken = leaseToken,
            PreviousError = previousError
        };
    }

    public static bool CompleteRuleRequest(
        SqliteConnection database,
        string requestKey,
        string leaseToken,
        int latencyMilliseconds,
        string? responseJson,
        string? error,
        ResponseTokenUsage? usage = null,
        int upstreamAttempts = 0,
        int validationFailures = 0,
        DateTime? completedUtc = null)
    {
        var succeeded = string.IsNullOrWhiteSpace(error) && !string.IsNullOrWhiteSpace(responseJson);
        using var update = new SqliteCommand(
            """
            UPDATE request_log SET
                completed_utc=$completed,
                latency_ms=COALESCE(latency_ms,0)+$latency,
                last_latency_ms=$latency,
                valid=$valid,
                error=$error,
                response_json=$response,
                lease_token=NULL,
                input_tokens=CASE WHEN $input IS NULL THEN input_tokens ELSE COALESCE(input_tokens,0)+$input END,
                output_tokens=CASE WHEN $output IS NULL THEN output_tokens ELSE COALESCE(output_tokens,0)+$output END,
                total_tokens=CASE WHEN $total IS NULL THEN total_tokens ELSE COALESCE(total_tokens,0)+$total END,
                cached_input_tokens=CASE WHEN $cached IS NULL THEN cached_input_tokens ELSE COALESCE(cached_input_tokens,0)+$cached END,
                cache_write_tokens=CASE WHEN $cacheWrite IS NULL THEN cache_write_tokens ELSE COALESCE(cache_write_tokens,0)+$cacheWrite END,
                reasoning_tokens=CASE WHEN $reasoning IS NULL THEN reasoning_tokens ELSE COALESCE(reasoning_tokens,0)+$reasoning END,
                upstream_attempts=COALESCE(upstream_attempts,0)+$upstreamAttempts,
                validation_failures=COALESCE(validation_failures,0)+$validationFailures
            WHERE request_key=$key AND lease_token=$lease AND valid IS NULL
            """,
            database);
        var latency = Math.Max(0, latencyMilliseconds);
        update.Parameters.AddWithValue("$completed", (completedUtc ?? DateTime.UtcNow).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        update.Parameters.AddWithValue("$latency", latency);
        update.Parameters.AddWithValue("$valid", succeeded ? 1 : 0);
        update.Parameters.AddWithValue("$error", succeeded ? string.Empty : (error ?? "INTERNAL_ERROR"));
        update.Parameters.AddWithValue("$response", succeeded ? responseJson! : DBNull.Value);
        update.Parameters.AddWithValue("$input", usage?.InputTokens is long input ? input : DBNull.Value);
        update.Parameters.AddWithValue("$output", usage?.OutputTokens is long output ? output : DBNull.Value);
        update.Parameters.AddWithValue("$total", usage?.TotalTokens is long total ? total : DBNull.Value);
        update.Parameters.AddWithValue("$cached", usage?.CachedInputTokens is long cached ? cached : DBNull.Value);
        update.Parameters.AddWithValue("$cacheWrite", usage?.CacheWriteTokens is long cacheWrite ? cacheWrite : DBNull.Value);
        update.Parameters.AddWithValue("$reasoning", usage?.ReasoningTokens is long reasoning ? reasoning : DBNull.Value);
        update.Parameters.AddWithValue("$upstreamAttempts", Math.Max(0, upstreamAttempts));
        update.Parameters.AddWithValue("$validationFailures", Math.Max(0, validationFailures));
        update.Parameters.AddWithValue("$key", requestKey);
        update.Parameters.AddWithValue("$lease", leaseToken);
        return update.ExecuteNonQuery() == 1;
    }

    public static void InvalidateStoredResponse(SqliteConnection database, string requestKey)
    {
        using var update = new SqliteCommand(
            "UPDATE request_log SET valid=0, error='STORED_RESPONSE_INVALID', response_json=NULL, lease_token=NULL WHERE request_key=$key AND valid=1",
            database);
        update.Parameters.AddWithValue("$key", requestKey);
        update.ExecuteNonQuery();
    }

    public static SessionIssue IssueSession(
        SqliteConnection database,
        string runId,
        string ipHash,
        DateTime utcNow,
        int maxActiveSessions = 2,
        int lifetimeSeconds = 3600)
    {
        maxActiveSessions = Math.Clamp(maxActiveSessions, 1, 10);
        lifetimeSeconds = Math.Clamp(lifetimeSeconds, 300, 86_400);
        var now = utcNow.ToUniversalTime();
        var nowText = now.ToString("O", CultureInfo.InvariantCulture);
        var expiresText = now.AddSeconds(lifetimeSeconds).ToString("O", CultureInfo.InvariantCulture);
        using var transaction = database.BeginTransaction(deferred: false);
        using (var prune = new SqliteCommand("DELETE FROM game_session WHERE expires_utc <= $now", database, transaction))
        {
            prune.Parameters.AddWithValue("$now", nowText);
            prune.ExecuteNonQuery();
        }
        using (var replaceSameRun = new SqliteCommand(
            "DELETE FROM game_session WHERE ip_hash=$ip AND run_id=$run",
            database,
            transaction))
        {
            replaceSameRun.Parameters.AddWithValue("$ip", ipHash);
            replaceSameRun.Parameters.AddWithValue("$run", runId);
            replaceSameRun.ExecuteNonQuery();
        }

        using (var count = new SqliteCommand(
            "SELECT COUNT(*) FROM game_session WHERE ip_hash=$ip AND expires_utc>$now",
            database,
            transaction))
        {
            count.Parameters.AddWithValue("$ip", ipHash);
            count.Parameters.AddWithValue("$now", nowText);
            if ((long)(count.ExecuteScalar() ?? 0L) >= maxActiveSessions)
            {
                transaction.Commit();
                return new SessionIssue { Kind = SessionIssueKind.ActiveLimitReached };
            }
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var tokenHash = HashToken(token);
        using (var insert = new SqliteCommand(
            "INSERT INTO game_session(token_hash,run_id,ip_hash,created_utc,expires_utc) VALUES($token,$run,$ip,$created,$expires)",
            database,
            transaction))
        {
            insert.Parameters.AddWithValue("$token", tokenHash);
            insert.Parameters.AddWithValue("$run", runId);
            insert.Parameters.AddWithValue("$ip", ipHash);
            insert.Parameters.AddWithValue("$created", nowText);
            insert.Parameters.AddWithValue("$expires", expiresText);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
        return new SessionIssue { Kind = SessionIssueKind.Issued, Token = token, ExpiresInSeconds = lifetimeSeconds };
    }

    public static bool ValidateSession(
        SqliteConnection database,
        string token,
        string runId,
        string ipHash,
        DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        using var command = new SqliteCommand(
            "SELECT COUNT(*) FROM game_session WHERE token_hash=$token AND run_id=$run AND ip_hash=$ip AND expires_utc>$now",
            database);
        command.Parameters.AddWithValue("$token", HashToken(token));
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$ip", ipHash);
        command.Parameters.AddWithValue("$now", utcNow.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        return (long)(command.ExecuteScalar() ?? 0L) == 1;
    }

    private static string HashToken(string token)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    public static string[] ParseAllowedOrigins(string? configuredOrigins)
    {
        var source = string.IsNullOrWhiteSpace(configuredOrigins) ? ProductionWebOrigin : configuredOrigins;
        var origins = source
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeOrigin)
            .Where(origin => origin != null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return origins.Length == 0 ? new[] { ProductionWebOrigin } : origins;
    }

    public static IPAddress[] ParseTrustedProxies(string? configuredProxies)
    {
        if (string.IsNullOrWhiteSpace(configuredProxies)) return Array.Empty<IPAddress>();
        return configuredProxies
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(candidate => IPAddress.TryParse(candidate, out var address) ? address : null)
            .Where(address => address != null)
            .Cast<IPAddress>()
            .Distinct()
            .ToArray();
    }

    public static IReadOnlyList<string> ValidateSnapshot(GameSnapshotV1? snapshot)
    {
        var errors = new List<string>();
        if (snapshot == null)
        {
            errors.Add("SNAPSHOT_REQUIRED");
            return errors;
        }

        if (string.IsNullOrWhiteSpace(snapshot.runId) || snapshot.runId.Length > 128) errors.Add("RUN_ID_REQUIRED");
        if (snapshot.turn < 0 || snapshot.turn > 1_000_000) errors.Add("TURN_OUT_OF_RANGE");
        if (string.IsNullOrWhiteSpace(snapshot.catalogHash) || snapshot.catalogHash.Length > 128) errors.Add("CATALOG_HASH_REQUIRED");
        ValidateCollection(snapshot.map, 1, 4_096, "MAP", errors);
        ValidateCollection(snapshot.entities, 0, 1_024, "ENTITIES", errors);
        ValidateCollection(snapshot.buildings, 0, 1_024, "BUILDINGS", errors);
        ValidateCollection(snapshot.factions, 1, 64, "FACTIONS", errors);
        ValidateCollection(snapshot.actionStats, 0, 64, "ACTION_STATS", errors);
        ValidateCollection(snapshot.activeRules, 0, RuleLimits.MaxStoredRules, "ACTIVE_RULES", errors);
        ValidateCollection(snapshot.victoryContracts, 0, 3, "VICTORY_CONTRACTS", errors);
        ValidateCollection(snapshot.dynamicActions, 0, 64, "DYNAMIC_ACTIONS", errors);
        ValidateCollection(snapshot.ruleState, 0, 512, "RULE_STATE", errors);
        ValidateCollection(snapshot.journal, 0, 512, "JOURNAL", errors);
        if (snapshot.factions != null && !snapshot.factions.Any(faction => faction != null && faction.kind == FactionKind.Player))
            errors.Add("PLAYER_FACTION_REQUIRED");
        return errors;
    }

    public static IReadOnlyList<string> ValidateRuleSet(RuleSetV1? ruleSet)
    {
        var errors = new List<string>();
        if (ruleSet == null)
        {
            errors.Add("RULESET_REQUIRED");
            return errors;
        }

        if (!string.Equals(ruleSet.schemaVersion, "v1", StringComparison.Ordinal)) errors.Add("SCHEMA_VERSION_INVALID");
        if (string.IsNullOrWhiteSpace(ruleSet.requestId) || ruleSet.requestId.Length > 128) errors.Add("REQUEST_ID_INVALID");
        if (string.IsNullOrWhiteSpace(ruleSet.koreanSummary) || ruleSet.koreanSummary.Length > 2_000) errors.Add("SUMMARY_INVALID");
        ValidateCollection(ruleSet.changes, 1, 3, "CHANGES", errors);
        ValidateCollection(ruleSet.actions, 0, 16, "ACTIONS", errors);
        ValidateCollection(ruleSet.victoryContracts, 0, 3, "VICTORY_CONTRACTS", errors);

        foreach (var rule in ruleSet.changes ?? new List<RuleNodeV1>())
        {
            if (rule == null)
            {
                errors.Add("RULE_NULL");
                continue;
            }

            if (string.IsNullOrWhiteSpace(rule.id) || rule.id.Length > 128) errors.Add("RULE_ID_INVALID");
            if (string.IsNullOrWhiteSpace(rule.name) || rule.name.Length > 200) errors.Add("RULE_NAME_INVALID:" + rule.id);
            if (string.IsNullOrWhiteSpace(rule.description) || rule.description.Length > 2_000) errors.Add("RULE_DESCRIPTION_INVALID:" + rule.id);
            ValidateCollection(rule.effects, 1, 16, "RULE_EFFECTS:" + rule.id, errors);
            if (rule.effects != null && rule.effects.Any(effect => effect == null)) errors.Add("RULE_EFFECT_NULL:" + rule.id);
        }

        foreach (var action in ruleSet.actions ?? new List<DynamicActionV1>())
        {
            if (action == null)
            {
                errors.Add("ACTION_NULL");
                continue;
            }

            if (string.IsNullOrWhiteSpace(action.id) || action.id.Length > 128) errors.Add("ACTION_ID_INVALID");
            if (string.IsNullOrWhiteSpace(action.name) || action.name.Length > 200) errors.Add("ACTION_NAME_INVALID:" + action.id);
            ValidateCollection(action.effects, 1, 16, "ACTION_EFFECTS:" + action.id, errors);
            if (action.effects != null && action.effects.Any(effect => effect == null)) errors.Add("ACTION_EFFECT_NULL:" + action.id);
        }

        foreach (var contract in ruleSet.victoryContracts ?? new List<VictoryContractV1>())
        {
            if (contract == null)
            {
                errors.Add("VICTORY_CONTRACT_NULL");
                continue;
            }

            if (string.IsNullOrWhiteSpace(contract.id) || contract.id.Length > 128) errors.Add("VICTORY_ID_INVALID");
            if (string.IsNullOrWhiteSpace(contract.title) || contract.title.Length > 200) errors.Add("VICTORY_TITLE_INVALID:" + contract.id);
            if (string.IsNullOrWhiteSpace(contract.description) || contract.description.Length > 2_000) errors.Add("VICTORY_DESCRIPTION_INVALID:" + contract.id);
        }

        return errors;
    }

    public static IReadOnlyList<string> ValidateGenerationLifecycle(RuleSetV1 ruleSet, GameSnapshotV1 snapshot)
    {
        var errors = new List<string>();
        var existingRules = (snapshot.activeRules ?? new List<RuleNodeV1>()).Where(rule => rule != null).ToList();
        var activeIds = existingRules
            .Where(rule => GameRules.IsRuleActive(rule, ruleSet.applyTurn))
            .Select(rule => rule.id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (activeIds.Count >= RuleLimits.MaxActiveRules
            && (ruleSet.changes ?? new List<RuleNodeV1>()).Any(rule => rule != null && !activeIds.Contains(rule.id)))
            errors.Add("ACTIVE_RULE_REPLACEMENT_REQUIRED");

        var existingContracts = (snapshot.victoryContracts ?? new List<VictoryContractV1>())
            .Where(contract => contract != null)
            .ToList();
        if (existingContracts.Count == 0 && (ruleSet.victoryContracts?.Count ?? 0) == 0)
            errors.Add("FIRST_VICTORY_CONTRACT_REQUIRED");
        if (existingContracts.Count >= RuleLimits.MaxVictoryContracts)
        {
            var existingIds = existingContracts
                .Select(contract => contract.id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);
            if ((ruleSet.victoryContracts ?? new List<VictoryContractV1>())
                .Any(contract => contract != null && !existingIds.Contains(contract.id)))
                errors.Add("VICTORY_CONTRACT_REPLACEMENT_ID_REQUIRED");
        }

        return errors;
    }

    public static void ApplyServerOwnedFields(
        RuleSetV1 ruleSet,
        string requestId,
        int applyTurn,
        GameSnapshotV1? snapshot = null)
    {
        ruleSet.requestId = requestId;
        ruleSet.applyTurn = applyTurn;
        var replacementWarningIssued = false;
        foreach (var rule in ruleSet.changes ?? new List<RuleNodeV1>())
        {
            if (rule != null) rule.appliedTurn = applyTurn;
        }

        foreach (var action in ruleSet.actions ?? new List<DynamicActionV1>())
        {
            if (action != null) action.availableTurn = Math.Max(action.availableTurn, applyTurn);
        }

        foreach (var contract in ruleSet.victoryContracts ?? new List<VictoryContractV1>())
        {
            if (contract == null) continue;
            var existing = (snapshot?.victoryContracts ?? new List<VictoryContractV1>())
                .FirstOrDefault(candidate => candidate != null && string.Equals(candidate.id, contract.id, StringComparison.Ordinal));
            if (existing == null)
            {
                contract.minimumTurns = Math.Max(RuleValidator.MinimumFirstVictoryTurns, contract.minimumTurns);
                contract.announcedTurn = applyTurn;
                contract.achievableFromTurn = Math.Max(contract.achievableFromTurn, applyTurn + 2);
                contract.replaceWarningTurn = 0;
                continue;
            }

            var definitionChanged = ContractDefinitionChanged(existing, contract);
            var minimumReplacementTurn = (long)existing.announcedTurn + Math.Max(3, existing.minimumTurns);
            var minimumLifetimeMet = (long)applyTurn >= minimumReplacementTurn;
            var warningWindowOpen = (long)applyTurn + 1L >= minimumReplacementTurn;
            var warnedOnPriorTurn = existing.replaceWarningTurn > 0 && existing.replaceWarningTurn < applyTurn;
            if (definitionChanged && minimumLifetimeMet && warnedOnPriorTurn)
            {
                contract.minimumTurns = Math.Max(RuleValidator.MinimumFirstVictoryTurns, contract.minimumTurns);
                contract.announcedTurn = applyTurn;
                contract.achievableFromTurn = Math.Max(contract.achievableFromTurn, applyTurn + 2);
                contract.replaceWarningTurn = 0;
                continue;
            }

            CopyContractDefinition(existing, contract);
            if (definitionChanged && existing.replaceWarningTurn <= 0 && warningWindowOpen)
            {
                contract.replaceWarningTurn = applyTurn;
                replacementWarningIssued = true;
            }
        }
        if (replacementWarningIssued)
            ruleSet.koreanSummary = "승리 계약 교체 예고 · " + (ruleSet.koreanSummary ?? string.Empty);
    }

    private static bool ContractDefinitionChanged(VictoryContractV1 existing, VictoryContractV1 proposed)
    {
        return !string.Equals(existing.title, proposed.title, StringComparison.Ordinal)
               || !string.Equals(existing.description, proposed.description, StringComparison.Ordinal)
               || !string.Equals(existing.progressKey, proposed.progressKey, StringComparison.Ordinal)
               || existing.target != proposed.target
               || existing.minimumTurns != proposed.minimumTurns
               || !string.Equals(existing.worldCue, proposed.worldCue, StringComparison.Ordinal);
    }

    private static void CopyContractDefinition(VictoryContractV1 source, VictoryContractV1 destination)
    {
        destination.id = source.id;
        destination.title = source.title;
        destination.description = source.description;
        destination.progressKey = source.progressKey;
        destination.target = source.target;
        destination.minimumTurns = source.minimumTurns;
        destination.announcedTurn = source.announcedTurn;
        destination.achievableFromTurn = source.achievableFromTurn;
        destination.replaceWarningTurn = source.replaceWarningTurn;
        destination.worldCue = source.worldCue;
    }

    private static string? NormalizeOrigin(string candidate)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;
        if (string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    private static long? ReadNonNegativeInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var parsed)
            || parsed < 0)
            return null;
        return parsed;
    }

    private static bool MaintenanceRanRecently(string? performedUtc, DateTime utcNow)
    {
        return DateTime.TryParse(performedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var previous)
               && utcNow - previous.ToUniversalTime() < TimeSpan.FromHours(6);
    }

    private static void AddColumnIfMissing(
        SqliteConnection database,
        SqliteTransaction transaction,
        ISet<string> columns,
        string name,
        string type)
    {
        if (columns.Contains(name)) return;
        using var alter = new SqliteCommand($"ALTER TABLE request_log ADD COLUMN {name} {type}", database, transaction);
        alter.ExecuteNonQuery();
        columns.Add(name);
    }

    private static void ValidateCollection<T>(ICollection<T>? collection, int minimum, int maximum, string name, ICollection<string> errors)
    {
        if (collection == null)
        {
            errors.Add(name + "_REQUIRED");
            return;
        }

        if (collection.Count < minimum || collection.Count > maximum) errors.Add(name + "_COUNT");
        if (collection.Any(item => item is null)) errors.Add(name + "_NULL_ITEM");
    }
}
