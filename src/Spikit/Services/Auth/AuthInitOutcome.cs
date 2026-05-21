namespace Spikit.Services.Auth;

// EP-11.8 — clasifica el resultado del último InitializeAsync para que App.xaml.cs
// pueda routear según el tipo de falla (no es lo mismo "expiró" que "sin red").
public enum AuthInitOutcome
{
    // Estado por default antes del primer InitializeAsync.
    NotRun,

    // No había tokens persistidos — primer arranque o post-logout normal.
    NoTokens,

    // Tokens válidos y server confirmó la sesión.
    Success,

    // Tokens persistidos pero el server rechazó (401 sobre access_token o sobre
    // refresh_token). Los tokens locales se borraron. UI: SessionExpired.
    SessionRevoked,

    // No pudimos llegar al server (DNS, timeout, 5xx). Si caímos al modo offline
    // (cache válido), State queda LoggedIn y IsOfflineMode=true. Si no había cache
    // válido, State queda LoggedOut y los tokens se PRESERVAN para reintentar.
    // App.xaml.cs distingue ambos casos via auth.State.
    NetworkFailure,
}
