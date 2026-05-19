namespace Spikit.Services.Auth;

// Persistencia del par access/refresh token del usuario logueado. RN-3 obliga DPAPI;
// la impl productiva delega en ISecretStore. Stateful: hay 1 sesión a la vez por user
// en una máquina (multi-device se resuelve server-side, no acá).
public interface IAuthTokenStore
{
    // null si no hay sesión guardada o si el archivo no se pudo descifrar
    // (CB-14: usuario de Windows distinto del que cifró). El caller trata ambos casos
    // como "logged out" y dispara re-login.
    AccessTokenPair? Read();

    // Sobrescribe. Atomic a nivel filesystem (write tmp + move) por la impl de ISecretStore.
    void Write(AccessTokenPair pair);

    // Idempotente. Llamado en Logout y al detectar refresh token vencido / inválido.
    void Clear();
}
