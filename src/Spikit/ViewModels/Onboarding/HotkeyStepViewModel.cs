using Microsoft.Extensions.Logging;
using Spikit.Models;
using Spikit.Native;

namespace Spikit.ViewModels.Onboarding;

// VM del paso 2 del onboarding (Hotkey). Mantiene la combinación capturada por el
// HotkeyCaptureField + el modo (PTT / Toggle).
//
// Validación inline (US-1.2):
//   - Sin combinación capturada            → "Siguiente" deshabilitado.
//   - Sin modificadora (ej. solo F1)       → SOFT WARNING (no bloquea).
//
// El registro real del hotkey en el OS lo hace EP-3.6 (HotkeyService.Register al guardar).
// Acá solo capturamos + validamos; el conflict con otra app aparece recién al registrar.
public sealed class HotkeyStepViewModel : ViewModelBase
{
    private readonly ILogger<HotkeyStepViewModel> _logger;

    private HotkeyDefinition? _hotkey = HotkeyDefinition.Default;
    private HotkeyMode _mode = HotkeyMode.PushToTalk;

    public HotkeyStepViewModel(ILogger<HotkeyStepViewModel> logger)
    {
        _logger = logger;
    }

    // Disparado cada vez que cambia HasHotkey (relevante para CanGoNext del shell).
    // El OnboardingViewModel se suscribe — mismo patrón que ProviderStepViewModel
    // con ConnectionStateChanged.
    public event EventHandler? HotkeyStateChanged;

    // null mientras el usuario no haya capturado ninguna combinación. Default V1
    // (Ctrl+Alt+M) precarga al abrir el step — el ticket pide "default V1 = Ctrl+Alt+M",
    // si el usuario quiere otra apreta el botón de re-capturar.
    public HotkeyDefinition? Hotkey
    {
        get => _hotkey;
        set
        {
            if (SetProperty(ref _hotkey, value))
            {
                NotifyDerivedChanged();
                HotkeyStateChanged?.Invoke(this, EventArgs.Empty);
                _logger.LogDebug("Hotkey actualizada → {Hotkey}", value?.ToString() ?? "(null)");
            }
        }
    }

    public HotkeyMode Mode
    {
        get => _mode;
        set
        {
            if (SetProperty(ref _mode, value))
            {
                OnPropertyChanged(nameof(IsPushToTalk));
                OnPropertyChanged(nameof(IsToggle));
                _logger.LogDebug("Hotkey mode → {Mode}", value);
            }
        }
    }

    // Bindings para los RadioButtons. WPF no tiene un converter directo enum → bool/checked,
    // así que exponemos dos bools two-way que mutan Mode al setearse.
    public bool IsPushToTalk
    {
        get => _mode == HotkeyMode.PushToTalk;
        set { if (value) Mode = HotkeyMode.PushToTalk; }
    }

    public bool IsToggle
    {
        get => _mode == HotkeyMode.Toggle;
        set { if (value) Mode = HotkeyMode.Toggle; }
    }

    // Hay combinación capturada (cualquiera, válida o con warning).
    public bool HasHotkey => _hotkey is not null;

    // Texto rendereado: ej. "Ctrl + Alt + M". Reusa HotkeyDefinition.ToString() pero
    // expande "+" a " + " para que respire en el input.
    public string HotkeyDisplay => _hotkey is null
        ? string.Empty
        : _hotkey.ToString().Replace("+", " + ");

    // Soft warning: combinación sin modificadora. El ticket pide explícitamente que
    // no bloquee el avance — el usuario experto decide si quiere seguir adelante.
    public bool HasWarning => _hotkey is not null && _hotkey.Modifiers == HotkeyModifiers.None;

    public string WarningMessage => HasWarning
        ? "Esta combinación puede entrar en conflicto con otras apps."
        : string.Empty;

    private void NotifyDerivedChanged()
    {
        OnPropertyChanged(nameof(HasHotkey));
        OnPropertyChanged(nameof(HotkeyDisplay));
        OnPropertyChanged(nameof(HasWarning));
        OnPropertyChanged(nameof(WarningMessage));
    }
}
