namespace Spikit.Models;

// Estado del feedback ✓/✗ del botón "Probar conexión" (US-1.1 AC 8-10).
//
//   Idle    → todavía no se probó (o se invalidó por edición).
//   Testing → request en vuelo. UI muestra spinner, inputs y botón disabled.
//   Ok      → última prueba devolvió 2xx. UI muestra ✓ verde + timestamp.
//   Error   → última prueba falló. UI muestra ✗ rojo + mensaje específico.
public enum ProviderConnectionStatus
{
    Idle = 0,
    Testing = 1,
    Ok = 2,
    Error = 3,
}
