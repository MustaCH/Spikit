namespace Spikit.Services.Auth;

// Estado de alto nivel del IAuthService que la UI consume. Intencionadamente binario
// — los detalles ("tengo tokens pero todavía no validé contra el server") quedan
// internos. El consumer ve solo "logueado" o "no logueado".
public enum AuthSessionState
{
    LoggedOut,
    LoggedIn,
}
