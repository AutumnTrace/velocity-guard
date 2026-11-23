using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace VelocityGuard.Tests.Api;

/// <summary>End-to-end over HTTP against the shipped <c>rules.json</c>.</summary>
public sealed class AssessmentApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Approves_a_routine_deposit()
    {
        var result = await _client.AssessAsync(ApiClientExtensions.Deposit("p-api-approve", attemptId: "att_api_approve"));

        Assert.Equal("approve", result.Decision());
        Assert.Equal(0, result.GetProperty("score").GetInt32());
        Assert.Equal("att_api_approve", result.GetProperty("attemptId").GetString());
        Assert.Equal(15, result.GetProperty("evaluatedRules").GetArrayLength());
        Assert.False(result.TryGetProperty("degraded", out _));
        Assert.Matches("^[0-9a-f]{12}$", result.GetProperty("rulesVersion").GetString());
    }

    [Fact]
    public async Task Sends_a_large_deposit_from_a_new_account_with_a_foreign_card_to_review()
    {
        var request = ApiClientExtensions.Deposit("p-api-review", amount: 600m, binCountry: "BR", accountAgeDays: 2, device: "dev-review", fingerprint: "fp_review_01");

        var result = await _client.AssessAsync(request);

        Assert.Equal("review", result.Decision());
        Assert.Equal(55, result.GetProperty("score").GetInt32());
        Assert.Equal(["new-account-large-deposit", "bin-ip-country-mismatch"], result.ReasonRules());
        Assert.Equal("Card issued in BR, IP located in DE", result.GetProperty("reasons")[1].GetProperty("message").GetString());
    }

    [Fact]
    public async Task Declines_a_device_on_the_blocklist()
    {
        var result = await _client.AssessAsync(ApiClientExtensions.Deposit("p-api-decline", device: "dev-known-fraud-01", fingerprint: "fp_decline_01"));

        Assert.Equal("decline", result.Decision());
        var reason = Assert.Single(result.GetProperty("reasons").EnumerateArray());
        Assert.Equal("blocked-device", reason.GetProperty("rule").GetString());
        Assert.Equal("decline", reason.GetProperty("action").GetString());
    }

    [Fact]
    public async Task A_burst_from_one_device_trips_the_card_testing_rule()
    {
        JsonElement last = default;
        for (var i = 0; i < 4; i++)
        {
            factory.Time.Advance(TimeSpan.FromSeconds(3));
            last = await _client.AssessAsync(ApiClientExtensions.Deposit($"p-api-burst-{i}", device: "dev-api-burst", fingerprint: $"fp_burst_{i:00}"));
        }

        Assert.Equal("review", last.Decision());
        Assert.Equal("4 attempts from this device in 1m (max 3)", last.GetProperty("reasons")[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task Invalid_input_gets_rfc9457_problem_details_with_every_error()
    {
        var response = await _client.PostAsJsonAsync("/v1/assessments", new
        {
            playerId = "p-1",
            amount = 10.005m,
            currency = "EUR",
            method = "card",
            ip = "10.1",
            ipCountry = "DE",
            card = new { binCountry = "DE", fingerprint = "4111 1111 1111 1111" },
            accountAgeDays = 3,
        }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var errors = problem.GetProperty("errors");
        Assert.True(errors.TryGetProperty("amount", out _));
        Assert.True(errors.TryGetProperty("ip", out _));
        Assert.True(errors.TryGetProperty("card.fingerprint", out _));
        Assert.True(problem.TryGetProperty("traceId", out _));
        Assert.DoesNotContain("4111", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Malformed_json_is_a_400_problem_not_a_500()
    {
        var response = await _client.PostAsync("/v1/assessments", new StringContent("{ \"playerId\": ", Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Lists_the_active_compiled_rules()
    {
        var rules = await _client.GetFromJsonAsync<JsonElement>("/v1/rules", Ct);

        Assert.Equal(40, rules.GetProperty("decision").GetProperty("reviewAt").GetInt32());
        var all = rules.GetProperty("rules").EnumerateArray().ToList();
        Assert.Equal(15, all.Count);
        var sum = all.Single(r => r.GetProperty("id").GetString() == "player-deposits-24h");
        Assert.Equal("velocitySum", sum.GetProperty("type").GetString());
        Assert.Equal("24h", sum.GetProperty("window").GetString());
        Assert.Equal(2000m, sum.GetProperty("limits").GetProperty("EUR").GetDecimal());
        Assert.Equal("shadow", all.Single(r => r.GetProperty("id").GetString() == "card-deposits-7d").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task A_chargeback_blocks_the_device_for_the_next_attempt()
    {
        await _client.AssessAsync(ApiClientExtensions.Deposit("p-api-cb", device: "dev-api-cb", fingerprint: "fp_api_cb_01", attemptId: "att_api_cb"));
        (await _client.PostAsJsonAsync("/v1/assessments/att_api_cb/outcome", new { outcome = "approved" }, Ct)).EnsureSuccessStatusCode();

        var response = await _client.PostAsJsonAsync("/v1/assessments/att_api_cb/outcome", new { outcome = "chargeback" }, Ct);
        var next = await _client.AssessAsync(ApiClientExtensions.Deposit("p-api-cb-2", device: "dev-api-cb", fingerprint: "fp_api_cb_02"));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("recorded", body.GetProperty("status").GetString());
        Assert.Equal("approved", body.GetProperty("previous").GetString());
        Assert.Equal(1, body.GetProperty("profile").GetProperty("chargebacks").GetInt32());
        Assert.Equal("decline", next.Decision());
    }

    [Theory]
    [InlineData("att_never_seen", "approved", HttpStatusCode.NotFound)]
    [InlineData("att_api_conflict", "chargeback", HttpStatusCode.Conflict)]
    [InlineData("att_api_conflict", "refunded", HttpStatusCode.BadRequest)]
    public async Task Outcome_errors_are_problem_details(string attemptId, string outcome, HttpStatusCode expected)
    {
        await _client.AssessAsync(ApiClientExtensions.Deposit("p-api-conflict", attemptId: "att_api_conflict", device: "dev-api-conflict"));

        var response = await _client.PostAsJsonAsync($"/v1/assessments/{attemptId}/outcome", new { outcome }, Ct);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Health_probes_answer()
    {
        var live = await _client.GetAsync("/health/live", Ct);
        var ready = await _client.GetFromJsonAsync<JsonElement>("/health/ready", Ct);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("Healthy", ready.GetProperty("status").GetString());
        Assert.Equal("rules", ready.GetProperty("checks")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Serves_an_openapi_document()
    {
        var document = await _client.GetFromJsonAsync<JsonElement>("/openapi/v1.json", Ct);

        Assert.True(document.GetProperty("paths").TryGetProperty("/v1/assessments", out _));
    }
}
