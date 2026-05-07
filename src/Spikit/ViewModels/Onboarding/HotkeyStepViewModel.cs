using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Spikit.Models;
using Spikit.Native;
using Spikit.Services.Hotkey;

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
    private readonly IHotkeyConfigWriter _configWriter;

    private HotkeyDefinition? _hotkey = HotkeyDefinition.Default;
    private HotkeyMode _mode = HotkeyMode.PushToTalk;

    private bool _isSaving;
    private string _saveError = string.Empty;

    public HotkeyStepViewModel(
        ILogger<HotkeyStepViewModel> logger,
        IHotkeyConfigWriter configWriter)
    {
        _logger = logger;
        _configWriter = configWriter;
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
                // Cualquier captura nueva limpia un SaveError previo (CB-7 o disco): el
                // usuario probó algo distinto, dale chance de avanzar.
                if (!string.IsNullOrEmpty(_saveError)) SaveError = string.Empty;
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

    // Estado del guardado transaccional (EP-3.6). El OnboardingViewModel los lee para
    // gatear el avance al paso 3 y la UI muestra inline error si HasSaveError.
    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (SetProperty(ref _isSaving, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string SaveError
    {
        get => _saveError;
        private set
        {
            if (SetProperty(ref _saveError, value))
            {
                OnPropertyChanged(nameof(HasSaveError));
            }
        }
    }

    public bool HasSaveError => !string.IsNullOrEmpty(_saveError);

    // Persiste vía IHotkeyConfigWriter. Devuelve true si OK; false con SaveError seteado
    // si CB-7 (combinación tomada) o JsonSettings rechaza. La OnboardingViewModel lo
    // invoca al transicionar fuera del step Hotkey.
    public async Task<bool> SaveAsync(CancellationToken ct = default)
    {
        if (_hotkey is null)
        {
            SaveError = "Capturá una combinación antes de avanzar.";
            return false;
        }

        SaveError = string.Empty;
        IsSaving = true;
        try
        {
            await _configWriter.SaveAsync(_hotkey, _mode, ct).ConfigureAwait(true);
            _logger.LogInformation("Hotkey config persistida desde el step VM ({Hotkey} / {Mode})", _hotkey, _mode);
            return true;
        }
        catch (HotkeyRegistrationException ex)
        {
            // CB-7: la combinación está tomada por el sistema o por otra app. Mensaje literal
            // del ticket — el usuario tiene que probar otra antes de avanzar.
            _logger.LogWarning(ex, "CB-7: hotkey {Hotkey} ya está en uso", _hotkey);
            SaveError = "Esta combinación está en uso por el sistema o por otra app. Probá otra.";
            return false;
        }
        catch (HotkeyConfigSaveException ex)
        {
            _logger.LogWarning(ex, "Guardado de hotkey config falló");
            SaveError = ex.Message;
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inesperado al guardar hotkey config");
            SaveError = "Error inesperado al guardar la configuración. Probá de nuevo.";
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void NotifyDerivedChanged()
    {
        OnPropertyChanged(nameof(HasHotkey));
        OnPropertyChanged(nameof(HotkeyDisplay));
        OnPropertyChanged(nameof(HasWarning));
        OnPropertyChanged(nameof(WarningMessage));
    }
}
