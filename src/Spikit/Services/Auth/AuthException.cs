namespace Spikit.Services.Auth;

// Excepción base para todo lo que falla en el flow de auth. Los call sites la atrapan
// para distinguir "error técnico" (red, parsing, server) de "no estás logueado" (que
// no es una excepción sino el estado normal del IAuthService).
public class AuthException : Exception
{
    public AuthException(string message) : base(message) { }
    public AuthException(string message, Exception inner) : base(message, inner) { }
}

// El access_token actual fue rechazado por el server (401). Distinguido de los demás
// para que el caller intente refresh antes de declarar logout.
public sealed class AuthTokenInvalidException : AuthException
{
    public AuthTokenInvalidException(string message) : base(message) { }
}

// El refresh_token también falló (típicamente expirado o revocado). Forza re-login.
public sealed class AuthRefreshFailedException : AuthException
{
    public AuthRefreshFailedException(string message) : base(message) { }
    public AuthRefreshFailedException(string message, Exception inner) : base(message, inner) { }
}
