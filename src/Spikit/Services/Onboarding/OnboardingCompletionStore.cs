using Microsoft.Extensions.Logging;
using Spikit.Services.Settings;

namespace Spikit.Services.Onboarding;

// Implementación que delega en ISettingsService — un Load → mutar → Save por operación.
// Sin caching propio: las dos operaciones (IsCompleted, MarkCompleted) ocurren raramente
// (al startup y al final del onboarding), así que no vale la pena cachear el AppSettings.
public sealed class OnboardingCompletionStore : IOnboardingCompletionStore
{
    private readonly ISettingsService _settings;
    private readonly ILogger<OnboardingCompletionStore> _logger;

    public OnboardingCompletionStore(ISettingsService settings, ILogger<OnboardingCompletionStore> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool IsCompleted() => _settings.Load().OnboardingCompleted;

    public void MarkCompleted()
    {
        var current = _settings.Load();
        if (current.OnboardingCompleted)
        {
            _logger.LogDebug("OnboardingCompleted ya estaba en true — no-op");
            return;
        }

        current.OnboardingCompleted = true;
        _settings.Save(current);
        _logger.LogInformation("Onboarding marcado como completado en settings.json");
    }
}
