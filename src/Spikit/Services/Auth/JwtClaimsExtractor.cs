using System.Text;
using System.Text.Json;

namespace Spikit.Services.Auth;

// EP-11.8 — decodifica el payload de un JWT Supabase para extraer las claims que
// la app desktop necesita en modo offline (sub + email → UserProfile).
//
// NO valida la firma — la verificación criptográfica corresponde al server cuando
// hay red. Confiamos en el JWT porque vino de la persistencia DPAPI (que solo nosotros
// escribimos vía SetLoggedIn post-validación server-side). Si por algún motivo el
// payload fuese tampered (DPAPI roto, copia entre máquinas), el primer round-trip
// online lo va a rechazar.
//
// Formato JWT: header.payload.signature — todos base64url. Nos interesa solo el
// payload (segundo segmento), que es JSON con sub/email/exp/etc.
internal static class JwtClaimsExtractor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Devuelve null si el token no es parseable (3 segmentos, base64url válido,
    // JSON deserializable con claims sub + email). El caller cae al flow normal
    // (no offline mode) en ese caso.
    public static UserProfile? TryExtractProfile(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt)) return null;

        var segments = jwt.Split('.');
        if (segments.Length != 3) return null;

        byte[] payloadBytes;
        try
        {
            payloadBytes = Base64UrlDecode(segments[1]);
        }
        catch (FormatException)
        {
            return null;
        }

        JwtPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<JwtPayload>(payloadBytes, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload?.Sub is null || string.IsNullOrWhiteSpace(payload.Email))
        {
            return null;
        }

        return new UserProfile(payload.Sub, payload.Email);
    }

    // Base64url (RFC 4648 §5): like base64 pero - y _ en lugar de + y /, sin padding.
    // System.Convert.FromBase64String exige padding y los chars estándares; recomponemos
    // a base64 estándar antes de decodificar.
    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: throw new FormatException("Largo de base64url inválido");
        }
        return Convert.FromBase64String(s);
    }

    // Subset del payload del JWT que nos interesa. Otros claims (exp, iat, aud, etc.)
    // se ignoran — `exp` ya lo trackea el AccessTokenPair localmente y los demás no
    // los consumimos client-side.
    private sealed record JwtPayload(string? Sub, string? Email);
}
