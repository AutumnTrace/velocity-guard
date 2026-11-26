using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;

namespace VelocityGuard.Tests.Api;

/// <summary>
/// Lists named in configuration follow the same rules as lists in rules.json and the admin API,
/// so a chargeback block or a seed entry can never land in a list nothing else can read.
/// </summary>
public sealed class ConfiguredListsApiTests
{
    [Theory]
    [InlineData("Outcomes:ChargebackBlocklists:Card", "Blocked-Cards", "Outcomes:ChargebackBlocklists: list names")]
    [InlineData("Outcomes:ChargebackBlocklists:Device", "blocked_devices", "Outcomes:ChargebackBlocklists: list names")]
    [InlineData("RiskData:SeedLists:Blocked-Devices:0", "dev-seeded", "RiskData:SeedLists: list names")]
    [InlineData("RiskData:SeedLists:blocked-cards:0", "4242 4242 4242 4242", "RiskData:SeedLists: entries")]
    [InlineData("RiskData:SeedLists:blocked-devices:0", "dev seeded", "RiskData:SeedLists: entries")]
    public void The_service_refuses_to_start_with_a_list_it_could_not_use(string key, string value, string failure)
    {
        using var root = new ApiFactory();
        using var factory = root.WithWebHostBuilder(builder => builder.UseSetting(key, value));

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var validation = Assert.IsType<OptionsValidationException>(ex.GetBaseException(), exactMatch: false);
        Assert.Contains(validation.Failures, f => f.StartsWith(failure, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_valid_custom_chargeback_list_is_accepted()
    {
        using var root = new ApiFactory();
        using var factory = root.WithWebHostBuilder(builder => builder.UseSetting("Outcomes:ChargebackBlocklists:Card", "chargeback-cards"));

        var response = await factory.CreateClient().GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
    }
}
