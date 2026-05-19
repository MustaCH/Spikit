namespace Spikit.Services.Auth;

// Par access/refresh token devuelto por Supabase Auth (`/auth/v1/token` y al validar
// el callback del deep-link). El `ExpiresAt` se computa al recibir el par desde el
// `expires_in` (segundos) que devuelve Supabase, no se trustea ningún campo del
// query string del deep-link sin revalidar.
public sealed record AccessTokenPair(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt);
