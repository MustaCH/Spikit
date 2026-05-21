using System.Text;
using Spikit.Services.Auth;

namespace Spikit.Tests.Services.Auth;

// EP-11.8 — tests del JWT claims extractor. Cubre el happy path (Supabase JWT real
// con sub + email) y los casos de degradación que el fallback offline tiene que
// rechazar (segmento faltante, base64 inválido, JSON inválido, claims vacías).
public class JwtClaimsExtractorTests
{
    [Fact]
    public void Extracts_sub_and_email_from_valid_jwt()
    {
        var jwt = BuildJwt(payloadJson: """{"sub":"user-42","email":"nacho@spikit.dev"}""");

        var profile = JwtClaimsExtractor.TryExtractProfile(jwt);

        Assert.NotNull(profile);
        Assert.Equal("user-42", profile!.Id);
        Assert.Equal("nacho@spikit.dev", profile.Email);
    }

    [Fact]
    public void Ignores_extra_claims()
    {
        // Supabase JWT real trae mil claims (iat, exp, aud, role, app_metadata,
        // user_metadata, etc.). El extractor debe ignorarlas sin fallar.
        var jwt = BuildJwt(payloadJson: """
        {
            "sub": "user-99",
            "email": "test@spikit.dev",
            "exp": 1779292000,
            "iat": 1779288400,
            "aud": "authenticated",
            "role": "authenticated",
            "app_metadata": {"provider": "email"}
        }
        """);

        var profile = JwtClaimsExtractor.TryExtractProfile(jwt);

        Assert.NotNull(profile);
        Assert.Equal("user-99", profile!.Id);
        Assert.Equal("test@spikit.dev", profile.Email);
    }

    [Fact]
    public void Returns_null_for_null_or_empty()
    {
        Assert.Null(JwtClaimsExtractor.TryExtractProfile(null));
        Assert.Null(JwtClaimsExtractor.TryExtractProfile(""));
        Assert.Null(JwtClaimsExtractor.TryExtractProfile("   "));
    }

    [Fact]
    public void Returns_null_when_not_three_segments()
    {
        Assert.Null(JwtClaimsExtractor.TryExtractProfile("only-one-segment"));
        Assert.Null(JwtClaimsExtractor.TryExtractProfile("two.segments"));
        Assert.Null(JwtClaimsExtractor.TryExtractProfile("a.b.c.d"));
    }

    [Fact]
    public void Returns_null_when_payload_is_invalid_base64()
    {
        Assert.Null(JwtClaimsExtractor.TryExtractProfile("header.!!!not-base64!!!.sig"));
    }

    [Fact]
    public void Returns_null_when_payload_is_not_json()
    {
        var notJson = Base64Url("just a string");
        Assert.Null(JwtClaimsExtractor.TryExtractProfile($"header.{notJson}.sig"));
    }

    [Fact]
    public void Returns_null_when_sub_is_missing()
    {
        var jwt = BuildJwt(payloadJson: """{"email":"x@y.com"}""");
        Assert.Null(JwtClaimsExtractor.TryExtractProfile(jwt));
    }

    [Fact]
    public void Returns_null_when_email_is_missing()
    {
        var jwt = BuildJwt(payloadJson: """{"sub":"u1"}""");
        Assert.Null(JwtClaimsExtractor.TryExtractProfile(jwt));
    }

    [Fact]
    public void Returns_null_when_email_is_empty_string()
    {
        var jwt = BuildJwt(payloadJson: """{"sub":"u1","email":""}""");
        Assert.Null(JwtClaimsExtractor.TryExtractProfile(jwt));
    }

    [Fact]
    public void Handles_base64url_chars_without_padding()
    {
        // Payload con caracteres que producen base64url con `-` / `_` y sin padding —
        // formato exacto del Supabase JWT (RFC 4648 §5). El extractor debe decodificar.
        // Para forzar `-`/`_` con alta probabilidad, sumamos un email con caracteres
        // que en base64 producen esos chars (los `/` y `+` del alfabeto base64 son
        // `_` y `-` en base64url).
        var jwt = BuildJwt(payloadJson: """{"sub":"u-99","email":"a+b@spikit.dev"}""");

        var profile = JwtClaimsExtractor.TryExtractProfile(jwt);

        Assert.NotNull(profile);
        Assert.Equal("a+b@spikit.dev", profile!.Email);
    }

    // Helpers ────────────────────────────────────────────────────────────────────

    private static string BuildJwt(string payloadJson)
    {
        var header = Base64Url("""{"alg":"HS256","typ":"JWT"}""");
        var payload = Base64Url(payloadJson);
        return $"{header}.{payload}.fake-signature";
    }

    private static string Base64Url(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
