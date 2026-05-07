namespace Spikit.Services.Provider;

// Prueba la combinación BaseUrl + API key contra el endpoint estándar de Whisper-compatibles
// (`GET {baseUrl}/models`).
//
// Reusable: lo usa el step Provider del onboarding (EP-3.3) y va a usarlo Settings → Provider
// (EP-4) cuando rote credenciales.
//
// El método NUNCA tira; encapsula los errores en ProviderConnectionResult con el mensaje ya
// localizado al español listo para mostrar al usuario.
public interface IProviderConnectionTester
{
    Task<ProviderConnectionResult> TestAsync(string baseUrl, string apiKey, CancellationToken ct = default);
}
