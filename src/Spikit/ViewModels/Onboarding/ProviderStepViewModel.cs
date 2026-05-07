using Microsoft.Extensions.Logging;
using Spikit.Services.Provider;
using Spikit.ViewModels.Provider;

namespace Spikit.ViewModels.Onboarding;

// VM del paso 1 del onboarding (Provider). Extiende ProviderFormViewModelBase, que tiene
// toda la lógica compartida con el SectionVM de Settings (EP-4.3): presets canónicos,
// validación de la API key, comando "Probar conexión", demote-a-Custom al editar manual,
// SaveAsync transaccional vía IProviderConfigWriter.
//
// Lo único específico del wizard que vive acá:
//   - `ConnectionStateChanged` event para que el OnboardingViewModel recompute CanGoNext
//     del shell (no podemos depender solo de PropertyChanged porque el shell quiere un
//     trigger explícito y agregado).
//
// Comportamiento de SaveAsync se hereda directo: el shell lo invoca al avanzar de paso.
public sealed class ProviderStepViewModel : ProviderFormViewModelBase
{
    public ProviderStepViewModel(
        ILogger<ProviderStepViewModel> logger,
        IProviderConnectionTester connectionTester,
        IProviderConfigWriter configWriter)
        : base(logger, connectionTester, configWriter)
    {
    }

    // Disparado cada vez que ConnectionStatus cambia. El OnboardingViewModel se suscribe
    // para recomputar CanGoNext del shell — los DataTriggers genéricos no alcanzaban porque
    // hay varias propiedades cambiando juntas y queríamos un trigger explícito.
    public event EventHandler? ConnectionStateChanged;

    protected override void OnConnectionStatusChanged()
    {
        ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
    }
}
