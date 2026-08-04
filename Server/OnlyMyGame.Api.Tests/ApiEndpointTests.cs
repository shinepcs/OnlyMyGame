using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using OnlyMyGame.Api;
using OnlyMyGame.Core;

namespace OnlyMyGame.Api.Tests;

public sealed class ApiEndpointTests : IAsyncLifetime
{
    private readonly string temporaryDirectory = Path.Combine(Path.GetTempPath(), "onlymygame-api-tests-" + Guid.NewGuid().ToString("N"));
    private readonly StringBuilder processOutput = new();
    private readonly ConcurrentQueue<MockUpstreamResponse> upstreamResponses = new();
    private readonly ConcurrentQueue<string> upstreamRequestBodies = new();
    private Process? apiProcess;
    private HttpClient? client;
    private HttpListener? upstreamListener;
    private CancellationTokenSource? upstreamCancellation;
    private Task? upstreamLoop;
    private int upstreamRequestCount;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(temporaryDirectory);
        var upstreamPort = FindFreeTcpPort();
        upstreamListener = new HttpListener();
        upstreamListener.Prefixes.Add($"http://127.0.0.1:{upstreamPort}/");
        upstreamListener.Start();
        upstreamCancellation = new CancellationTokenSource();
        upstreamLoop = RunUpstreamAsync(upstreamListener, upstreamCancellation.Token);
        var port = FindFreeTcpPort();
        var apiAssembly = Path.GetFullPath(
            "../../../../OnlyMyGame.Api/bin/Release/net8.0/OnlyMyGame.Api.dll",
            AppContext.BaseDirectory);
        Assert.True(File.Exists(apiAssembly), "API assembly was not built: " + apiAssembly);

        var startInfo = new ProcessStartInfo("dotnet", '"' + apiAssembly + '"')
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        startInfo.Environment["ONLYMYGAME_DB"] = Path.Combine(temporaryDirectory, "api.db");
        startInfo.Environment["ONLYMYGAME_ALLOWED_ORIGIN"] = "https://shinepcs.github.io/OnlyMyGame/";
        startInfo.Environment["OPENAI_API_KEY"] = "test-key";
        startInfo.Environment["ONLYMYGAME_DAILY_SALT"] = "test-salt";
        startInfo.Environment["ONLYMYGAME_DAILY_LIMIT"] = "1";
        startInfo.Environment["ONLYMYGAME_UPSTREAM_TIMEOUT_SECONDS"] = "1";
        startInfo.Environment["ONLYMYGAME_READINESS_CACHE_SECONDS"] = "1";
        startInfo.Environment["ONLYMYGAME_TRUSTED_PROXIES"] = "127.0.0.1,::1,::ffff:127.0.0.1";
        startInfo.Environment["ONLYMYGAME_OPENAI_BASE_URL"] = $"http://127.0.0.1:{upstreamPort}/";

        apiProcess = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the API process.");
        apiProcess.OutputDataReceived += (_, args) => AppendOutput(args.Data);
        apiProcess.ErrorDataReceived += (_, args) => AppendOutput(args.Data);
        apiProcess.BeginOutputReadLine();
        apiProcess.BeginErrorReadLine();
        client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        client.DefaultRequestHeaders.TryAddWithoutValidation(ApiPolicies.RuleCompatibilityHeader, ApiPolicies.RuleCompatibilityVersion);

        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (apiProcess.HasExited) throw new InvalidOperationException("API exited during startup.\n" + ReadOutput());
            try
            {
                using var response = await client.GetAsync("/health");
                if (response.StatusCode == HttpStatusCode.OK) return;
            }
            catch (HttpRequestException)
            {
                // Kestrel has not bound the loopback port yet.
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("API did not become healthy.\n" + ReadOutput());
    }

    [Fact]
    public async Task Preflight_ReturnsCorsHeadersForProductionWebOrigin()
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/v1/rules/generate");
        request.Headers.TryAddWithoutValidation("Origin", "https://shinepcs.github.io");
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "content-type,idempotency-key,x-unity-version,x-rules-compatibility");

        using var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("https://shinepcs.github.io", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("POST", response.Headers.GetValues("Access-Control-Allow-Methods").Single(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("idempotency-key", response.Headers.GetValues("Access-Control-Allow-Headers").Single(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("authorization", response.Headers.GetValues("Access-Control-Allow-Headers").Single(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x-unity-version", response.Headers.GetValues("Access-Control-Allow-Headers").Single(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x-rules-compatibility", response.Headers.GetValues("Access-Control-Allow-Headers").Single(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Health_ChecksDatabaseAndRequiredConfiguration()
    {
        using var response = await Client.GetAsync("/health");
        var payload = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, payload + "\n" + ReadOutput());
        Assert.Contains("\"database\":\"ok\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"configured\":true", payload, StringComparison.Ordinal);
        Assert.Contains(ApiPolicies.RuleCompatibilityVersion, payload, StringComparison.Ordinal);
        Assert.Contains("\"retentionDays\":30", payload, StringComparison.Ordinal);
        await using var database = new SqliteConnection("Data Source=" + Path.Combine(temporaryDirectory, "api.db"));
        await database.OpenAsync();
        await using var probeRows = new SqliteCommand(
            "SELECT COUNT(*) FROM service_maintenance WHERE name='__onlymygame_readiness_probe__'",
            database);
        Assert.Equal(0L, (long)(await probeRows.ExecuteScalarAsync() ?? 0L));
    }

    [Fact]
    public async Task RuleEndpoints_RejectMissingOrMismatchedCompatibilityContract()
    {
        using var incompatibleClient = new HttpClient { BaseAddress = Client.BaseAddress };
        using (var missingSession = new HttpRequestMessage(HttpMethod.Post, "/v1/sessions")
        {
            Content = JsonContent.Create(new { runId = "missing-contract" })
        })
        using (var response = await incompatibleClient.SendAsync(missingSession))
        {
            var payload = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("RULE_COMPATIBILITY_MISMATCH", payload, StringComparison.Ordinal);
            Assert.Contains(ApiPolicies.RuleCompatibilityVersion, payload, StringComparison.Ordinal);
        }

        using (var mismatchedSession = new HttpRequestMessage(HttpMethod.Post, "/v1/sessions")
        {
            Content = JsonContent.Create(new { runId = "mismatched-contract" })
        })
        {
            mismatchedSession.Headers.TryAddWithoutValidation(ApiPolicies.RuleCompatibilityHeader, "rules-v3-legacy");
            using var response = await incompatibleClient.SendAsync(mismatchedSession);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        const string forwardedClientIp = "198.51.100.94";
        var snapshot = ValidSnapshot("missing-generation-contract");
        var token = await IssueSessionAsync(snapshot.runId, forwardedClientIp);
        using var generation = new HttpRequestMessage(HttpMethod.Post, "/v1/rules/generate")
        {
            Content = new StringContent(JsonSerializer.Serialize(snapshot, FieldJsonOptions()), Encoding.UTF8, "application/json")
        };
        generation.Headers.TryAddWithoutValidation("Idempotency-Key", "missing-generation-contract-key");
        generation.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedClientIp);
        generation.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var generationResponse = await incompatibleClient.SendAsync(generation);
        Assert.Equal(HttpStatusCode.Conflict, generationResponse.StatusCode);
        Assert.Equal(0, Volatile.Read(ref upstreamRequestCount));
    }

    [Fact]
    public async Task ReadinessRequiresDatabaseWriteWhileLivenessRemainsAvailable()
    {
        await Task.Delay(1_200);
        using (var prime = await Client.GetAsync("/health"))
            Assert.Equal(HttpStatusCode.OK, prime.StatusCode);
        await using var database = new SqliteConnection("Data Source=" + Path.Combine(temporaryDirectory, "api.db"));
        await database.OpenAsync();
        await using var writeLock = database.BeginTransaction(deferred: false);

        using (var cachedReadiness = await Client.GetAsync("/health"))
            Assert.Equal(HttpStatusCode.OK, cachedReadiness.StatusCode);
        await Task.Delay(1_200);
        using (var readiness = await Client.GetAsync("/health"))
        {
            var payload = await readiness.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
            Assert.Contains("\"database\":\"unavailable\"", payload, StringComparison.Ordinal);
            Assert.Contains("\"configured\":true", payload, StringComparison.Ordinal);
        }
        using (var liveness = await Client.GetAsync("/live"))
        {
            var payload = await liveness.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
            Assert.Contains("\"status\":\"ok\"", payload, StringComparison.Ordinal);
            Assert.Contains(ApiPolicies.RuleCompatibilityVersion, payload, StringComparison.Ordinal);
        }

        await writeLock.RollbackAsync();
        await Task.Delay(1_200);
        using var recovered = await Client.GetAsync("/health");
        var recoveredPayload = await recovered.Content.ReadAsStringAsync();
        Assert.True(recovered.StatusCode == HttpStatusCode.OK, recoveredPayload + "\n" + ReadOutput());
    }

    [Fact]
    public async Task InvalidTrustedProxyConfiguration_MakesHealthAndSessionsFailClosed()
    {
        var port = FindFreeTcpPort();
        var apiAssembly = Path.GetFullPath(
            "../../../../OnlyMyGame.Api/bin/Release/net8.0/OnlyMyGame.Api.dll",
            AppContext.BaseDirectory);
        var invalidOutput = new StringBuilder();
        var startInfo = new ProcessStartInfo("dotnet", '"' + apiAssembly + '"')
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        startInfo.Environment["ONLYMYGAME_DB"] = Path.Combine(temporaryDirectory, "invalid-proxy.db");
        startInfo.Environment["OPENAI_API_KEY"] = "test-key";
        startInfo.Environment["ONLYMYGAME_DAILY_SALT"] = "test-salt";
        startInfo.Environment["ONLYMYGAME_TRUSTED_PROXIES"] = "not-an-ip";

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the invalid-proxy API process.");
        process.OutputDataReceived += (_, args) => { if (args.Data != null) invalidOutput.AppendLine(args.Data); };
        process.ErrorDataReceived += (_, args) => { if (args.Data != null) invalidOutput.AppendLine(args.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
            using var invalidClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            invalidClient.DefaultRequestHeaders.TryAddWithoutValidation(ApiPolicies.RuleCompatibilityHeader, ApiPolicies.RuleCompatibilityVersion);
        HttpResponseMessage? healthResponse = null;
        try
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                if (process.HasExited)
                    throw new InvalidOperationException("Invalid-proxy API exited during startup.\n" + invalidOutput);
                try
                {
                    healthResponse = await invalidClient.GetAsync("/health");
                    break;
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(100);
                }
            }

            Assert.NotNull(healthResponse);
            using (healthResponse)
            {
                var payload = await healthResponse!.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.ServiceUnavailable, healthResponse.StatusCode);
                Assert.Contains("\"status\":\"unavailable\"", payload, StringComparison.Ordinal);
                Assert.Contains("\"configured\":false", payload, StringComparison.Ordinal);
            }
            using (var liveness = await invalidClient.GetAsync("/live"))
            {
                var payload = await liveness.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
                Assert.Contains("\"status\":\"ok\"", payload, StringComparison.Ordinal);
            }

            using var sessionRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/sessions")
            {
                Content = JsonContent.Create(new { runId = "invalid-proxy-run" })
            };
            using var sessionResponse = await invalidClient.SendAsync(sessionRequest);
            var sessionPayload = await sessionResponse.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, sessionResponse.StatusCode);
            Assert.Contains("SERVICE_NOT_CONFIGURED", sessionPayload, StringComparison.Ordinal);
        }
        finally
        {
            healthResponse?.Dispose();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
        }
    }

    [Fact]
    public async Task InvalidSnapshot_IsRejectedWithoutCallingOpenAi()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/rules/generate")
        {
            Content = JsonContent.Create(new { })
        };
        request.Headers.TryAddWithoutValidation("Origin", "https://shinepcs.github.io");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "invalid-snapshot-test");

        using var response = await Client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("INVALID_SNAPSHOT", payload, StringComparison.Ordinal);
        Assert.Equal("https://shinepcs.github.io", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task PartialJson_IsRepairedOnceAndPersistsGenerationTelemetry()
    {
        const string forwardedClientIp = "198.51.100.61";
        var snapshot = ValidSnapshot("partial-json-repair-run");
        upstreamResponses.Enqueue(new MockUpstreamResponse(
            (int)HttpStatusCode.OK,
            ResponsesPayload("{", inputTokens: 10, outputTokens: 4, cachedTokens: 2, cacheWriteTokens: 1, reasoningTokens: 3)));
        upstreamResponses.Enqueue(new MockUpstreamResponse(
            (int)HttpStatusCode.OK,
            ResponsesPayload(
                JsonSerializer.Serialize(ValidGeneratedRuleSet(snapshot), FieldJsonOptions()),
                inputTokens: 20,
                outputTokens: 8,
                cachedTokens: 5,
                cacheWriteTokens: 2,
                reasoningTokens: 6)));

        using var request = CreateGenerationRequest(snapshot, "partial-json-repair-key", forwardedClientIp);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await IssueSessionAsync(snapshot.runId, forwardedClientIp));
        using var response = await Client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, payload + "\n" + ReadOutput());
        Assert.Equal("2", response.Headers.GetValues("X-OnlyMyGame-Generation-Attempts").Single());
        Assert.StartsWith("total;dur=", response.Headers.GetValues("Server-Timing").Single(), StringComparison.Ordinal);
        Assert.Equal(2, Volatile.Read(ref upstreamRequestCount));
        var repairedRequestBody = upstreamRequestBodies.ElementAt(1);
        Assert.Contains("UPSTREAM_INVALID_JSON", repairedRequestBody, StringComparison.Ordinal);
        using (var requestDocument = JsonDocument.Parse(repairedRequestBody))
        {
            var schema = requestDocument.RootElement.GetProperty("text").GetProperty("format").GetProperty("schema");
            var definitions = schema.GetProperty("$defs");
            var triggerValues = definitions.GetProperty("rule").GetProperty("properties").GetProperty("trigger").GetProperty("enum")
                .EnumerateArray().Select(value => value.GetString()).ToArray();
            var progressValues = definitions.GetProperty("contract").GetProperty("properties").GetProperty("progressKey").GetProperty("enum")
                .EnumerateArray().Select(value => value.GetString()).ToArray();
            var effectValues = definitions.GetProperty("effect").GetProperty("properties").GetProperty("type").GetProperty("enum")
                .EnumerateArray().Select(value => value.GetString()).ToArray();
            Assert.Contains("capture", triggerValues);
            Assert.Contains("capture", progressValues);
            Assert.Contains("territory", progressValues);
            Assert.Contains("alliances", progressValues);
            Assert.Contains("typedState", effectValues);
            Assert.True(definitions.TryGetProperty("stateReference", out _));
            Assert.True(definitions.TryGetProperty("stateDefinition", out _));
            Assert.True(definitions.TryGetProperty("numberExpression", out _));
            Assert.True(definitions.TryGetProperty("predicateExpression", out _));
            Assert.True(definitions.TryGetProperty("stateMutation", out _));
            Assert.True(definitions.TryGetProperty("dynamicTargetSelector", out var targetSelectorSchema), definitions.GetRawText());
            var targetKinds = targetSelectorSchema.GetProperty("properties").GetProperty("kind").GetProperty("enum")
                .EnumerateArray().Select(value => value.GetString()).ToArray();
            Assert.Equal(new[] { "none", "tile", "unit", "building" }, targetKinds);
            Assert.Contains("maxCandidates", targetSelectorSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
            var ruleSchema = definitions.GetProperty("rule");
            Assert.Contains("stateDefinitions", ruleSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal("#/$defs/stateDefinition", ruleSchema.GetProperty("properties").GetProperty("stateDefinitions").GetProperty("items").GetProperty("$ref").GetString());
            var conditionSchema = definitions.GetProperty("condition");
            Assert.Contains("predicate", conditionSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(JsonValueKind.Array, conditionSchema.GetProperty("properties").GetProperty("predicate").GetProperty("anyOf").ValueKind);
            Assert.Contains("stateMutation", definitions.GetProperty("effect").GetProperty("required").EnumerateArray().Select(value => value.GetString()));
            var actionSchema = definitions.GetProperty("action");
            Assert.Contains("targetSelector", actionSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal("#/$defs/dynamicTargetSelector", actionSchema.GetProperty("properties").GetProperty("targetSelector").GetProperty("$ref").GetString());
        }

        await using var database = new SqliteConnection("Data Source=" + Path.Combine(temporaryDirectory, "api.db"));
        await database.OpenAsync();
        await using var telemetry = new SqliteCommand(
            "SELECT input_tokens,output_tokens,total_tokens,cached_input_tokens,cache_write_tokens,reasoning_tokens,upstream_attempts,validation_failures,error FROM request_log WHERE request_key='partial-json-repair-key'",
            database);
        await using var reader = await telemetry.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(30L, reader.GetInt64(0));
        Assert.Equal(12L, reader.GetInt64(1));
        Assert.Equal(42L, reader.GetInt64(2));
        Assert.Equal(7L, reader.GetInt64(3));
        Assert.Equal(3L, reader.GetInt64(4));
        Assert.Equal(9L, reader.GetInt64(5));
        Assert.Equal(2L, reader.GetInt64(6));
        Assert.Equal(1L, reader.GetInt64(7));
        Assert.Equal(string.Empty, reader.GetString(8));
    }

    [Fact]
    public async Task TransientHttpFailure_IsRetriedOnceWithoutCountingValidationFailure()
    {
        const string forwardedClientIp = "198.51.100.62";
        var snapshot = ValidSnapshot("http-retry-run");
        upstreamResponses.Enqueue(new MockUpstreamResponse(
            (int)HttpStatusCode.ServiceUnavailable,
            "{\"error\":{\"message\":\"temporary\"}}"));
        upstreamResponses.Enqueue(new MockUpstreamResponse(
            (int)HttpStatusCode.OK,
            ResponsesPayload(
                JsonSerializer.Serialize(ValidGeneratedRuleSet(snapshot), FieldJsonOptions()),
                inputTokens: 15,
                outputTokens: 5,
                cachedTokens: 1,
                cacheWriteTokens: 0,
                reasoningTokens: 2)));

        using var request = CreateGenerationRequest(snapshot, "http-retry-key", forwardedClientIp);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await IssueSessionAsync(snapshot.runId, forwardedClientIp));
        using var response = await Client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, payload + "\n" + ReadOutput());
        Assert.Equal("2", response.Headers.GetValues("X-OnlyMyGame-Generation-Attempts").Single());
        Assert.Equal(2, Volatile.Read(ref upstreamRequestCount));
        Assert.Contains("UPSTREAM_TRANSIENT_HTTP_ERROR", upstreamRequestBodies.ElementAt(1), StringComparison.Ordinal);
        await using var database = new SqliteConnection("Data Source=" + Path.Combine(temporaryDirectory, "api.db"));
        await database.OpenAsync();
        await using var telemetry = new SqliteCommand(
            "SELECT upstream_attempts,validation_failures,input_tokens,total_tokens FROM request_log WHERE request_key='http-retry-key'",
            database);
        await using var reader = await telemetry.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));
        Assert.Equal(15L, reader.GetInt64(2));
        Assert.Equal(20L, reader.GetInt64(3));
    }

    [Fact]
    public async Task InvalidJsonAfterRepair_ReturnsFinalAttemptTelemetryWithoutThirdCall()
    {
        const string forwardedClientIp = "198.51.100.63";
        var snapshot = ValidSnapshot("invalid-json-final-run");
        upstreamResponses.Enqueue(new MockUpstreamResponse(
            (int)HttpStatusCode.OK,
            ResponsesPayload("{", 3, 1, 0, 0, 1)));
        upstreamResponses.Enqueue(new MockUpstreamResponse(
            (int)HttpStatusCode.OK,
            ResponsesPayload("[", 4, 2, 0, 0, 1)));

        using var request = CreateGenerationRequest(snapshot, "invalid-json-final-key", forwardedClientIp);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await IssueSessionAsync(snapshot.runId, forwardedClientIp));
        using var response = await Client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("RULE_GENERATION_UNAVAILABLE", payload, StringComparison.Ordinal);
        Assert.Equal("2", response.Headers.GetValues("X-OnlyMyGame-Generation-Attempts").Single());
        Assert.StartsWith("total;dur=", response.Headers.GetValues("Server-Timing").Single(), StringComparison.Ordinal);
        Assert.Equal(2, Volatile.Read(ref upstreamRequestCount));
        await using var database = new SqliteConnection("Data Source=" + Path.Combine(temporaryDirectory, "api.db"));
        await database.OpenAsync();
        await using var telemetry = new SqliteCommand(
            "SELECT upstream_attempts,validation_failures,input_tokens,output_tokens,total_tokens,error FROM request_log WHERE request_key='invalid-json-final-key'",
            database);
        await using var reader = await telemetry.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2L, reader.GetInt64(0));
        Assert.Equal(2L, reader.GetInt64(1));
        Assert.Equal(7L, reader.GetInt64(2));
        Assert.Equal(3L, reader.GetInt64(3));
        Assert.Equal(10L, reader.GetInt64(4));
        Assert.Equal("UPSTREAM_INVALID_JSON", reader.GetString(5));
    }

    [Fact]
    public async Task UpstreamTimeout_IsNotRetried()
    {
        const string forwardedClientIp = "198.51.100.64";
        var snapshot = ValidSnapshot("timeout-no-retry-run");
        upstreamResponses.Enqueue(new MockUpstreamResponse(
            (int)HttpStatusCode.OK,
            ResponsesPayload(
                JsonSerializer.Serialize(ValidGeneratedRuleSet(snapshot), FieldJsonOptions()),
                10,
                3,
                0,
                0,
                1),
            DelayMilliseconds: 2_000));

        using var request = CreateGenerationRequest(snapshot, "timeout-no-retry-key", forwardedClientIp);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await IssueSessionAsync(snapshot.runId, forwardedClientIp));
        using var response = await Client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("RULE_GENERATION_UNAVAILABLE", payload, StringComparison.Ordinal);
        Assert.Equal("1", response.Headers.GetValues("X-OnlyMyGame-Generation-Attempts").Single());
        Assert.Equal(1, Volatile.Read(ref upstreamRequestCount));
        await using var database = new SqliteConnection("Data Source=" + Path.Combine(temporaryDirectory, "api.db"));
        await database.OpenAsync();
        await using var telemetry = new SqliteCommand(
            "SELECT upstream_attempts,validation_failures,input_tokens,error FROM request_log WHERE request_key='timeout-no-retry-key'",
            database);
        await using var reader = await telemetry.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));
        Assert.True(reader.IsDBNull(2));
        Assert.Equal("UPSTREAM_TIMEOUT", reader.GetString(3));
    }

    [Fact]
    public async Task TrustedProxy_UsesForwardedClientIpForDailyQuota()
    {
        const string forwardedClientIp = "203.0.113.42";
        const string runId = "proxy-quota-run";
        var day = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var ipHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(day + "test-salt" + forwardedClientIp)));
        await using (var database = new SqliteConnection("Data Source=" + Path.Combine(temporaryDirectory, "api.db")))
        {
            await database.OpenAsync();
            await using var seed = new SqliteCommand(
                "INSERT INTO request_log(day,ip_hash,request_key,created_utc,valid,error,response_json,request_hash,compatibility_version) VALUES($day,$ip,'quota-seed',$utc,1,'','{}','seed',$compat); INSERT INTO request_attempt(day,ip_hash,request_key,created_utc) VALUES($day,$ip,'quota-seed',$utc)",
                database);
            seed.Parameters.AddWithValue("$day", day);
            seed.Parameters.AddWithValue("$ip", ipHash);
            seed.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            seed.Parameters.AddWithValue("$compat", ApiPolicies.RuleCompatibilityVersion);
            await seed.ExecuteNonQueryAsync();
        }

        // Quota behavior must be exercised with a snapshot that passes the same
        // deep world-topology validation as production traffic. A malformed
        // anonymous placeholder would correctly stop at INVALID_SNAPSHOT before
        // the forwarded client identity reaches the quota claim.
        using var request = CreateGenerationRequest(ValidSnapshot(runId), "proxy-quota-test", forwardedClientIp);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await IssueSessionAsync(runId, forwardedClientIp));

        using var response = await Client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Contains("DAILY_LIMIT_REACHED", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LostSuccessfulResponse_IsReplayedAfterRetryDiagnosticsChange()
    {
        const string requestKey = "cached-response-loss-test";
        const string forwardedClientIp = "198.51.100.10";
        var snapshot = ValidSnapshot("cached-response-run");
        var requestHash = ApiPolicies.ComputeRuleRequestHash(snapshot);
        var cachedRuleSet = new RuleSetV1
        {
            requestId = requestKey,
            applyTurn = snapshot.turn,
            koreanSummary = "캐시된 안전 규칙",
            changes = new List<RuleNodeV1>
            {
                new()
                {
                    id = "cached-rule",
                    name = "비축 지원",
                    description = "식량 비축을 지원합니다.",
                    trigger = EventType.TurnStart,
                    durationTurns = 3,
                    appliedTurn = snapshot.turn,
                    worldCue = "식량 상자",
                    effects = new List<EffectNode>
                    {
                        new() { type = EffectType.Resource, resource = ResourceType.Food, amount = 1, target = "", key = "", value = "" }
                    }
                }
            },
            victoryContracts = new List<VictoryContractV1>
            {
                new()
                {
                    id = "first-contract",
                    title = "장기 생존",
                    description = "18턴 이상 생존해 승리합니다.",
                    progressKey = "turn",
                    target = snapshot.turn + 6,
                    minimumTurns = 18,
                    announcedTurn = snapshot.turn,
                    achievableFromTurn = snapshot.turn + 2,
                    worldCue = "새로운 여정"
                }
            }
        };
        var jsonOptions = FieldJsonOptions();
        var responseJson = JsonSerializer.Serialize(cachedRuleSet, jsonOptions);
        var day = DateTime.UtcNow.ToString("yyyy-MM-dd");
        await using (var database = new SqliteConnection("Data Source=" + Path.Combine(temporaryDirectory, "api.db")))
        {
            await database.OpenAsync();
            await using var seed = new SqliteCommand(
                "INSERT INTO request_log(day,ip_hash,request_key,created_utc,valid,error,response_json,request_hash,attempt_count,attempt_day,compatibility_version) VALUES($day,'ip',$key,$utc,1,'',$response,$hash,1,$day,$compat)",
                database);
            seed.Parameters.AddWithValue("$day", day);
            seed.Parameters.AddWithValue("$key", requestKey);
            seed.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            seed.Parameters.AddWithValue("$response", responseJson);
            seed.Parameters.AddWithValue("$hash", requestHash);
            seed.Parameters.AddWithValue("$compat", ApiPolicies.RuleCompatibilityVersion);
            await seed.ExecuteNonQueryAsync();
        }

        snapshot.journal.Add("응답을 받지 못해 재시도합니다.");
        snapshot.ruleBudget = new RuleRuntimeBudget { turn = snapshot.turn, dispatches = 2, effects = 4 };
        Assert.Equal(requestHash, ApiPolicies.ComputeRuleRequestHash(snapshot));
        var wireJson = JsonSerializer.Serialize(snapshot, jsonOptions);
        var wireSnapshot = JsonSerializer.Deserialize<GameSnapshotV1>(wireJson, jsonOptions);
        Assert.NotNull(wireSnapshot);
        Assert.Equal(requestHash, ApiPolicies.ComputeRuleRequestHash(wireSnapshot!));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/rules/generate")
        {
            Content = new StringContent(wireJson, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", requestKey);
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedClientIp);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await IssueSessionAsync(snapshot.runId, forwardedClientIp));

        using var response = await Client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, payload + "\n" + ReadOutput());
        Assert.Contains(requestKey, payload, StringComparison.Ordinal);
        Assert.Contains("캐시된 안전 규칙", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuleGeneration_RejectsNonAwaitingLifecycleBeforeUpstream()
    {
        var before = Volatile.Read(ref upstreamRequestCount);
        var snapshot = ValidSnapshot("invalid-lifecycle-run");
        snapshot.phase = RunPhase.Planning;
        using var request = CreateGenerationRequest(snapshot, "invalid-lifecycle-key", "198.51.100.77");

        using var response = await Client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("RULE_GENERATION_LIFECYCLE_INVALID", payload, StringComparison.Ordinal);
        Assert.Equal(before, Volatile.Read(ref upstreamRequestCount));
    }

    [Fact]
    public async Task CoreDeepValidation_RejectsInvalidGameplayStateBeforeUpstream()
    {
        const string forwardedClientIp = "198.51.100.40";
        var snapshot = ValidSnapshot("deep-validation-run");
        snapshot.factions.Single().resources.food = -1;
        var jsonOptions = FieldJsonOptions();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/rules/generate")
        {
            Content = new StringContent(JsonSerializer.Serialize(snapshot, jsonOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "deep-validation-test");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedClientIp);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await IssueSessionAsync(snapshot.runId, forwardedClientIp));

        using var response = await Client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("INVALID_SNAPSHOT", payload, StringComparison.Ordinal);
        Assert.Contains("FACTION_STATE_INVALID", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CoreDeepValidation_RejectsStoredRuleAstBeforeUpstream()
    {
        const string forwardedClientIp = "198.51.100.41";
        var snapshot = ValidSnapshot("deep-ast-run");
        var root = new ConditionNode { op = CompareOp.Always, all = new List<ConditionNode>() };
        var cursor = root;
        for (var depth = 0; depth < RuleLimits.MaxConditionDepth + 1; depth++)
        {
            var child = new ConditionNode { op = CompareOp.Always, all = new List<ConditionNode>() };
            cursor.all.Add(child);
            cursor = child;
        }
        snapshot.activeRules.Add(new RuleNodeV1
        {
            id = "malformed-depth",
            name = "악성 중첩",
            description = "검증 전에 거부되어야 합니다.",
            trigger = EventType.TurnStart,
            condition = root,
            appliedTurn = snapshot.turn,
            durationTurns = 3,
            effects = new List<EffectNode> { new() { type = EffectType.Resource, resource = ResourceType.Food, amount = 1 } }
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/rules/generate")
        {
            Content = new StringContent(JsonSerializer.Serialize(snapshot, FieldJsonOptions()), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "deep-ast-test");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedClientIp);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await IssueSessionAsync(snapshot.runId, forwardedClientIp));

        using var response = await Client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("AST_DEPTH_LIMIT", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_IsBoundToRunAndTrustedClientIp()
    {
        const string forwardedClientIp = "198.51.100.20";
        var token = await IssueSessionAsync("session-run-a", forwardedClientIp);
        var snapshot = ValidSnapshot("session-run-b");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/rules/generate")
        {
            Content = new StringContent(JsonSerializer.Serialize(snapshot, FieldJsonOptions()), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "session-binding-test");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedClientIp);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
    }

    [Fact]
    public async Task RuleGeneration_RequiresBearerSession()
    {
        var snapshot = ValidSnapshot("missing-session-run");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/rules/generate")
        {
            Content = new StringContent(JsonSerializer.Serialize(snapshot, FieldJsonOptions()), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "missing-session-test");

        using var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Sessions_EnforceTwoActiveTokensPerTrustedClientIp()
    {
        const string forwardedClientIp = "198.51.100.30";
        await IssueSessionAsync("session-limit-a", forwardedClientIp);
        await IssueSessionAsync("session-limit-b", forwardedClientIp);
        using var third = new HttpRequestMessage(HttpMethod.Post, "/v1/sessions")
        {
            Content = JsonContent.Create(new { runId = "session-limit-c" })
        };
        third.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedClientIp);

        using var response = await Client.SendAsync(third);
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Contains("ACTIVE_SESSION_LIMIT_REACHED", payload, StringComparison.Ordinal);
    }

    public async Task DisposeAsync()
    {
        client?.Dispose();
        if (apiProcess is { HasExited: false })
        {
            apiProcess.Kill(entireProcessTree: true);
            apiProcess.WaitForExit(5_000);
        }
        apiProcess?.Dispose();
        upstreamCancellation?.Cancel();
        upstreamListener?.Close();
        if (upstreamLoop != null)
        {
            try
            {
                await upstreamLoop;
            }
            catch (OperationCanceledException)
            {
                // Expected when the per-test upstream stub shuts down.
            }
        }
        upstreamCancellation?.Dispose();
        try
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
        catch (IOException)
        {
            // SQLite can release its final native handle just after process exit.
        }
    }

    private HttpClient Client => client ?? throw new InvalidOperationException("Test client was not initialized.");

    private void AppendOutput(string? line)
    {
        if (line == null) return;
        lock (processOutput) processOutput.AppendLine(line);
    }

    private string ReadOutput()
    {
        lock (processOutput) return processOutput.ToString();
    }

    private static int FindFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static GameSnapshotV1 ValidSnapshot(string runId)
    {
        return new GameSnapshotV1
        {
            runId = runId,
            turn = 7,
            seed = 123,
            luck = 50,
            phase = RunPhase.AwaitingRules,
            catalogHash = "catalog",
            map = new List<TileState>
            {
                new() { position = new HexCoord(0, 0), terrain = "Grass", owner = 1, explored = true, visible = true }
            },
            factions = new List<FactionState>
            {
                new() { id = 1, name = "플레이어", kind = FactionKind.Player, maxSp = 10, sp = 10 }
            }
        };
    }

    private static RuleSetV1 ValidGeneratedRuleSet(GameSnapshotV1 snapshot)
    {
        return new RuleSetV1
        {
            koreanSummary = "검증 가능한 안전 규칙",
            changes = new List<RuleNodeV1>
            {
                new()
                {
                    id = "generated-support",
                    name = "비축 지원",
                    description = "매 턴 식량 비축을 지원합니다.",
                    trigger = EventType.TurnStart,
                    condition = new ConditionNode { op = CompareOp.Always, all = new List<ConditionNode>() },
                    effects = new List<EffectNode>
                    {
                        new()
                        {
                            type = EffectType.Resource,
                            resource = ResourceType.Food,
                            amount = 1,
                            target = string.Empty,
                            key = string.Empty,
                            value = string.Empty
                        }
                    },
                    priority = 0,
                    durationTurns = 3,
                    worldCue = "보급 상자"
                }
            },
            actions = new List<DynamicActionV1>(),
            victoryContracts = new List<VictoryContractV1>
            {
                new()
                {
                    id = "generated-contract",
                    title = "장기 생존",
                    description = "충분한 기간 생존해 승리합니다.",
                    progressKey = "turn",
                    target = snapshot.turn + 6,
                    minimumTurns = 18,
                    worldCue = "생존 기념비"
                }
            }
        };
    }

    private static HttpRequestMessage CreateGenerationRequest(GameSnapshotV1 snapshot, string requestKey, string forwardedClientIp)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/rules/generate")
        {
            Content = new StringContent(JsonSerializer.Serialize(snapshot, FieldJsonOptions()), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", requestKey);
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedClientIp);
        return request;
    }

    private static string ResponsesPayload(
        string outputText,
        int inputTokens,
        int outputTokens,
        int cachedTokens,
        int cacheWriteTokens,
        int reasoningTokens)
    {
        return JsonSerializer.Serialize(new
        {
            output = new[]
            {
                new
                {
                    content = new[] { new { type = "output_text", text = outputText } }
                }
            },
            usage = new
            {
                input_tokens = inputTokens,
                output_tokens = outputTokens,
                total_tokens = inputTokens + outputTokens,
                input_tokens_details = new { cached_tokens = cachedTokens, cache_write_tokens = cacheWriteTokens },
                output_tokens_details = new { reasoning_tokens = reasoningTokens }
            }
        });
    }

    private async Task RunUpstreamAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested || !listener.IsListening)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            try
            {
                using var bodyReader = new StreamReader(
                    context.Request.InputStream,
                    context.Request.ContentEncoding ?? Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false,
                    leaveOpen: true);
                upstreamRequestBodies.Enqueue(await bodyReader.ReadToEndAsync(cancellationToken));
                Interlocked.Increment(ref upstreamRequestCount);
                if (!upstreamResponses.TryDequeue(out var stubResponse))
                {
                    stubResponse = new MockUpstreamResponse(
                        (int)HttpStatusCode.ServiceUnavailable,
                        "{\"error\":{\"message\":\"No upstream response was queued.\"}}");
                }

                if (stubResponse.DelayMilliseconds > 0)
                    await Task.Delay(stubResponse.DelayMilliseconds, cancellationToken);
                var bytes = Encoding.UTF8.GetBytes(stubResponse.Body);
                context.Response.StatusCode = stubResponse.StatusCode;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.KeepAlive = false;
                await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The API request or test fixture was cancelled.
            }
            catch (IOException)
            {
                // The API client can disconnect after exercising its timeout path.
            }
            finally
            {
                context.Response.Close();
            }
        }
    }

    private static JsonSerializerOptions FieldJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            IncludeFields = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private async Task<string> IssueSessionAsync(string runId, string forwardedClientIp)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/sessions")
        {
            Content = JsonContent.Create(new { runId })
        };
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedClientIp);
        using var response = await Client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, payload + "\n" + ReadOutput());
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("Session response did not include a token.");
    }

    private sealed record MockUpstreamResponse(int StatusCode, string Body, int DelayMilliseconds = 0);
}
