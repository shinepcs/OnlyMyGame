using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Data.Sqlite;
using OnlyMyGame.Api;
using OnlyMyGame.Core;

const string CorsPolicyName = "GameClient";
const long MaxRequestBodyBytes = 1_000_000;
var ruleGenerationDeadline = TimeSpan.FromSeconds(18);

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var upstreamTimeoutSeconds = int.TryParse(config["ONLYMYGAME_UPSTREAM_TIMEOUT_SECONDS"], out var parsedUpstreamTimeout)
    ? Math.Clamp(parsedUpstreamTimeout, 1, 15)
    : 8;
var upstreamRequestTimeout = TimeSpan.FromSeconds(upstreamTimeoutSeconds);
var modelName = config["ONLYMYGAME_OPENAI_MODEL"] ?? "gpt-5.6-luna";
var dailyLimit = int.TryParse(config["ONLYMYGAME_DAILY_LIMIT"], out var parsedLimit)
    ? Math.Clamp(parsedLimit, 1, 1_000)
    : 60;
var globalDailyLimit = int.TryParse(config["ONLYMYGAME_GLOBAL_DAILY_LIMIT"], out var parsedGlobalLimit)
    ? Math.Clamp(parsedGlobalLimit, 1, 100_000)
    : 600;
var maxAttemptsPerKey = int.TryParse(config["ONLYMYGAME_MAX_ATTEMPTS_PER_KEY"], out var parsedAttempts)
    ? Math.Clamp(parsedAttempts, 2, 10)
    : 3;
var retryCooldownSeconds = int.TryParse(config["ONLYMYGAME_RETRY_COOLDOWN_SECONDS"], out var parsedCooldown)
    ? Math.Clamp(parsedCooldown, 30, 900)
    : 120;
var retentionDays = int.TryParse(config["ONLYMYGAME_RETENTION_DAYS"], out var parsedRetentionDays)
    ? ApiPolicies.NormalizeRetentionDays(parsedRetentionDays)
    : 30;
const int MaxActiveSessionsPerIp = 2;
const int SessionLifetimeSeconds = 3600;
var allowedOrigins = ApiPolicies.ParseAllowedOrigins(config["ONLYMYGAME_ALLOWED_ORIGINS"] ?? config["ONLYMYGAME_ALLOWED_ORIGIN"]);
var trustedProxies = ApiPolicies.ParseTrustedProxies(config["ONLYMYGAME_TRUSTED_PROXIES"]);
var openAiBaseAddress = ParseOpenAiBaseAddress(config["ONLYMYGAME_OPENAI_BASE_URL"]);
var dbPath = config["ONLYMYGAME_DB"] ?? "/data/onlymygame.db";
var dbDirectory = Path.GetDirectoryName(dbPath);
if (!string.IsNullOrWhiteSpace(dbDirectory)) Directory.CreateDirectory(dbDirectory);
var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, DefaultTimeout = 5 }.ToString();
var ruleSetJsonOptions = CreateRuleSetJsonOptions();

using (var db = new SqliteConnection(connectionString))
{
    db.Open();
    ApiPolicies.EnsureRequestLogSchema(db);
    ApiPolicies.PruneExpiredData(db, DateTime.UtcNow, retentionDays, force: true);
}

builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = MaxRequestBodyBytes);
builder.Services.AddCors(options => options.AddPolicy(CorsPolicyName, policy => policy
    .WithOrigins(allowedOrigins)
    .WithHeaders("Content-Type", "Authorization", "Idempotency-Key", "X-Unity-Version")
    .WithMethods("GET", "POST", "OPTIONS")
    .SetPreflightMaxAge(TimeSpan.FromHours(1))));
if (trustedProxies.Length > 0)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var proxy in trustedProxies) options.KnownProxies.Add(proxy);
    });
}
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.IncludeFields = true;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.AddHttpClient("openai", client =>
{
    client.BaseAddress = openAiBaseAddress;
    client.Timeout = upstreamRequestTimeout;
});

var app = builder.Build();
if (trustedProxies.Length > 0) app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    context.Response.Headers["Cache-Control"] = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    await next();
});
app.UseCors(CorsPolicyName);

app.MapGet("/health", async (CancellationToken cancellationToken) =>
{
    var databaseOk = false;
    try
    {
        await using var db = new SqliteConnection(connectionString);
        await db.OpenAsync(cancellationToken);
        await using var command = new SqliteCommand("SELECT 1", db);
        databaseOk = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Database health check failed.");
    }

    var configured = !string.IsNullOrWhiteSpace(config["OPENAI_API_KEY"])
        && !string.IsNullOrWhiteSpace(config["ONLYMYGAME_DAILY_SALT"]);
    var healthy = databaseOk && configured;
    return Results.Json(new
    {
        status = healthy ? "ok" : "unavailable",
        model = modelName,
        apiVersion = ApiPolicies.ApiVersion,
        compatibilityVersion = ApiPolicies.RuleCompatibilityVersion,
        limits = new
        {
            perClientDailyAttempts = dailyLimit,
            globalDailyAttempts = globalDailyLimit,
            maxBurstAttemptsPerKey = maxAttemptsPerKey,
            retryCooldownSeconds,
            activeSessionsPerIp = MaxActiveSessionsPerIp,
            upstreamTimeoutSeconds,
            retentionDays
        },
        database = databaseOk ? "ok" : "unavailable",
        configured
    }, statusCode: healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
});

app.MapPost("/v1/sessions", async (
    HttpContext context,
    SessionRequest body,
    CancellationToken cancellationToken) =>
{
    if (body == null || string.IsNullOrWhiteSpace(body.runId) || body.runId.Length > 128)
        return Results.BadRequest(new { error = "INVALID_RUN_ID" });
    var dailySalt = config["ONLYMYGAME_DAILY_SALT"];
    if (string.IsNullOrWhiteSpace(dailySalt))
        return Results.Json(new { error = "SERVICE_NOT_CONFIGURED" }, statusCode: StatusCodes.Status503ServiceUnavailable);

    var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var sessionIpHash = ComputeSessionIpHash(dailySalt, ip);
    await using var database = new SqliteConnection(connectionString);
    await database.OpenAsync(cancellationToken);
    TryPruneExpiredData(database, retentionDays, app.Logger);
    var issue = ApiPolicies.IssueSession(
        database,
        body.runId,
        sessionIpHash,
        DateTime.UtcNow,
        MaxActiveSessionsPerIp,
        SessionLifetimeSeconds);
    if (issue.Kind == SessionIssueKind.ActiveLimitReached)
        return Results.Json(new { error = "ACTIVE_SESSION_LIMIT_REACHED" }, statusCode: StatusCodes.Status429TooManyRequests);
    return Results.Ok(new { token = issue.Token, expiresInSeconds = issue.ExpiresInSeconds });
});

app.MapMethods("/v1/sessions", new[] { HttpMethods.Options }, () => Results.NoContent());
app.MapMethods("/v1/rules/generate", new[] { HttpMethods.Options }, () => Results.NoContent());

app.MapPost("/v1/rules/generate", async (
    HttpContext context,
    GameSnapshotV1 snapshot,
    IHttpClientFactory clients,
    CancellationToken cancellationToken) =>
{
    if (context.Request.ContentLength is > MaxRequestBodyBytes)
        return Results.Json(new { error = "REQUEST_TOO_LARGE" }, statusCode: StatusCodes.Status413PayloadTooLarge);

    var snapshotErrors = ApiPolicies.ValidateSnapshot(snapshot);
    if (snapshotErrors.Count > 0)
        return Results.BadRequest(new { error = "INVALID_SNAPSHOT", diagnostics = snapshotErrors });
    var requestKey = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(requestKey) || requestKey.Length > 128)
        return Results.BadRequest(new { error = "INVALID_IDEMPOTENCY_KEY" });

    var dailySalt = config["ONLYMYGAME_DAILY_SALT"];
    if (string.IsNullOrWhiteSpace(config["OPENAI_API_KEY"]) || string.IsNullOrWhiteSpace(dailySalt))
        return Results.Json(new { error = "SERVICE_NOT_CONFIGURED" }, statusCode: StatusCodes.Status503ServiceUnavailable);

    var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var claimedAt = DateTime.UtcNow;
    var day = claimedAt.ToString("yyyy-MM-dd");
    var ipHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(day + dailySalt + ip)));

    await using var db = new SqliteConnection(connectionString);
    await db.OpenAsync(cancellationToken);
    TryPruneExpiredData(db, retentionDays, app.Logger);
    var authorization = context.Request.Headers.Authorization.FirstOrDefault();
    if (!AuthenticationHeaderValue.TryParse(authorization, out var authHeader)
        || !string.Equals(authHeader.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
        || string.IsNullOrWhiteSpace(authHeader.Parameter)
        || !ApiPolicies.ValidateSession(db, authHeader.Parameter, snapshot.runId, ComputeSessionIpHash(dailySalt, ip), claimedAt))
    {
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return Results.Json(new { error = "INVALID_OR_EXPIRED_SESSION" }, statusCode: StatusCodes.Status401Unauthorized);
    }
    RuleValidationResult deepSnapshotValidation;
    try
    {
        deepSnapshotValidation = RuleValidator.ValidateSnapshot(snapshot);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Core snapshot validation rejected malformed input.");
        return Results.BadRequest(new { error = "INVALID_SNAPSHOT", diagnostics = new[] { "CORE_VALIDATOR_EXCEPTION" } });
    }
    if (!deepSnapshotValidation.valid)
        return Results.BadRequest(new { error = "INVALID_SNAPSHOT", diagnostics = deepSnapshotValidation.errors });
    var requestHash = ApiPolicies.ComputeRuleRequestHash(snapshot);
    RuleRequestClaim claim;
    try
    {
        claim = ApiPolicies.ClaimRuleRequest(db, day, ipHash, requestKey, requestHash, dailyLimit, claimedAt, maxAttemptsPerKey, retryCooldownSeconds, globalDailyLimit);
    }
    catch (SqliteException ex)
    {
        app.Logger.LogError(ex, "Could not reserve the idempotent request.");
        return Results.Json(new { error = "REQUEST_STORE_UNAVAILABLE" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (claim.Kind == RuleRequestClaimKind.Replay)
    {
        try
        {
            var replay = JsonSerializer.Deserialize<RuleSetV1>(claim.ResponseJson!, ruleSetJsonOptions)
                ?? throw new JsonException("EMPTY_STORED_RULESET");
            var replayValidation = ValidateGeneratedRuleSet(replay, snapshot);
            if (replayValidation.valid) return Results.Ok(replay);
            app.Logger.LogWarning(
                "Stored idempotent response failed current validation: {Diagnostics}",
                string.Join(",", replayValidation.errors.Concat(replayValidation.diagnostics)));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            app.Logger.LogError(ex, "Stored idempotent response could not be deserialized.");
        }

        ApiPolicies.InvalidateStoredResponse(db, requestKey);
        try
        {
            claim = ApiPolicies.ClaimRuleRequest(
                db,
                day,
                ipHash,
                requestKey,
                requestHash,
                dailyLimit,
                DateTime.UtcNow,
                maxAttemptsPerKey,
                retryCooldownSeconds,
                globalDailyLimit);
        }
        catch (SqliteException ex)
        {
            app.Logger.LogError(ex, "Could not reclaim a stale idempotent response.");
            return Results.Json(new { error = "REQUEST_STORE_UNAVAILABLE" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    if (claim.Kind == RuleRequestClaimKind.DailyLimitReached)
        return Results.Json(new { error = "DAILY_LIMIT_REACHED" }, statusCode: StatusCodes.Status429TooManyRequests);
    if (claim.Kind == RuleRequestClaimKind.GlobalDailyLimitReached)
        return Results.Json(new { error = "GLOBAL_DAILY_LIMIT_REACHED" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    if (claim.Kind == RuleRequestClaimKind.RetryCooldown)
        return Results.Json(
            new { error = "RETRY_COOLDOWN", retryAfterSeconds = claim.RetryAfterSeconds },
            statusCode: StatusCodes.Status429TooManyRequests);
    if (claim.Kind == RuleRequestClaimKind.InProgress)
        return Results.Conflict(new { error = "REQUEST_IN_PROGRESS", retryAfterSeconds = 2 });
    if (claim.Kind == RuleRequestClaimKind.ResponseUnavailable)
        return Results.Json(new { error = "STORED_RESPONSE_UNAVAILABLE" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    if (claim.Kind == RuleRequestClaimKind.KeyMismatch)
        return Results.Conflict(new { error = "IDEMPOTENCY_KEY_REUSED_WITH_DIFFERENT_REQUEST" });
    if (claim.Kind != RuleRequestClaimKind.Claimed || string.IsNullOrWhiteSpace(claim.LeaseToken))
        return Results.Json(new { error = "REQUEST_STORE_UNAVAILABLE" }, statusCode: StatusCodes.Status503ServiceUnavailable);

    var started = claimedAt;
    RuleSetV1? ruleSet = null;
    string? error = null;
    var responseUsage = new ResponseTokenUsage();
    var upstreamAttempts = 0;
    var validationFailures = 0;
    using var generationDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    generationDeadline.CancelAfter(ruleGenerationDeadline);
    var generationToken = generationDeadline.Token;
    try
    {
        IEnumerable<string>? repairDiagnostics = null;
        for (var attemptIndex = 0; attemptIndex < 2; attemptIndex++)
        {
            try
            {
                upstreamAttempts++;
                ruleSet = await GenerateRules(
                    snapshot,
                    clients.CreateClient("openai"),
                    config["OPENAI_API_KEY"],
                    modelName,
                    generationToken,
                    responseUsage,
                    repairDiagnostics);
            }
            catch (JsonException ex) when (attemptIndex == 0)
            {
                validationFailures++;
                repairDiagnostics = new[] { "UPSTREAM_INVALID_JSON" };
                app.Logger.LogWarning(ex, "OpenAI returned invalid rule JSON; attempting one repair.");
                continue;
            }
            catch (HttpRequestException ex) when (attemptIndex == 0 && IsTransientUpstreamFailure(ex))
            {
                repairDiagnostics = new[] { "UPSTREAM_TRANSIENT_HTTP_ERROR" };
                app.Logger.LogWarning(ex, "OpenAI returned a transient HTTP error; attempting one retry.");
                continue;
            }

            ApiPolicies.ApplyServerOwnedFields(ruleSet, requestKey, snapshot.turn, snapshot);
            var validation = ValidateGeneratedRuleSet(ruleSet, snapshot);
            if (validation.valid) break;

            validationFailures++;
            if (attemptIndex == 0)
            {
                repairDiagnostics = validation.errors.Concat(validation.diagnostics).Distinct().ToArray();
                continue;
            }

            ruleSet = null;
            error = "VALIDATION_FAILED_AFTER_RETRY";
        }
    }
    catch (OperationCanceledException ex)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            app.Logger.LogInformation(ex, "Rule generation request was cancelled by the caller.");
            error = "REQUEST_CANCELLED";
        }
        else
        {
            app.Logger.LogWarning(ex, "OpenAI request timed out.");
            error = "UPSTREAM_TIMEOUT";
        }
    }
    catch (HttpRequestException ex)
    {
        app.Logger.LogWarning(ex, "OpenAI request failed.");
        error = "UPSTREAM_ERROR";
    }
    catch (JsonException ex)
    {
        validationFailures++;
        app.Logger.LogWarning(ex, "OpenAI returned invalid JSON.");
        error = "UPSTREAM_INVALID_JSON";
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Unexpected rule generation failure.");
        error = "INTERNAL_ERROR";
    }

    string? responseJson = null;
    if (error == null && ruleSet != null)
    {
        try
        {
            responseJson = JsonSerializer.Serialize(ruleSet, ruleSetJsonOptions);
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Validated rule response could not be persisted.");
            ruleSet = null;
            error = "RESPONSE_SERIALIZATION_FAILED";
        }
    }

    var totalLatencyMilliseconds = (int)Math.Min(int.MaxValue, Math.Max(0, (DateTime.UtcNow - started).TotalMilliseconds));
    bool completionAccepted;
    try
    {
        completionAccepted = ApiPolicies.CompleteRuleRequest(
            db,
            requestKey,
            claim.LeaseToken,
            totalLatencyMilliseconds,
            responseJson,
            error,
            responseUsage,
            upstreamAttempts,
            validationFailures);
    }
    catch (SqliteException ex)
    {
        app.Logger.LogError(ex, "Could not persist the idempotent request result.");
        return Results.Json(new { error = "REQUEST_STORE_UNAVAILABLE" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (!completionAccepted)
        return Results.Conflict(new { error = "REQUEST_SUPERSEDED" });

    context.Response.Headers["X-OnlyMyGame-Generation-Attempts"] = upstreamAttempts.ToString(CultureInfo.InvariantCulture);
    var totalServerDurationMilliseconds = (int)Math.Min(int.MaxValue, Math.Max(0, (DateTime.UtcNow - started).TotalMilliseconds));
    context.Response.Headers["Server-Timing"] = "total;dur=" + totalServerDurationMilliseconds.ToString(CultureInfo.InvariantCulture);

    return error == null
        ? Results.Ok(ruleSet)
        : Results.Json(new { error = "RULE_GENERATION_UNAVAILABLE" }, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.Run();

static JsonSerializerOptions CreateRuleSetJsonOptions()
{
    var options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        IncludeFields = true
    };
    options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    return options;
}

static Dictionary<string, object> CreateRuleSetSchema()
{
    var condition = ClosedSchema(new Dictionary<string, object>
    {
        ["op"] = EnumSchema("always", "equal", "greaterOrEqual", "lessOrEqual", "hasTag", "ownerIs"),
        ["left"] = StringSchema(RuleLimits.MaxIdentifierLength),
        ["value"] = IntegerSchema(-RuleLimits.MaxStateMagnitude, RuleLimits.MaxStateMagnitude),
        ["text"] = StringSchema(RuleLimits.MaxIdentifierLength),
        ["all"] = ArraySchema(RefSchema("#/$defs/condition"), 0, RuleLimits.MaxConditionNodes)
    });
    var effect = ClosedSchema(new Dictionary<string, object>
    {
        ["type"] = EnumSchema("resource", "sp", "relation", "status", "spawn", "unlockAction", "schedule", "factionSwitch"),
        ["resource"] = EnumSchema("none", "food", "wood", "stone", "iron", "coin"),
        ["amount"] = IntegerSchema(-RuleLimits.MaxEffectMagnitude, RuleLimits.MaxEffectMagnitude),
        ["target"] = StringSchema(RuleLimits.MaxIdentifierLength),
        ["key"] = StringSchema(RuleLimits.MaxIdentifierLength),
        ["value"] = StringSchema(RuleLimits.MaxDescriptionLength),
        ["delay"] = IntegerSchema(0, RuleLimits.MaxScheduleDelay)
    });
    var rule = ClosedSchema(new Dictionary<string, object>
    {
        ["id"] = StringSchema(RuleLimits.MaxIdentifierLength, 1),
        ["name"] = StringSchema(RuleLimits.MaxNameLength, 1),
        ["description"] = StringSchema(RuleLimits.MaxDescriptionLength),
        ["trigger"] = EnumSchema("turnStart", "turnEnd", "move", "attack", "kill", "gather", "build", "trade", "relationChanged", "tileEntered"),
        ["condition"] = RefSchema("#/$defs/condition"),
        ["effects"] = ArraySchema(RefSchema("#/$defs/effect"), 1, RuleLimits.MaxEffectsPerRule),
        ["priority"] = IntegerSchema(-RuleLimits.MaxEffectMagnitude, RuleLimits.MaxEffectMagnitude),
        ["durationTurns"] = IntegerSchema(1, 30),
        ["appliedTurn"] = IntegerSchema(0, RuleLimits.MaxStateMagnitude),
        ["worldCue"] = StringSchema(RuleLimits.MaxNameLength)
    });
    var action = ClosedSchema(new Dictionary<string, object>
    {
        ["id"] = StringSchema(RuleLimits.MaxIdentifierLength, 1),
        ["name"] = StringSchema(RuleLimits.MaxNameLength, 1),
        ["description"] = StringSchema(RuleLimits.MaxDescriptionLength),
        ["spCost"] = IntegerSchema(0, 10),
        ["resourceCost"] = EnumSchema("none", "food", "wood", "stone", "iron", "coin"),
        ["resourceAmount"] = IntegerSchema(0, RuleLimits.MaxEffectMagnitude),
        ["cooldown"] = IntegerSchema(0, RuleLimits.MaxScheduleDelay),
        ["availableTurn"] = IntegerSchema(0, RuleLimits.MaxStateMagnitude),
        ["condition"] = RefSchema("#/$defs/condition"),
        ["effects"] = ArraySchema(RefSchema("#/$defs/effect"), 1, RuleLimits.MaxEffectsPerRule)
    });
    var contract = ClosedSchema(new Dictionary<string, object>
    {
        ["id"] = StringSchema(RuleLimits.MaxIdentifierLength, 1),
        ["title"] = StringSchema(RuleLimits.MaxNameLength, 1),
        ["description"] = StringSchema(RuleLimits.MaxDescriptionLength),
        ["progressKey"] = EnumSchema("turn", "kills", "buildings", "coin", "move", "gather", "hunt", "attack", "trade", "persuade", "hire", "build", "upgrade"),
        ["target"] = IntegerSchema(1, RuleLimits.MaxStateMagnitude),
        ["minimumTurns"] = IntegerSchema(3, RuleLimits.MaxScheduleDelay),
        ["announcedTurn"] = IntegerSchema(0, RuleLimits.MaxStateMagnitude),
        ["achievableFromTurn"] = IntegerSchema(0, RuleLimits.MaxStateMagnitude),
        ["replaceWarningTurn"] = IntegerSchema(0, RuleLimits.MaxStateMagnitude),
        ["worldCue"] = StringSchema(RuleLimits.MaxNameLength)
    });
    var root = ClosedSchema(new Dictionary<string, object>
    {
        ["schemaVersion"] = EnumSchema("v1"),
        ["requestId"] = StringSchema(128),
        ["applyTurn"] = IntegerSchema(0, RuleLimits.MaxStateMagnitude),
        ["koreanSummary"] = StringSchema(RuleLimits.MaxDescriptionLength, 1),
        ["changes"] = ArraySchema(RefSchema("#/$defs/rule"), 1, 3),
        ["actions"] = ArraySchema(RefSchema("#/$defs/action"), 0, 16),
        ["victoryContracts"] = ArraySchema(RefSchema("#/$defs/contract"), 0, RuleLimits.MaxVictoryContracts)
    });
    root["$defs"] = new Dictionary<string, object>
    {
        ["condition"] = condition,
        ["effect"] = effect,
        ["rule"] = rule,
        ["action"] = action,
        ["contract"] = contract
    };
    return root;
}

static Dictionary<string, object> ClosedSchema(Dictionary<string, object> properties)
{
    return new Dictionary<string, object>
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = properties.Keys.ToArray(),
        ["properties"] = properties
    };
}

static Dictionary<string, object> StringSchema(int maximum, int minimum = 0) => new()
{
    ["type"] = "string",
    ["minLength"] = minimum,
    ["maxLength"] = maximum
};

static Dictionary<string, object> IntegerSchema(int minimum, int maximum) => new()
{
    ["type"] = "integer",
    ["minimum"] = minimum,
    ["maximum"] = maximum
};

static Dictionary<string, object> EnumSchema(params string[] values) => new()
{
    ["type"] = "string",
    ["enum"] = values
};

static Dictionary<string, object> ArraySchema(object items, int minimum, int maximum) => new()
{
    ["type"] = "array",
    ["minItems"] = minimum,
    ["maxItems"] = maximum,
    ["items"] = items
};

static Dictionary<string, object> RefSchema(string reference) => new() { ["$ref"] = reference };

static string ComputeSessionIpHash(string dailySalt, string ip)
{
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dailySalt + "|session|" + ip)));
}

static Uri ParseOpenAiBaseAddress(string? configuredAddress)
{
    var candidate = string.IsNullOrWhiteSpace(configuredAddress)
        ? "https://api.openai.com/"
        : configuredAddress.Trim();
    if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
        || !string.IsNullOrEmpty(uri.UserInfo)
        || !string.IsNullOrEmpty(uri.Query)
        || !string.IsNullOrEmpty(uri.Fragment)
        || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        throw new InvalidOperationException("ONLYMYGAME_OPENAI_BASE_URL must be an HTTPS URL or a loopback HTTP URL.");

    return uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
        ? uri
        : new Uri(uri.AbsoluteUri + "/", UriKind.Absolute);
}

static RuleValidationResult ValidateGeneratedRuleSet(RuleSetV1 ruleSet, GameSnapshotV1 snapshot)
{
    var structuralErrors = ApiPolicies.ValidateRuleSet(ruleSet)
        .Concat(ApiPolicies.ValidateGenerationLifecycle(ruleSet, snapshot))
        .Distinct()
        .ToArray();
    if (structuralErrors.Length > 0)
    {
        return new RuleValidationResult
        {
            valid = false,
            errors = structuralErrors.ToList(),
            diagnostics = new List<string> { "생성된 규칙 JSON의 필수 필드와 컬렉션을 확인하세요." }
        };
    }

    try
    {
        return RuleValidator.Validate(ruleSet, snapshot);
    }
    catch (Exception ex)
    {
        return new RuleValidationResult
        {
            valid = false,
            errors = new List<string> { "RULE_VALIDATOR_EXCEPTION:" + ex.GetType().Name },
            diagnostics = new List<string> { "생성된 규칙 구조가 안전 검증기에 적합하지 않습니다." }
        };
    }
}

static async Task<RuleSetV1> GenerateRules(
    GameSnapshotV1 snapshot,
    HttpClient client,
    string? apiKey,
    string modelName,
    CancellationToken cancellationToken,
    ResponseTokenUsage observedUsage,
    IEnumerable<string>? repair = null)
{
    if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("OPENAI_API_KEY_NOT_CONFIGURED");

    var schema = CreateRuleSetSchema();
    var activeRuleIds = (snapshot.activeRules ?? new List<RuleNodeV1>())
        .Where(rule => rule != null && GameRules.IsRuleActive(rule, snapshot.turn))
        .Select(rule => rule.id)
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .ToArray();
    var contractIds = (snapshot.victoryContracts ?? new List<VictoryContractV1>())
        .Where(contract => contract != null)
        .Select(contract => contract.id)
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .ToArray();
    var capacityPrompt = activeRuleIds.Length >= RuleLimits.MaxActiveRules
        ? "활성 규칙이 12개로 가득 찼다. changes의 id는 반드시 기존 활성 규칙 id 중 하나를 재사용해 수정 또는 종료하라. 기존 활성 id: " + string.Join(",", activeRuleIds) + ". "
        : string.Empty;
    var contractPrompt = contractIds.Length == 0
        ? "공개 승리 계약이 없으므로 victoryContracts에 달성 가능한 계약을 반드시 1개 만들고 minimumTurns를 18 이상으로 설정하라. "
        : contractIds.Length >= RuleLimits.MaxVictoryContracts
            ? "승리 계약이 3개로 가득 찼다. 새 id를 만들지 말고 기존 id만 사용하라. 계약 교체는 같은 id로 제안하며 서버가 최소 유지 기간과 1턴 사전 경고를 강제한다. 기존 계약 id: " + string.Join(",", contractIds) + ". "
            : "계약을 교체하려면 기존 id를 재사용하라. 실제 교체 전 최소 유지 기간과 1턴 사전 경고가 필요하다. 기존 계약 id: " + string.Join(",", contractIds) + ". ";
    var prompt = "당신은 OnlyMyGame의 안전한 규칙 설계자다. 한국어 RuleSetV1 JSON만 출력한다. strict schema의 모든 필드를 채우고 사용하지 않는 문자열은 빈 문자열, condition.all은 빈 배열로 둬라. changes는 절대로 비워 두지 말고 정확히 1~3개의 규칙을 넣어라. 각 규칙에는 id, name, description, trigger(turnStart/turnEnd/move/attack/kill/gather/build/trade/relationChanged/tileEntered), condition(op는 always 사용 가능), effects(반드시 1개 이상), priority, durationTurns(1~30), appliedTurn, worldCue를 넣어라. effects에는 type resource, resource food, amount 1처럼 안전한 양수 효과를 사용해도 된다. actions는 만들 항목이 없으면 빈 배열로 둬라. 즉시 승리·패배, 숨은 규칙, 음수 자원, 코드 실행, 반복·재귀는 절대 만들지 마라. 새 승리조건은 즉시 달성할 수 없고 minimumTurns가 18 이상이어야 한다. "
        + capacityPrompt
        + contractPrompt
        + (repair == null ? string.Empty : "이전 검증 진단: " + string.Join(";", repair));
    var jsonOptions = CreateRuleSetJsonOptions();
    var payload = new
    {
        model = modelName,
        reasoning = new { effort = "medium" },
        input = new[]
        {
            new { role = "system", content = new[] { new { type = "input_text", text = prompt } } },
            new { role = "user", content = new[] { new { type = "input_text", text = JsonSerializer.Serialize(snapshot, jsonOptions) } } }
        },
        text = new { format = new { type = "json_schema", name = "onlymygame_ruleset", strict = true, schema } }
    };

    using var request = new HttpRequestMessage(HttpMethod.Post, "v1/responses") { Content = JsonContent.Create(payload) };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    response.EnsureSuccessStatusCode();
    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    observedUsage.Add(ApiPolicies.ParseResponseUsage(document.RootElement));

    string? json = null;
    if (document.RootElement.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("type", out var type)
                    && type.GetString() == "output_text"
                    && part.TryGetProperty("text", out var text))
                {
                    json = text.GetString();
                    break;
                }
            }

            if (!string.IsNullOrWhiteSpace(json)) break;
        }
    }

    if (string.IsNullOrWhiteSpace(json)) throw new JsonException("EMPTY_OUTPUT_TEXT");
    return JsonSerializer.Deserialize<RuleSetV1>(json, jsonOptions) ?? throw new JsonException("EMPTY_RULESET");
}

static bool IsTransientUpstreamFailure(HttpRequestException exception)
{
    return exception.StatusCode is null
           or System.Net.HttpStatusCode.RequestTimeout
           or System.Net.HttpStatusCode.TooManyRequests
           || (int)exception.StatusCode.Value >= 500;
}

static void TryPruneExpiredData(SqliteConnection database, int retentionDays, ILogger logger)
{
    try
    {
        var result = ApiPolicies.PruneExpiredData(database, DateTime.UtcNow, retentionDays);
        if (result.Performed && (result.RequestLogsDeleted + result.AttemptsDeleted + result.SessionsDeleted) > 0)
        {
            logger.LogInformation(
                "Retention prune removed {RequestLogs} request logs, {Attempts} attempts, and {Sessions} sessions.",
                result.RequestLogsDeleted,
                result.AttemptsDeleted,
                result.SessionsDeleted);
        }
    }
    catch (SqliteException ex)
    {
        logger.LogWarning(ex, "Retention prune was deferred because the request store was unavailable.");
    }
}

public partial class Program
{
}

public sealed class SessionRequest
{
    public string? runId { get; set; }
}
