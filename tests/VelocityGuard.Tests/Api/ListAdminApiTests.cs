using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace VelocityGuard.Tests.Api;

public sealed class ListAdminApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Admin_endpoints_need_the_admin_key()
    {
        var anonymous = await _client.GetAsync("/v1/lists/blocked-devices", Ct);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/lists/blocked-devices");
        request.Headers.Add("X-Admin-Key", "wrong-key");
        var wrong = await _client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    [Fact]
    public async Task Seeded_entries_are_listed()
    {
        var list = await Admin(HttpMethod.Get, "/v1/lists/blocked-devices");

        Assert.Contains("dev-known-fraud-01", list.GetProperty("entries").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Adding_an_entry_takes_effect_on_the_next_assessment()
    {
        var before = await _client.AssessAsync(ApiClientExtensions.Deposit("p-admin-1", device: "dev-admin-add", fingerprint: "fp_admin_01"));

        var added = await Admin(HttpMethod.Put, "/v1/lists/blocked-devices/entries/dev-admin-add");
        var after = await _client.AssessAsync(ApiClientExtensions.Deposit("p-admin-1", device: "dev-admin-add", fingerprint: "fp_admin_02"));
        await Admin(HttpMethod.Delete, "/v1/lists/blocked-devices/entries/dev-admin-add");
        var removed = await _client.AssessAsync(ApiClientExtensions.Deposit("p-admin-1", device: "dev-admin-add", fingerprint: "fp_admin_03"));

        Assert.Equal("approve", before.Decision());
        Assert.True(added.GetProperty("changed").GetBoolean());
        Assert.Equal("decline", after.Decision());
        Assert.NotEqual("decline", removed.Decision());
    }

    [Fact]
    public async Task Replacing_a_list_invalidates_cached_answers()
    {
        var before = await _client.AssessAsync(ApiClientExtensions.Deposit("p-admin-trusted", amount: 600m, binCountry: "BR", accountAgeDays: 2, device: "dev-admin-t1", fingerprint: "fp_admin_t1"));

        var replaced = await Admin(HttpMethod.Put, "/v1/lists/trusted-players", new { entries = new[] { "p-admin-trusted", "p-vip-0001" } });
        var after = await _client.AssessAsync(ApiClientExtensions.Deposit("p-admin-trusted", amount: 600m, binCountry: "BR", accountAgeDays: 2, device: "dev-admin-t2", fingerprint: "fp_admin_t2"));

        Assert.Equal(2, replaced.GetProperty("count").GetInt32());
        Assert.Equal(55, before.GetProperty("score").GetInt32());
        Assert.Equal(25, after.GetProperty("score").GetInt32()); // 55 - 30 for the allowlist
        Assert.Contains("trusted-player", after.ReasonRules());
    }

    [Theory]
    [InlineData("/v1/lists/Blocked_Devices")]
    [InlineData("/v1/lists/blocked-cards/entries/4111111111111111")]
    public async Task Rejects_bad_list_names_and_card_numbers(string path)
    {
        using var request = new HttpRequestMessage(path.Contains("/entries/") ? HttpMethod.Put : HttpMethod.Get, path);
        request.Headers.Add("X-Admin-Key", ApiFactory.AdminKey);

        var response = await _client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<JsonElement> Admin(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Admin-Key", ApiFactory.AdminKey);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await _client.SendAsync(request, Ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }
}
