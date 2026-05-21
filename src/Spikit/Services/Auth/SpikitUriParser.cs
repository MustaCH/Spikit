namespace Spikit.Services.Auth;

// Tipos de URI `spikit://...` que la app reconoce. Cualquier otra cosa cae en
// Unknown y se ignora silenciosamente (un attacker no puede forzar lógica con un
// URI inventado — solo dispara el flow de validación correspondiente, que va al
// backend a confirmar).
public enum SpikitUriKind
{
    Unknown,
    AuthCallback,
    BillingReturn,

    // Emitido por la página `spikit.dev/auth` cuando el `signInWithOtp` responde 200
    // (cierre Q-9 de ADR-0008 follow-up). Lleva sólo el email destino del magic link
    // (URL-encoded en `email`), sin tokens. Lo consume el LoginWindow para mutar al
    // estado 0.2 `waiting_for_magic_link` mostrando el email exacto.
    AuthPending,
}

// Resultado de parsear un URI `spikit://...`. Los parámetros vienen normalizados:
// case-insensitive keys, valores URL-decoded.
public sealed record ParsedSpikitUri(
    SpikitUriKind Kind,
    IReadOnlyDictionary<string, string> Params);

// Parser puro de URIs `spikit://...` que recibe la app por argv[1] cuando el
// protocol handler de Windows la abre. Detalle del flow en ADR-0007 § 3.
//
// Soporta query string (`?k=v&...`) y fragment (`#k=v&...`) porque Supabase Auth
// históricamente pone los tokens en el fragment (default) pero con redirectTo
// custom-scheme algunas versiones los ponen en query — la página intermediaria de
// spikit.dev/auth-callback fuerza query, pero el parser tolera ambos.
public static class SpikitUriParser
{
    public const string Scheme = "spikit";

    private const string AuthCallbackHost = "auth-callback";
    private const string AuthPendingHost = "auth-pending";
    private const string BillingReturnHost = "billing-return";

    public static ParsedSpikitUri? TryParse(string? rawUri)
    {
        if (string.IsNullOrWhiteSpace(rawUri)) return null;
        if (!Uri.TryCreate(rawUri, UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)) return null;

        var kind = uri.Host.ToLowerInvariant() switch
        {
            AuthCallbackHost => SpikitUriKind.AuthCallback,
            AuthPendingHost => SpikitUriKind.AuthPending,
            BillingReturnHost => SpikitUriKind.BillingReturn,
            _ => SpikitUriKind.Unknown,
        };

        var rawParams = !string.IsNullOrEmpty(uri.Query)
            ? uri.Query.TrimStart('?')
            : uri.Fragment.TrimStart('#');

        return new ParsedSpikitUri(kind, ParseParams(rawParams));
    }

    private static IReadOnlyDictionary<string, string> ParseParams(string raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(raw)) return result;

        foreach (var pair in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx < 0)
            {
                // Param sin `=` (ej. `?foo`) → lo registramos con value vacío.
                result[Uri.UnescapeDataString(pair)] = string.Empty;
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..idx]);
            var value = Uri.UnescapeDataString(pair[(idx + 1)..]);
            result[key] = value;
        }

        return result;
    }
}
