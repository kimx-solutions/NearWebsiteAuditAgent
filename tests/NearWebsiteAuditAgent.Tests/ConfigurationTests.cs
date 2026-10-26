using NearWebsiteAuditAgent.Core;

namespace NearWebsiteAuditAgent.Tests;

public class ConfigurationTests
{
    [Fact]
    public void A_setting_error_identifies_the_key_without_repeating_its_value()
    {
        var error = Assert.Throws<AuditException>(() => ConfigurationValidation.Validate(new(), new(), new(), new()));
        Assert.Equal("Execution:Mode", error.SettingKey);
    }
    [Fact]
    public void Explicit_local_mode_requires_no_market_token()
        => ConfigurationValidation.Validate(new() { Mode = "Local", InputFile = "input.json" }, new(), new() { Root = ".audit-data" }, new());

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    public void Missing_or_unknown_modes_cannot_start_market_execution(string mode)
        => Assert.Throws<AuditException>(() => ConfigurationValidation.Validate(new() { Mode = mode }, new(), new() { Root = "data" }, new()));

    [Fact]
    public void Invalid_limits_are_rejected_at_startup()
    {
        Assert.Throws<AuditException>(() => ConfigurationValidation.Validate(new() { Mode = "Local", InputFile = "input.json" },
            new() { MaxDurationSeconds = 0 }, new() { Root = "data" }, new()));
        Assert.Throws<AuditException>(() => ConfigurationValidation.Validate(new() { Mode = "Local", InputFile = "input.json" },
            new() { DnsTimeoutSeconds = 20 }, new() { Root = "data" }, new()));
        Assert.Throws<AuditException>(() => ConfigurationValidation.Validate(new() { Mode = "Local", InputFile = "input.json" },
            new() { MaxHtmlPages = 2 }, new() { Root = "data" }, new()));
        Assert.Throws<AuditException>(() => ConfigurationValidation.Validate(new() { Mode = "Local", InputFile = "input.json" },
            new(), new() { Root = "data", RetentionDays = 0 }, new()));
    }

    [Theory]
    [InlineData("--Market:AgentApiToken=aat_VALUE")]
    [InlineData("--Market:AgentApiToken")]
    [InlineData("/Market__AgentApiToken:aat_VALUE")]
    public void CLI_arguments_cannot_carry_agent_tokens(string argument)
        => Assert.Throws<AuditException>(() => ConfigurationValidation.ValidateArguments([argument]));

    [Fact]
    public void Ordinary_configuration_arguments_remain_usable()
        => ConfigurationValidation.ValidateArguments(["--Execution:Mode", "Local", "--Storage:Root", "data"]);
}
