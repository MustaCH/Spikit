using System.Text.RegularExpressions;

namespace Spikit.ViewModels.Auth;

// Validador minimalista del formato de email. La validación "de verdad" (que el
// inbox exista, que reciba mail, etc.) la hace Supabase Auth cuando emite el magic
// link — acá sólo nos interesa **rechazar deep-links plantados con basura** que
// llegan en `spikit://auth-pending?email=...`. Si el email del query string no
// matchea siquiera la forma `algo@algo.algo`, ignoramos el deep-link y el LoginWindow
// queda en su estado anterior (típicamente Idle).
//
// Heurística: regex compacta tipo "HTML5 input" (RFC 5322 completo es overkill y
// rechaza emails legales raros — la peor falla acá sería false negative, no security
// hole). Validamos: parte local 1+ chars, exactamente una `@`, dominio con al menos
// un punto, TLD 2+ chars.
internal static class AuthEmail
{
    // RegEx de la spec HTML5 (https://html.spec.whatwg.org/#valid-e-mail-address)
    // con TLD ≥2 chars para descartar emails de prueba malformados (foo@bar.x).
    private static readonly Regex Pattern = new(
        @"^[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*\.[a-zA-Z]{2,}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static bool IsValid(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        if (email.Length > 254) return false;   // RFC 5321 total length cap
        try
        {
            return Pattern.IsMatch(email);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
