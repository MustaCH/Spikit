namespace Spikit.Services.Transcription;

// Wrapper inyectable y MUTABLE de la API key. Es singleton en DI: el bootstrap lo
// hidrata desde DPAPI (EP-3.4) y el ProviderConfigWriter lo refresca cuando el usuario
// guarda una nueva config, para que la próxima request al transcriber use la key nueva
// sin reiniciar la app (criterio de aceptación EP-3.4 — "DI: el WhisperApiKey wrapper
// se reconstruye desde DPAPI al iniciar la app y al guardar nueva config").
//
// La key vive solo en memoria — el cifrado lo hace DPAPI a través de DpapiSecretStore.
public sealed class WhisperApiKey
{
    private string _value;

    public WhisperApiKey(string value)
    {
        _value = value ?? string.Empty;
    }

    public string Value => _value;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_value);

    // Llamado por ProviderConfigWriter después de un guardado transaccional exitoso.
    // No emite eventos: el WhisperApiTranscriptionService es transient, así que cada
    // request construye su instancia con la referencia actual del singleton.
    public void Update(string value)
    {
        _value = value ?? string.Empty;
    }
}
