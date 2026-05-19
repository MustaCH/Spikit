namespace Spikit.Services.Auth;

// Cache local del Entitlement con TTL (default 24h, ADR-0007 § 8). Permite que la UI
// muestre el tier sin pegarle al backend en cada apertura de Settings, y que la app
// sobreviva blips transitorios de Supabase (los users BYOK siguen funcionando porque
// no necesitan backend en runtime del dictado).
//
// Authorization REAL siempre la valida el server: el Edge Function `transcribe`
// reconfirma el tier en cada llamada. Este cache es solo para UI.
public interface IEntitlementCache
{
    // Devuelve la entrada cacheada si existe y todavía no expiró. Si está vencida,
    // devuelve null igual (el caller debería refrescar). No borra automáticamente
    // las entradas vencidas — sigue habiendo info útil ahí (último tier conocido)
    // para casos de degradación offline; ese uso se construye en V2.
    Entitlement? ReadFresh();

    // Devuelve la última entrada cacheada sin chequear TTL. Útil para mostrar UI
    // "última vez que sabíamos que tenías Pro" cuando el backend está caído.
    Entitlement? ReadStale();

    // Sobrescribe el cache con el snapshot fresco + timestamp `now`.
    void Write(Entitlement entitlement);

    // Idempotente. Se invoca en Logout para que el próximo login arranque sin estado
    // del usuario anterior.
    void Clear();
}
