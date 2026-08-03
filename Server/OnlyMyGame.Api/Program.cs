using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OnlyMyGame.Core;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var allowedOrigin = config["ONLYMYGAME_ALLOWED_ORIGIN"] ?? "https://example.github.io";
var dbPath = config["ONLYMYGAME_DB"] ?? "/data/onlymygame.db";
Directory.CreateDirectory(Path.GetDirectoryName(dbPath) ?? ".");
var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
using (var db = new SqliteConnection(connectionString)) { db.Open(); new SqliteCommand("CREATE TABLE IF NOT EXISTS request_log (id INTEGER PRIMARY KEY, day TEXT NOT NULL, ip_hash TEXT NOT NULL, request_key TEXT NOT NULL UNIQUE, created_utc TEXT NOT NULL, latency_ms INTEGER, valid INTEGER, error TEXT);", db).ExecuteNonQuery(); }
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(allowedOrigin).AllowAnyHeader().WithMethods("GET", "POST")));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
builder.Services.AddHttpClient("openai", c => { c.BaseAddress = new Uri("https://api.openai.com/"); c.Timeout = TimeSpan.FromSeconds(20); });
var app = builder.Build(); app.UseCors();
app.MapGet("/health", () => Results.Ok(new { status = "ok", model = "gpt-5.6-luna", database = "ok", configured = !string.IsNullOrWhiteSpace(config["OPENAI_API_KEY"]) }));
app.MapPost("/v1/sessions", (HttpContext context) => Results.Ok(new { token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)), expiresInSeconds = 3600 }));
app.MapPost("/v1/rules/generate", async (HttpContext context, GameSnapshotV1 snapshot, IHttpClientFactory clients, CancellationToken ct) =>
{
    if (snapshot == null || snapshot.factions == null || snapshot.map == null) return Results.BadRequest(new { error = "INVALID_SNAPSHOT" });
    if (context.Request.ContentLength is > 1_000_000) return Results.StatusCode(413);
    var key = context.Request.Headers["Idempotency-Key"].FirstOrDefault(); if (string.IsNullOrWhiteSpace(key)) return Results.BadRequest(new { error = "IDEMPOTENCY_KEY_REQUIRED" });
    var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown"; var day = DateTime.UtcNow.ToString("yyyy-MM-dd"); var salt = config["ONLYMYGAME_DAILY_SALT"] ?? "change-me";
    var ipHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(day + salt + ip)));
    using var db = new SqliteConnection(connectionString); await db.OpenAsync(ct);
    using (var check = new SqliteCommand("SELECT COUNT(*) FROM request_log WHERE day=$day AND ip_hash=$ip", db)) { check.Parameters.AddWithValue("$day", day); check.Parameters.AddWithValue("$ip", ipHash); if ((long)(await check.ExecuteScalarAsync(ct) ?? 0L) >= int.Parse(config["ONLYMYGAME_DAILY_LIMIT"] ?? "60")) return Results.StatusCode(429); }
    using (var duplicate = new SqliteCommand("SELECT error FROM request_log WHERE request_key=$key", db)) { duplicate.Parameters.AddWithValue("$key", key); if (await duplicate.ExecuteScalarAsync(ct) is string old) return Results.Conflict(new { error = "DUPLICATE_REQUEST", previous = old }); }
    var started = DateTime.UtcNow; RuleSetV1? set = null; string? error = null;
    try { set = await GenerateRules(snapshot, clients.CreateClient("openai"), config["OPENAI_API_KEY"], ct); var validation = RuleValidator.Validate(set, snapshot); if (!validation.valid) { set = await GenerateRules(snapshot, clients.CreateClient("openai"), config["OPENAI_API_KEY"], ct, validation.diagnostics); validation = RuleValidator.Validate(set, snapshot); if (!validation.valid) throw new InvalidOperationException(string.Join("|", validation.errors)); } }
    catch (Exception ex) { error = ex.Message; }
    using (var insert = new SqliteCommand("INSERT INTO request_log(day,ip_hash,request_key,created_utc,latency_ms,valid,error) VALUES($day,$ip,$key,$utc,$latency,$valid,$error)", db)) { insert.Parameters.AddWithValue("$day", day); insert.Parameters.AddWithValue("$ip", ipHash); insert.Parameters.AddWithValue("$key", key); insert.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O")); insert.Parameters.AddWithValue("$latency", (int)(DateTime.UtcNow - started).TotalMilliseconds); insert.Parameters.AddWithValue("$valid", set != null && error == null ? 1 : 0); insert.Parameters.AddWithValue("$error", error ?? ""); await insert.ExecuteNonQueryAsync(ct); }
    return error == null ? Results.Ok(set) : Results.StatusCode(503);
});
app.Run();

static async Task<RuleSetV1> GenerateRules(GameSnapshotV1 snapshot, HttpClient client, string? apiKey, CancellationToken ct, IEnumerable<string>? repair = null)
{
    if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("OPENAI_API_KEY_NOT_CONFIGURED");
    var schema = new { type = "object", additionalProperties = false, required = new[] { "schemaVersion", "requestId", "applyTurn", "koreanSummary", "changes", "actions", "victoryContracts" }, properties = new { schemaVersion = new { type = "string" }, requestId = new { type = "string" }, applyTurn = new { type = "integer" }, koreanSummary = new { type = "string" }, changes = new { type = "array", minItems = 1, maxItems = 3, items = new { type = "object" } }, actions = new { type = "array", items = new { type = "object" } }, victoryContracts = new { type = "array", maxItems = 3, items = new { type = "object" } } } };
    var prompt = "당신은 OnlyMyGame의 안전한 규칙 설계자다. 한국어 RuleSetV1 JSON만 출력한다. 새 규칙 1~3개를 만들고, 즉시 승리/패배, 숨은 규칙, 음수 자원, 코드 실행, 반복/재귀는 절대 만들지 마라. 승리조건은 다음 턴 이후 공개되고 최소 3턴 유지한다. " + (repair == null ? "" : "이전 검증 진단: " + string.Join(";", repair));
    var payload = new { model = "gpt-5.6-luna", reasoning = new { effort = "medium" }, input = new[] { new { role = "system", content = new[] { new { type = "input_text", text = prompt } } }, new { role = "user", content = new[] { new { type = "input_text", text = JsonSerializer.Serialize(snapshot) } } } }, text = new { format = new { type = "json_schema", name = "onlymygame_ruleset", strict = false, schema } } };
    using var request = new HttpRequestMessage(HttpMethod.Post, "v1/responses") { Content = JsonContent.Create(payload) }; request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    using var response = await client.SendAsync(request, ct); response.EnsureSuccessStatusCode(); var raw = await response.Content.ReadAsStringAsync(ct);
    using var document = JsonDocument.Parse(raw); var output = document.RootElement.GetProperty("output"); var json = output.EnumerateArray().SelectMany(item => item.GetProperty("content").EnumerateArray()).First(c => c.TryGetProperty("type", out var t) && t.GetString() == "output_text").GetProperty("text").GetString();
    return JsonSerializer.Deserialize<RuleSetV1>(json!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("EMPTY_RULESET");
}
