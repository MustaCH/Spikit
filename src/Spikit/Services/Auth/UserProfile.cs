namespace Spikit.Services.Auth;

// Identidad mínima del usuario logueado, devuelta por `GET /auth/v1/user`.
// Se usa para mostrar el email en Settings → Account y como user_id de telemetría.
public sealed record UserProfile(string Id, string Email);
