namespace Spikit.Services.Provider;

// Excepción unificada que tira ProviderConfigWriter cuando DPAPI o JsonSettings rechazan.
// Lleva un mensaje listo para mostrar inline al usuario en el form de onboarding.
public sealed class ProviderConfigSaveException : Exception
{
    public ProviderConfigSaveException(string message) : base(message) { }
    public ProviderConfigSaveException(string message, Exception inner) : base(message, inner) { }
}
