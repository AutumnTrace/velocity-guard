using Grpc.Core;
using Grpc.Net.Client;
using VelocityGuard.Api.Grpc.V1;

namespace VelocityGuard.Tests.Api;

/// <summary>The gRPC endpoint over the in-memory test server: same engine, same validation, shared velocity.</summary>
public sealed class GrpcApiTests : IClassFixture<ApiFactory>, IDisposable
{
    private readonly ApiFactory _factory;
    private readonly GrpcChannel _channel;
    private readonly RiskService.RiskServiceClient _client;

    public GrpcApiTests(ApiFactory factory)
    {
        _factory = factory;
        _channel = GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = factory.Server.CreateHandler() });
        _client = new RiskService.RiskServiceClient(_channel);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Approves_a_routine_deposit_sent_in_minor_units()
    {
        var response = await _client.AssessAsync(Request("p-grpc-1", amountMinor: 2_500), cancellationToken: Ct);

        Assert.Equal(Decision.Approve, response.Decision);
        Assert.Equal(0, response.Score);
        Assert.Equal(15, response.EvaluatedRules.Count);
        Assert.Null(response.Degraded);
        Assert.Matches("^[0-9a-f]{12}$", response.RulesVersion);
    }

    [Fact]
    public async Task Declines_a_blocklisted_device()
    {
        var request = Request("p-grpc-2");
        request.DeviceId = "dev-known-fraud-01";

        var response = await _client.AssessAsync(request, cancellationToken: Ct);

        Assert.Equal(Decision.Decline, response.Decision);
        Assert.Equal("blocklist", Assert.Single(response.Reasons).Type);
    }

    [Fact]
    public async Task Converts_minor_units_with_the_currency_exponent()
    {
        var request = Request("p-grpc-3", amountMinor: 800_000, currency: "JPY"); // 800,000 JPY: above the 750,000 JPY limit

        var response = await _client.AssessAsync(request, cancellationToken: Ct);

        var reason = Assert.Single(response.Reasons);
        Assert.Equal("large-amount", reason.Rule);
        Assert.Equal("Amount 800,000 JPY is at or above 750,000 JPY", reason.Message);
    }

    [Fact]
    public async Task Invalid_requests_fail_with_invalid_argument_naming_every_field()
    {
        var request = Request("p-grpc-4");
        request.CardFingerprint = "4111111111111111";
        request.ClearAccountAgeDays();

        var ex = await Assert.ThrowsAsync<RpcException>(() => _client.AssessAsync(request, cancellationToken: Ct).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Contains("card.fingerprint", ex.Status.Detail);
        Assert.Contains("accountAgeDays", ex.Status.Detail);
        Assert.DoesNotContain("4111", ex.Status.Detail);
    }

    [Fact]
    public async Task Rest_and_grpc_callers_share_the_same_velocity_windows()
    {
        var http = _factory.CreateClient();

        await http.AssessAsync(ApiClientExtensions.Deposit("p-mixed-1", device: "dev-mixed", fingerprint: "fp_mixed_01"));
        await _client.AssessAsync(Request("p-mixed-2", device: "dev-mixed", fingerprint: "fp_mixed_02"), cancellationToken: Ct);
        await http.AssessAsync(ApiClientExtensions.Deposit("p-mixed-3", device: "dev-mixed", fingerprint: "fp_mixed_03"));
        var fourth = await _client.AssessAsync(Request("p-mixed-4", device: "dev-mixed", fingerprint: "fp_mixed_04"), cancellationToken: Ct);

        Assert.Equal(Decision.Review, fourth.Decision);
        Assert.Equal("device-burst-1m", Assert.Single(fourth.Reasons).Rule);
    }

    public void Dispose() => _channel.Dispose();

    private static AssessRequest Request(
        string player,
        long amountMinor = 2_500,
        string currency = "EUR",
        string device = "dev-grpc",
        string fingerprint = "fp_grpc_0001") => new()
    {
        PlayerId = player,
        AmountMinor = amountMinor,
        Currency = currency,
        Method = PaymentMethod.Card,
        Ip = "203.0.113.10",
        IpCountry = "DE",
        CardBinCountry = "DE",
        CardFingerprint = fingerprint,
        DeviceId = device,
        AccountAgeDays = 420,
    };
}
