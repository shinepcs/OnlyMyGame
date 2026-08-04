using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OnlyMyGame.Api;
using OnlyMyGame.Core;

namespace OnlyMyGame.Api.Tests;

public sealed class ApiPoliciesTests
{
    [Fact]
    public void AllowedOrigin_StripsPagesPathAndTrailingSlash()
    {
        var origins = ApiPolicies.ParseAllowedOrigins("https://shinepcs.github.io/OnlyMyGame/");

        Assert.Equal(new[] { "https://shinepcs.github.io" }, origins);
    }

    [Fact]
    public void AllowedOrigins_RejectUnsafeSchemesAndKeepValidOrigins()
    {
        var origins = ApiPolicies.ParseAllowedOrigins("javascript:alert(1), https://example.com/game;http://localhost:8080/path");

        Assert.Equal(new[] { "https://example.com", "http://localhost:8080" }, origins);
    }

    [Fact]
    public void EmptySnapshot_IsRejectedBeforeAnUpstreamRequest()
    {
        var errors = ApiPolicies.ValidateSnapshot(new GameSnapshotV1());

        Assert.Contains("RUN_ID_REQUIRED", errors);
        Assert.Contains("MAP_COUNT", errors);
        Assert.Contains("FACTIONS_COUNT", errors);
        Assert.Contains("PLAYER_FACTION_REQUIRED", errors);
    }

    [Fact]
    public void TrustedProxies_AcceptsOnlyExplicitIpAddresses()
    {
        var proxies = ApiPolicies.ParseTrustedProxies("10.0.0.2, invalid; 2001:db8::1, 10.0.0.2");

        Assert.Equal(new[] { IPAddress.Parse("10.0.0.2"), IPAddress.Parse("2001:db8::1") }, proxies);
        Assert.Empty(ApiPolicies.ParseTrustedProxies(null));
    }

    [Fact]
    public void Snapshot_AllowsStoredRulesBeyondTheSimultaneousActiveLimit()
    {
        var snapshot = MinimalSnapshot();
        snapshot.activeRules = Enumerable.Range(0, RuleLimits.MaxStoredRules)
            .Select(index => new RuleNodeV1 { id = "stored-" + index })
            .ToList();

        Assert.DoesNotContain("ACTIVE_RULES_COUNT", ApiPolicies.ValidateSnapshot(snapshot));

        snapshot.activeRules.Add(new RuleNodeV1 { id = "one-too-many" });
        Assert.Contains("ACTIVE_RULES_COUNT", ApiPolicies.ValidateSnapshot(snapshot));
    }

    [Fact]
    public void ServerOwnedFields_ApplyToTheCurrentPlanningTurn()
    {
        var ruleSet = new RuleSetV1
        {
            changes = new List<RuleNodeV1> { new() },
            actions = new List<DynamicActionV1> { new() { availableTurn = 1 } },
            victoryContracts = new List<VictoryContractV1> { new() { minimumTurns = 1 } }
        };

        ApiPolicies.ApplyServerOwnedFields(ruleSet, "run-turn-7", 7);

        Assert.Equal(7, ruleSet.applyTurn);
        Assert.Equal(7, ruleSet.changes.Single().appliedTurn);
        Assert.Equal(7, ruleSet.actions.Single().availableTurn);
        Assert.Equal(7, ruleSet.victoryContracts.Single().announcedTurn);
        Assert.Equal(9, ruleSet.victoryContracts.Single().achievableFromTurn);
        Assert.Equal(18, ruleSet.victoryContracts.Single().minimumTurns);
    }

    [Fact]
    public void RequestLog_MigratesLegacySchemaWithoutDroppingRows()
    {
        using var database = OpenMemoryDatabase();
        using (var create = new SqliteCommand(
            "CREATE TABLE request_log (id INTEGER PRIMARY KEY, day TEXT NOT NULL, ip_hash TEXT NOT NULL, request_key TEXT NOT NULL UNIQUE, created_utc TEXT NOT NULL, latency_ms INTEGER, valid INTEGER, error TEXT); INSERT INTO request_log(day,ip_hash,request_key,created_utc,valid,error) VALUES('2026-08-05','ip','legacy','2026-08-05T00:00:00Z',1,'');",
            database))
        {
            create.ExecuteNonQuery();
        }

        ApiPolicies.EnsureRequestLogSchema(database);

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var pragma = new SqliteCommand("PRAGMA table_info(request_log)", database))
        using (var reader = pragma.ExecuteReader())
        {
            while (reader.Read()) columns.Add(reader.GetString(1));
        }
        Assert.Contains("response_json", columns);
        Assert.Contains("request_hash", columns);
        Assert.Contains("lease_token", columns);
        Assert.Contains("attempt_count", columns);
        Assert.Contains("attempt_day", columns);
        Assert.Contains("compatibility_version", columns);
        Assert.Contains("completed_utc", columns);
        Assert.Contains("last_latency_ms", columns);
        Assert.Contains("input_tokens", columns);
        Assert.Contains("output_tokens", columns);
        Assert.Contains("total_tokens", columns);
        Assert.Contains("cached_input_tokens", columns);
        Assert.Contains("cache_write_tokens", columns);
        Assert.Contains("reasoning_tokens", columns);
        Assert.Contains("upstream_attempts", columns);
        Assert.Contains("validation_failures", columns);
        using (var attemptsTable = new SqliteCommand("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='request_attempt'", database))
            Assert.Equal(1L, (long)(attemptsTable.ExecuteScalar() ?? 0L));
        using (var sessionsTable = new SqliteCommand("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='game_session'", database))
            Assert.Equal(1L, (long)(sessionsTable.ExecuteScalar() ?? 0L));
        using (var maintenanceTable = new SqliteCommand("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='service_maintenance'", database))
            Assert.Equal(1L, (long)(maintenanceTable.ExecuteScalar() ?? 0L));
        using var count = new SqliteCommand("SELECT COUNT(*) FROM request_log WHERE request_key='legacy'", database);
        Assert.Equal(1L, (long)(count.ExecuteScalar() ?? 0L));
    }

    [Fact]
    public void ResponseUsage_ParsesAvailableTokenBreakdowns()
    {
        using var response = JsonDocument.Parse(
            """
            {
              "usage": {
                "input_tokens": 120,
                "output_tokens": 30,
                "total_tokens": 150,
                "input_tokens_details": { "cached_tokens": 20, "cache_write_tokens": 4 },
                "output_tokens_details": { "reasoning_tokens": 10 }
              }
            }
            """);

        var usage = ApiPolicies.ParseResponseUsage(response.RootElement);

        Assert.Equal(120, usage.InputTokens);
        Assert.Equal(30, usage.OutputTokens);
        Assert.Equal(150, usage.TotalTokens);
        Assert.Equal(20, usage.CachedInputTokens);
        Assert.Equal(4, usage.CacheWriteTokens);
        Assert.Equal(10, usage.ReasoningTokens);
    }

    [Fact]
    public void ResponseUsage_ComputesMissingTotalAndIgnoresInvalidNumbers()
    {
        using var response = JsonDocument.Parse(
            """{"usage":{"input_tokens":12,"output_tokens":8,"input_tokens_details":{"cached_tokens":-1}}}""");

        var usage = ApiPolicies.ParseResponseUsage(response.RootElement);

        Assert.Equal(20, usage.TotalTokens);
        Assert.Null(usage.CachedInputTokens);
    }

    [Fact]
    public void SuccessfulRequest_IsReplayedEvenWhenQuotaIsFull()
    {
        using var database = OpenRequestDatabase();
        var now = new DateTime(2026, 8, 5, 1, 2, 3, DateTimeKind.Utc);
        var first = ApiPolicies.ClaimRuleRequest(database, "2026-08-05", "ip", "key-1", "hash-1", 1, now);

        Assert.Equal(RuleRequestClaimKind.Claimed, first.Kind);
        Assert.True(ApiPolicies.CompleteRuleRequest(database, "key-1", first.LeaseToken!, 12, "{\"schemaVersion\":\"v1\"}", null));

        var replay = ApiPolicies.ClaimRuleRequest(database, "2026-08-05", "ip", "key-1", "hash-1", 1, now.AddSeconds(1));
        var overQuota = ApiPolicies.ClaimRuleRequest(database, "2026-08-05", "ip", "key-2", "hash-2", 1, now.AddSeconds(1));
        Assert.Equal(RuleRequestClaimKind.Replay, replay.Kind);
        Assert.Equal("{\"schemaVersion\":\"v1\"}", replay.ResponseJson);
        Assert.Equal(RuleRequestClaimKind.DailyLimitReached, overQuota.Kind);
    }

    [Fact]
    public void FailedRequest_CanBeClaimedAgainWithoutAUniqueConstraintFailure()
    {
        using var database = OpenRequestDatabase();
        var now = new DateTime(2026, 8, 5, 1, 2, 3, DateTimeKind.Utc);
        var first = ApiPolicies.ClaimRuleRequest(database, "2026-08-05", "ip", "retry-key", "hash", 10, now);
        Assert.True(ApiPolicies.CompleteRuleRequest(database, "retry-key", first.LeaseToken!, 20, null, "UPSTREAM_TIMEOUT"));

        var retry = ApiPolicies.ClaimRuleRequest(database, "2026-08-05", "ip", "retry-key", "hash", 10, now.AddSeconds(1));

        Assert.Equal(RuleRequestClaimKind.Claimed, retry.Kind);
        Assert.NotEqual(first.LeaseToken, retry.LeaseToken);
        Assert.Equal("UPSTREAM_TIMEOUT", retry.PreviousError);
    }

    [Fact]
    public void RequestObservability_AccumulatesRetriesAndReplayIsFree()
    {
        using var database = OpenRequestDatabase();
        var now = new DateTime(2026, 8, 5, 1, 2, 3, DateTimeKind.Utc);
        var first = ApiPolicies.ClaimRuleRequest(database, "2026-08-05", "ip", "observed-key", "hash", 10, now);
        var firstUsage = new ResponseTokenUsage
        {
            InputTokens = 10,
            OutputTokens = 4,
            TotalTokens = 14,
            CachedInputTokens = 2,
            CacheWriteTokens = 1,
            ReasoningTokens = 3
        };
        Assert.True(ApiPolicies.CompleteRuleRequest(
            database,
            "observed-key",
            first.LeaseToken!,
            10,
            null,
            "UPSTREAM_TIMEOUT",
            firstUsage,
            upstreamAttempts: 1,
            validationFailures: 1,
            completedUtc: now.AddSeconds(1)));

        var retry = ApiPolicies.ClaimRuleRequest(database, "2026-08-05", "ip", "observed-key", "hash", 10, now.AddSeconds(2));
        var retryUsage = new ResponseTokenUsage
        {
            InputTokens = 20,
            OutputTokens = 8,
            TotalTokens = 28,
            CachedInputTokens = 5,
            CacheWriteTokens = 2,
            ReasoningTokens = 6
        };
        Assert.True(ApiPolicies.CompleteRuleRequest(
            database,
            "observed-key",
            retry.LeaseToken!,
            20,
            "winner",
            null,
            retryUsage,
            upstreamAttempts: 2,
            validationFailures: 1,
            completedUtc: now.AddSeconds(3)));

        using (var query = new SqliteCommand(
                   "SELECT latency_ms,last_latency_ms,input_tokens,output_tokens,total_tokens,cached_input_tokens,cache_write_tokens,reasoning_tokens,upstream_attempts,validation_failures,valid,error,completed_utc FROM request_log WHERE request_key='observed-key'",
                   database))
        using (var reader = query.ExecuteReader())
        {
            Assert.True(reader.Read());
            Assert.Equal(30L, reader.GetInt64(0));
            Assert.Equal(20L, reader.GetInt64(1));
            Assert.Equal(30L, reader.GetInt64(2));
            Assert.Equal(12L, reader.GetInt64(3));
            Assert.Equal(42L, reader.GetInt64(4));
            Assert.Equal(7L, reader.GetInt64(5));
            Assert.Equal(3L, reader.GetInt64(6));
            Assert.Equal(9L, reader.GetInt64(7));
            Assert.Equal(3L, reader.GetInt64(8));
            Assert.Equal(2L, reader.GetInt64(9));
            Assert.Equal(1L, reader.GetInt64(10));
            Assert.Equal(string.Empty, reader.GetString(11));
            Assert.False(reader.IsDBNull(12));
        }

        var replay = ApiPolicies.ClaimRuleRequest(database, "2026-08-05", "ip", "observed-key", "hash", 10, now.AddSeconds(4));
        Assert.Equal(RuleRequestClaimKind.Replay, replay.Kind);
        using var attempts = new SqliteCommand("SELECT COUNT(*) FROM request_attempt WHERE request_key='observed-key'", database);
        Assert.Equal(2L, (long)(attempts.ExecuteScalar() ?? 0L));
    }

    [Fact]
    public void RetentionPrune_EnforcesThirtyDayMinimumAndRemovesOnlyExpiredData()
    {
        using var database = OpenRequestDatabase();
        var now = new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);
        using (var seed = new SqliteCommand(
                   """
                   INSERT INTO request_log(day,ip_hash,request_key,created_utc,completed_utc,valid,error,lease_token)
                   VALUES('2026-07-01','ip','old-completed',$old,$old,1,'',NULL),
                         ('2026-08-01','ip','recent-completed',$recent,$recent,1,'',NULL),
                         ('2026-07-07','ip','within-minimum',$withinMinimum,$withinMinimum,1,'',NULL),
                         ('2026-07-06','ip','retention-boundary',$boundary,$boundary,1,'',NULL),
                         ('2026-07-01','ip','old-lease',$old,NULL,NULL,'','old-lease-token'),
                         ('2026-08-01','ip','recent-lease',$recent,NULL,NULL,'','recent-lease-token');
                   INSERT INTO request_attempt(day,ip_hash,request_key,created_utc)
                   VALUES('2026-07-01','ip','old-completed',$old),
                         ('2026-08-01','ip','recent-completed',$recent);
                   INSERT INTO game_session(token_hash,run_id,ip_hash,created_utc,expires_utc)
                   VALUES('expired','run-old','ip',$old,$expired),
                         ('live','run-live','ip',$recent,$live);
                   """,
                   database))
        {
            seed.Parameters.AddWithValue("$old", now.AddDays(-31).ToString("O"));
            seed.Parameters.AddWithValue("$recent", now.AddDays(-4).ToString("O"));
            seed.Parameters.AddWithValue("$withinMinimum", now.AddDays(-29).ToString("O"));
            seed.Parameters.AddWithValue("$boundary", now.AddDays(-30).ToString("O"));
            seed.Parameters.AddWithValue("$expired", now.AddMinutes(-1).ToString("O"));
            seed.Parameters.AddWithValue("$live", now.AddHours(1).ToString("O"));
            seed.ExecuteNonQuery();
        }

        var pruned = ApiPolicies.PruneExpiredData(database, now, retentionDays: 7, force: true);

        Assert.True(pruned.Performed);
        Assert.Equal(2, pruned.RequestLogsDeleted);
        Assert.Equal(1, pruned.AttemptsDeleted);
        Assert.Equal(1, pruned.SessionsDeleted);
        Assert.Equal(0L, CountRows(database, "request_log", "request_key='old-completed'"));
        Assert.Equal(1L, CountRows(database, "request_log", "request_key='recent-completed'"));
        Assert.Equal(1L, CountRows(database, "request_log", "request_key='within-minimum'"));
        Assert.Equal(1L, CountRows(database, "request_log", "request_key='retention-boundary'"));
        Assert.Equal(0L, CountRows(database, "request_log", "request_key='old-lease'"));
        Assert.Equal(1L, CountRows(database, "request_log", "request_key='recent-lease' AND valid IS NULL AND lease_token='recent-lease-token'"));
        Assert.Equal(0L, CountRows(database, "request_attempt", "request_key='old-completed'"));
        Assert.Equal(1L, CountRows(database, "request_attempt", "request_key='recent-completed'"));
        Assert.Equal(0L, CountRows(database, "game_session", "token_hash='expired'"));
        Assert.Equal(1L, CountRows(database, "game_session", "token_hash='live'"));

        var throttled = ApiPolicies.PruneExpiredData(database, now.AddHours(1), retentionDays: 30);
        Assert.False(throttled.Performed);
    }

    [Theory]
    [InlineData(-1, 30)]
    [InlineData(7, 30)]
    [InlineData(30, 30)]
    [InlineData(365, 365)]
    [InlineData(500, 365)]
    public void RetentionDays_AreClampedToSupportedObservabilityWindow(int requested, int expected)
    {
        Assert.Equal(expected, ApiPolicies.NormalizeRetentionDays(requested));
    }

    [Fact]
    public void ConcurrentAndStaleClaims_DoNotOverwriteTheWinningLease()
    {
        using var database = OpenRequestDatabase();
        var now = new DateTime(2026, 8, 5, 1, 2, 3, DateTimeKind.Utc);
        var first = ApiPolicies.ClaimRuleRequest(database, "2026-08-05", "ip", "race-key", "hash", 10, now);

        var concurrent = ApiPolicies.ClaimRuleRequest(database, "2026-08-05", "ip", "race-key", "hash", 10, now.AddSeconds(1));
        Assert.Equal(RuleRequestClaimKind.InProgress, concurrent.Kind);

        var replacement = ApiPolicies.ClaimRuleRequest(
            database,
            "2026-08-05",
            "ip",
            "race-key",
            "hash",
            10,
            now + ApiPolicies.RequestLeaseDuration + TimeSpan.FromSeconds(1));
        Assert.Equal(RuleRequestClaimKind.Claimed, replacement.Kind);
        Assert.False(ApiPolicies.CompleteRuleRequest(database, "race-key", first.LeaseToken!, 100, "stale", null));
        Assert.True(ApiPolicies.CompleteRuleRequest(database, "race-key", replacement.LeaseToken!, 10, "winner", null));
    }

    [Fact]
    public void IdempotencyKey_CannotBeReusedForADifferentSnapshot()
    {
        using var database = OpenRequestDatabase();
        var now = new DateTime(2026, 8, 5, 1, 2, 3, DateTimeKind.Utc);
        ApiPolicies.ClaimRuleRequest(database, "2026-08-05", "ip", "same-key", "hash-a", 10, now);

        var mismatch = ApiPolicies.ClaimRuleRequest(database, "2026-08-05", "ip", "same-key", "hash-b", 10, now.AddSeconds(1));

        Assert.Equal(RuleRequestClaimKind.KeyMismatch, mismatch.Kind);
    }

    [Fact]
    public void CanonicalRequestHash_IgnoresRetryTransientStateOnly()
    {
        var snapshot = MinimalSnapshot();
        snapshot.phase = RunPhase.AwaitingRules;
        snapshot.planningPrepared = true;
        snapshot.journal.Add("first request failed");
        snapshot.ruleBudget = new RuleRuntimeBudget { turn = snapshot.turn, dispatches = 3 };
        var before = ApiPolicies.ComputeRuleRequestHash(snapshot);

        snapshot.phase = RunPhase.Planning;
        snapshot.planningPrepared = false;
        snapshot.journal.Add("retrying");
        snapshot.ruleBudget.effects = 99;
        var afterTransientChanges = ApiPolicies.ComputeRuleRequestHash(snapshot);
        snapshot.luck++;
        var afterGameplayChange = ApiPolicies.ComputeRuleRequestHash(snapshot);

        Assert.Equal(before, afterTransientChanges);
        Assert.NotEqual(before, afterGameplayChange);
    }

    [Fact]
    public void FailedRequest_BurstRetriesUseRecoverableCooldown()
    {
        using var database = OpenRequestDatabase();
        var now = new DateTime(2026, 8, 5, 1, 2, 3, DateTimeKind.Utc);
        RuleRequestClaim claim = ApiPolicies.ClaimRuleRequest(database, "2026-08-05", "ip", "bounded-retry", "hash", 10, now);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Assert.Equal(RuleRequestClaimKind.Claimed, claim.Kind);
            Assert.True(ApiPolicies.CompleteRuleRequest(database, "bounded-retry", claim.LeaseToken!, 1, null, "UPSTREAM_TIMEOUT"));
            claim = ApiPolicies.ClaimRuleRequest(database, "2026-08-05", "ip", "bounded-retry", "hash", 10, now.AddSeconds(attempt));
        }

        Assert.Equal(RuleRequestClaimKind.RetryCooldown, claim.Kind);
        Assert.InRange(claim.RetryAfterSeconds, 1, 120);
        var afterCooldown = ApiPolicies.ClaimRuleRequest(database, "2026-08-05", "ip", "bounded-retry", "hash", 10, now.AddSeconds(123));
        Assert.Equal(RuleRequestClaimKind.Claimed, afterCooldown.Kind);
    }

    [Fact]
    public void CompatibilityVersionChange_ReclaimsInsteadOfReplayingStaleRules()
    {
        using var database = OpenRequestDatabase();
        var now = new DateTime(2026, 8, 5, 1, 2, 3, DateTimeKind.Utc);
        var first = ApiPolicies.ClaimRuleRequest(database, "2026-08-05", "ip", "versioned", "hash", 10, now);
        Assert.True(ApiPolicies.CompleteRuleRequest(database, "versioned", first.LeaseToken!, 1, "cached", null));
        using (var downgrade = new SqliteCommand("UPDATE request_log SET compatibility_version='old-version' WHERE request_key='versioned'", database))
            downgrade.ExecuteNonQuery();

        var reclaimed = ApiPolicies.ClaimRuleRequest(database, "2026-08-05", "ip", "versioned", "new-hash", 10, now.AddSeconds(1));

        Assert.Equal(RuleRequestClaimKind.Claimed, reclaimed.Kind);
        Assert.Equal("COMPATIBILITY_VERSION_CHANGED", reclaimed.PreviousError);
    }

    [Fact]
    public void VictoryContractReplacement_RequiresLifetimeAndPriorTurnWarning()
    {
        var existing = new VictoryContractV1
        {
            id = "contract-1",
            title = "기존 계약",
            description = "기존 목표",
            progressKey = "turn",
            target = 30,
            minimumTurns = 18,
            announcedTurn = 1,
            achievableFromTurn = 3,
            worldCue = "기존 표식"
        };
        var snapshot = MinimalSnapshot();
        snapshot.turn = 5;
        snapshot.victoryContracts.Add(existing);
        var earlySet = new RuleSetV1
        {
            victoryContracts = new List<VictoryContractV1>
            {
                new() { id = "contract-1", title = "너무 이른 새 계약", description = "새 목표", progressKey = "kills", target = 5, minimumTurns = 18, worldCue = "새 표식" }
            }
        };

        ApiPolicies.ApplyServerOwnedFields(earlySet, "early", 5, snapshot);

        var held = earlySet.victoryContracts.Single();
        Assert.Equal("기존 계약", held.title);
        Assert.Equal(0, held.replaceWarningTurn);

        snapshot.turn = 25;
        var warningSet = new RuleSetV1
        {
            victoryContracts = new List<VictoryContractV1>
            {
                new() { id = "contract-1", title = "새 계약", description = "새 목표", progressKey = "kills", target = 5, minimumTurns = 18, worldCue = "새 표식" }
            }
        };

        ApiPolicies.ApplyServerOwnedFields(warningSet, "warning", 25, snapshot);

        var warning = warningSet.victoryContracts.Single();
        Assert.Equal("기존 계약", warning.title);
        Assert.Equal(25, warning.replaceWarningTurn);
        Assert.StartsWith("승리 계약 교체 예고", warningSet.koreanSummary, StringComparison.Ordinal);
        snapshot.turn = 26;
        snapshot.victoryContracts = new List<VictoryContractV1> { warning };
        var replacementSet = new RuleSetV1
        {
            victoryContracts = new List<VictoryContractV1>
            {
                new() { id = "contract-1", title = "새 계약", description = "새 목표", progressKey = "kills", target = 5, minimumTurns = 18, worldCue = "새 표식" }
            }
        };

        ApiPolicies.ApplyServerOwnedFields(replacementSet, "replacement", 26, snapshot);

        var replacement = replacementSet.victoryContracts.Single();
        Assert.Equal("새 계약", replacement.title);
        Assert.Equal(26, replacement.announcedTurn);
        Assert.Equal(0, replacement.replaceWarningTurn);
        Assert.True(replacement.minimumTurns >= 18);
    }

    [Fact]
    public void GenerationLifecycle_RequiresFirstContractAndExistingIdsAtCapacity()
    {
        var snapshot = MinimalSnapshot();
        var emptySet = new RuleSetV1 { applyTurn = snapshot.turn };
        Assert.Contains("FIRST_VICTORY_CONTRACT_REQUIRED", ApiPolicies.ValidateGenerationLifecycle(emptySet, snapshot));

        snapshot.activeRules = Enumerable.Range(0, RuleLimits.MaxActiveRules)
            .Select(index => new RuleNodeV1 { id = "active-" + index, appliedTurn = snapshot.turn, durationTurns = 3 })
            .ToList();
        snapshot.victoryContracts = Enumerable.Range(0, RuleLimits.MaxVictoryContracts)
            .Select(index => new VictoryContractV1 { id = "contract-" + index })
            .ToList();
        var overflowing = new RuleSetV1
        {
            applyTurn = snapshot.turn,
            changes = new List<RuleNodeV1> { new() { id = "new-rule" } },
            victoryContracts = new List<VictoryContractV1> { new() { id = "new-contract" } }
        };
        var errors = ApiPolicies.ValidateGenerationLifecycle(overflowing, snapshot);

        Assert.Contains("ACTIVE_RULE_REPLACEMENT_REQUIRED", errors);
        Assert.Contains("VICTORY_CONTRACT_REPLACEMENT_ID_REQUIRED", errors);
    }

    [Fact]
    public void Sessions_StoreOnlyTokenHashAndEnforceBindingExpiryAndLimit()
    {
        using var database = OpenRequestDatabase();
        var now = new DateTime(2026, 8, 5, 1, 2, 3, DateTimeKind.Utc);
        var first = ApiPolicies.IssueSession(database, "run-a", "ip-a", now, maxActiveSessions: 2, lifetimeSeconds: 3600);
        var second = ApiPolicies.IssueSession(database, "run-b", "ip-a", now, maxActiveSessions: 2, lifetimeSeconds: 3600);
        var third = ApiPolicies.IssueSession(database, "run-c", "ip-a", now, maxActiveSessions: 2, lifetimeSeconds: 3600);

        Assert.Equal(SessionIssueKind.Issued, first.Kind);
        Assert.Equal(SessionIssueKind.Issued, second.Kind);
        Assert.Equal(SessionIssueKind.ActiveLimitReached, third.Kind);
        Assert.True(ApiPolicies.ValidateSession(database, first.Token!, "run-a", "ip-a", now.AddMinutes(30)));
        Assert.False(ApiPolicies.ValidateSession(database, first.Token!, "run-b", "ip-a", now.AddMinutes(30)));
        Assert.False(ApiPolicies.ValidateSession(database, first.Token!, "run-a", "ip-b", now.AddMinutes(30)));
        Assert.False(ApiPolicies.ValidateSession(database, first.Token!, "run-a", "ip-a", now.AddHours(2)));
        using var rawTokenSearch = new SqliteCommand("SELECT COUNT(*) FROM game_session WHERE token_hash=$raw", database);
        rawTokenSearch.Parameters.AddWithValue("$raw", first.Token!);
        Assert.Equal(0L, (long)(rawTokenSearch.ExecuteScalar() ?? 0L));
    }

    [Fact]
    public void GlobalAttemptCircuitBreaker_BlocksGenerationButNotReplay()
    {
        using var database = OpenRequestDatabase();
        var now = new DateTime(2026, 8, 5, 1, 2, 3, DateTimeKind.Utc);
        var first = ApiPolicies.ClaimRuleRequest(database, "2026-08-05", "ip-a", "global-1", "hash-1", 10, now, globalDailyLimit: 1);
        Assert.True(ApiPolicies.CompleteRuleRequest(database, "global-1", first.LeaseToken!, 1, "cached", null));

        var replay = ApiPolicies.ClaimRuleRequest(database, "2026-08-05", "ip-a", "global-1", "hash-1", 10, now.AddSeconds(1), globalDailyLimit: 1);
        var blocked = ApiPolicies.ClaimRuleRequest(database, "2026-08-05", "ip-b", "global-2", "hash-2", 10, now.AddSeconds(1), globalDailyLimit: 1);

        Assert.Equal(RuleRequestClaimKind.Replay, replay.Kind);
        Assert.Equal(RuleRequestClaimKind.GlobalDailyLimitReached, blocked.Kind);
    }

    private static GameSnapshotV1 MinimalSnapshot()
    {
        return new GameSnapshotV1
        {
            runId = "run",
            catalogHash = "catalog",
            map = new List<TileState> { new() },
            factions = new List<FactionState> { new() { kind = FactionKind.Player } }
        };
    }

    private static SqliteConnection OpenMemoryDatabase()
    {
        var database = new SqliteConnection("Data Source=:memory:");
        database.Open();
        return database;
    }

    private static SqliteConnection OpenRequestDatabase()
    {
        var database = OpenMemoryDatabase();
        ApiPolicies.EnsureRequestLogSchema(database);
        return database;
    }

    private static long CountRows(SqliteConnection database, string table, string predicate)
    {
        using var command = new SqliteCommand($"SELECT COUNT(*) FROM {table} WHERE {predicate}", database);
        return (long)(command.ExecuteScalar() ?? 0L);
    }
}
