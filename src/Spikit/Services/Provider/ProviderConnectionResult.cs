namespace Spikit.Services.Provider;

// Resultado de IProviderConnectionTester.TestAsync.
// IsOk=true → conexión exitosa. Message es null/vacío.
// IsOk=false → falló. Message tiene el texto que se muestra al usuario en español
// (ya mapeado desde el status code; no es el reason phrase HTTP crudo).
public sealed record ProviderConnectionResult(bool IsOk, string Message)
{
    public static ProviderConnectionResult Ok() => new(true, string.Empty);
    public static ProviderConnectionResult Failed(string message) => new(false, message);
}
