using Microsoft.Extensions.Logging;

namespace Spikit.ViewModels.Onboarding;

// VM del paso 3 del onboarding (Prueba). Mantiene el texto de la TextBox (two-way bound)
// y dispara TextStateChanged cuando HasText cambia para que el OnboardingViewModel pueda
// gatear "Finalizar".
//
// El flow real de dictado lo maneja el DictationOrchestrator: el clipboard paste cae en
// la TextBox que tiene foco, y el binding con UpdateSourceTrigger=PropertyChanged refresca
// _text. Este VM no se acopla al orchestrator — se mantiene puramente como estado del form.
public sealed class PruebaStepViewModel : ViewModelBase
{
    private readonly ILogger<PruebaStepViewModel> _logger;
    private string _text = string.Empty;

    public PruebaStepViewModel(ILogger<PruebaStepViewModel> logger)
    {
        _logger = logger;
    }

    // Disparado cuando HasText cambia (lo que afecta CanGoNext del shell).
    public event EventHandler? TextStateChanged;

    public string Text
    {
        get => _text;
        set
        {
            var hadText = HasText;
            if (SetProperty(ref _text, value))
            {
                if (hadText != HasText)
                {
                    OnPropertyChanged(nameof(HasText));
                    TextStateChanged?.Invoke(this, EventArgs.Empty);
                    _logger.LogDebug("Test step text state → HasText={HasText}", HasText);
                }
            }
        }
    }

    // True si el usuario tipeó algo o si el dictado real insertó texto. Cualquier whitespace
    // puro NO cuenta — Whisper devuelve " " si no detecta audio (CB-8) y no queremos habilitar
    // Finalizar en ese caso.
    public bool HasText => !string.IsNullOrWhiteSpace(_text);
}
