using Spikit.Services.Auth;

namespace Spikit.Tests.Services.Auth;

public class SpikitUriParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_returns_null_for_empty(string? input)
    {
        Assert.Null(SpikitUriParser.TryParse(input));
    }

    [Theory]
    [InlineData("https://spikit.dev/auth-callback?foo=bar")]
    [InlineData("not-a-uri")]
    [InlineData("file:///c:/tmp/x.txt")]
    public void TryParse_returns_null_for_non_spikit_scheme(string input)
    {
        Assert.Null(SpikitUriParser.TryParse(input));
    }

    [Fact]
    public void TryParse_recognizes_auth_callback_with_query_params()
    {
        var result = SpikitUriParser.TryParse(
            "spikit://auth-callback?access_token=abc&refresh_token=def&expires_in=3600");

        Assert.NotNull(result);
        Assert.Equal(SpikitUriKind.AuthCallback, result!.Kind);
        Assert.Equal("abc", result.Params["access_token"]);
        Assert.Equal("def", result.Params["refresh_token"]);
        Assert.Equal("3600", result.Params["expires_in"]);
    }

    [Fact]
    public void TryParse_recognizes_billing_return_with_status()
    {
        var result = SpikitUriParser.TryParse("spikit://billing-return?status=success");

        Assert.NotNull(result);
        Assert.Equal(SpikitUriKind.BillingReturn, result!.Kind);
        Assert.Equal("success", result.Params["status"]);
    }

    [Fact]
    public void TryParse_falls_back_to_fragment_when_query_is_empty()
    {
        // Supabase default — tokens en fragment.
        var result = SpikitUriParser.TryParse("spikit://auth-callback#access_token=xyz&refresh_token=qrs");

        Assert.NotNull(result);
        Assert.Equal(SpikitUriKind.AuthCallback, result!.Kind);
        Assert.Equal("xyz", result.Params["access_token"]);
        Assert.Equal("qrs", result.Params["refresh_token"]);
    }

    [Fact]
    public void TryParse_url_decodes_values()
    {
        var result = SpikitUriParser.TryParse(
            "spikit://auth-callback?error_description=Magic%20link%20expired");

        Assert.NotNull(result);
        Assert.Equal("Magic link expired", result!.Params["error_description"]);
    }

    [Fact]
    public void TryParse_keys_are_case_insensitive()
    {
        var result = SpikitUriParser.TryParse("spikit://auth-callback?Access_Token=abc");

        Assert.NotNull(result);
        Assert.Equal("abc", result!.Params["access_token"]);
    }

    [Fact]
    public void TryParse_unknown_host_classifies_as_Unknown()
    {
        var result = SpikitUriParser.TryParse("spikit://random-host?x=1");

        Assert.NotNull(result);
        Assert.Equal(SpikitUriKind.Unknown, result!.Kind);
    }

    [Fact]
    public void TryParse_param_without_equals_kept_with_empty_value()
    {
        var result = SpikitUriParser.TryParse("spikit://auth-callback?solo&other=ok");

        Assert.NotNull(result);
        Assert.Equal(string.Empty, result!.Params["solo"]);
        Assert.Equal("ok", result.Params["other"]);
    }

    [Fact]
    public void TryParse_empty_query_returns_empty_params_dict()
    {
        var result = SpikitUriParser.TryParse("spikit://auth-callback");

        Assert.NotNull(result);
        Assert.Equal(SpikitUriKind.AuthCallback, result!.Kind);
        Assert.Empty(result.Params);
    }

    // ====== auth-pending (EP-11.3, cierre Q-9 de ADR-0008) ======

    [Fact]
    public void TryParse_recognizes_auth_pending_with_email_param()
    {
        var result = SpikitUriParser.TryParse(
            "spikit://auth-pending?email=nacho%40spikit.dev");

        Assert.NotNull(result);
        Assert.Equal(SpikitUriKind.AuthPending, result!.Kind);
        Assert.Equal("nacho@spikit.dev", result.Params["email"]);
    }

    [Fact]
    public void TryParse_auth_pending_url_decodes_email_with_special_chars()
    {
        var result = SpikitUriParser.TryParse(
            "spikit://auth-pending?email=user%2Btag%40example.com");

        Assert.NotNull(result);
        Assert.Equal(SpikitUriKind.AuthPending, result!.Kind);
        Assert.Equal("user+tag@example.com", result.Params["email"]);
    }

    [Fact]
    public void TryParse_auth_pending_host_case_insensitive()
    {
        var result = SpikitUriParser.TryParse(
            "spikit://AUTH-PENDING?email=a%40b.co");

        Assert.NotNull(result);
        Assert.Equal(SpikitUriKind.AuthPending, result!.Kind);
    }
}
