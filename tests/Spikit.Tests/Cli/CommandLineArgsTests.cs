using Spikit.Cli;

namespace Spikit.Tests.Cli;

public class CommandLineArgsTests
{
    [Fact]
    public void Empty_args_yields_all_false_and_no_uri()
    {
        var cli = new CommandLineArgs(Array.Empty<string>());

        Assert.False(cli.DiagnosticsPoc);
        Assert.False(cli.Onboarding);
        Assert.Null(cli.SpikitUri);
    }

    [Theory]
    [InlineData("--diagnostics-poc")]
    [InlineData("--DIAGNOSTICS-POC")]
    public void Sets_DiagnosticsPoc_when_flag_present(string flag)
    {
        var cli = new CommandLineArgs(new[] { flag });

        Assert.True(cli.DiagnosticsPoc);
    }

    [Theory]
    [InlineData("--onboarding")]
    [InlineData("--Onboarding")]
    public void Sets_Onboarding_when_flag_present(string flag)
    {
        var cli = new CommandLineArgs(new[] { flag });

        Assert.True(cli.Onboarding);
    }

    [Fact]
    public void ConsumeOnboarding_flips_flag_to_false()
    {
        // EP-11.7: tras un logout/login round-trip, el flag --onboarding inicial no
        // debe pegarse y re-entrar al wizard. App lo consume tras pasarlo una vez.
        var cli = new CommandLineArgs(new[] { "--onboarding" });
        Assert.True(cli.Onboarding);

        cli.ConsumeOnboarding();

        Assert.False(cli.Onboarding);
    }

    [Fact]
    public void Captures_SpikitUri_when_arg_starts_with_scheme()
    {
        var cli = new CommandLineArgs(new[] { "spikit://auth-callback?access_token=abc&refresh_token=def" });

        Assert.Equal("spikit://auth-callback?access_token=abc&refresh_token=def", cli.SpikitUri);
    }

    [Fact]
    public void Captures_SpikitUri_case_insensitive_scheme()
    {
        var cli = new CommandLineArgs(new[] { "Spikit://billing-return?status=success" });

        Assert.Equal("Spikit://billing-return?status=success", cli.SpikitUri);
    }

    [Fact]
    public void Captures_first_SpikitUri_when_multiple_args()
    {
        var cli = new CommandLineArgs(new[]
        {
            "--onboarding",
            "spikit://auth-callback?token=first",
            "spikit://billing-return?status=success",
        });

        Assert.True(cli.Onboarding);
        Assert.Equal("spikit://auth-callback?token=first", cli.SpikitUri);
    }

    [Fact]
    public void Does_not_match_non_spikit_uri()
    {
        var cli = new CommandLineArgs(new[] { "https://spikit.dev/auth-callback?token=abc" });

        Assert.Null(cli.SpikitUri);
    }

    [Fact]
    public void Ignores_unrelated_args()
    {
        var cli = new CommandLineArgs(new[] { "/some/path", "--unknown", "argy" });

        Assert.False(cli.DiagnosticsPoc);
        Assert.False(cli.Onboarding);
        Assert.Null(cli.SpikitUri);
    }
}
