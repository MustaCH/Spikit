using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using Spikit.Models;

namespace Spikit.ViewModels.Onboarding;

// VM del paso 1 del onboarding (Provider). Expone los 4 campos editables (preset, base URL,
// API key, model) + flag IsBusyTesting que va a usar EP-3.3 cuando se cablée "Probar conexión".
//
// Comportamiento clave:
// - Cambiar el preset autocompleta BaseUrl + Model con los defaults canónicos.
// - Si el usuario edita BaseUrl o Model manualmente, el preset queda como `Custom` automáticamente
//   (no queremos mostrar "OpenAI" mientras la URL apunta a otro lado — UX engañosa).
// - ApiKey es un string plano en V1; persistir vía DPAPI lo hace EP-3.4. La capa segura entra
//   recién al guardar.
//
// La validación + test de conexión NO están acá — EP-3.3 los suma como features separados.
public sealed class ProviderStepViewModel : ViewModelBase
{
    private readonly ILogger<ProviderStepViewModel> _logger;

    // Suprime el auto-set a Custom mientras el VM está autocompletando los campos.
    // Sin esto, setear BaseUrl tras cambiar el preset disparaba un loop que volvía a Custom.
    private bool _isApplyingPreset;

    private ProviderPreset _selectedPreset = ProviderPreset.OpenAI;
    private string _baseUrl = string.Empty;
    private string _apiKey = string.Empty;
    private string _model = string.Empty;
    private bool _isApiKeyVisible;
    private bool _isBusyTesting;

    public ProviderStepViewModel(ILogger<ProviderStepViewModel> logger)
    {
        _logger = logger;

        Presets = new ObservableCollection<ProviderPresetOption>(
            Enum.GetValues<ProviderPreset>()
                .Select(p => new ProviderPresetOption(p, ProviderPresetDefaults.DisplayName(p))));

        // Sembrar los campos con los defaults del preset inicial (OpenAI). Sin disparar el
        // path de "selección manual con clearFields" porque acá no hay usuario, es bootstrap.
        var initialDefaults = ProviderPresetDefaults.For(_selectedPreset);
        _baseUrl = initialDefaults.BaseUrl;
        _model = initialDefaults.Model;
    }

    public ObservableCollection<ProviderPresetOption> Presets { get; }

    // El binding del ComboBox llega acá: es selección manual del usuario, así que
    // OpenAI/Groq autocompletan defaults y Custom vacía los campos (requirements.md US-1.1).
    public ProviderPreset SelectedPreset
    {
        get => _selectedPreset;
        set => SelectPreset(value, clearFieldsForCustom: true);
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set
        {
            if (SetProperty(ref _baseUrl, value))
            {
                DemotePresetToCustomIfManualEdit();
            }
        }
    }

    public string ApiKey
    {
        get => _apiKey;
        set => SetProperty(ref _apiKey, value);
    }

    public string Model
    {
        get => _model;
        set
        {
            if (SetProperty(ref _model, value))
            {
                DemotePresetToCustomIfManualEdit();
            }
        }
    }

    public bool IsApiKeyVisible
    {
        get => _isApiKeyVisible;
        set
        {
            if (SetProperty(ref _isApiKeyVisible, value))
            {
                OnPropertyChanged(nameof(IsApiKeyHidden));
            }
        }
    }

    // Espejo invertido: WPF BooleanToVisibilityConverter no soporta inversión, y mientras
    // no tengamos un InverseBoolConverter compartido (post-EP-6 pulido) este shim es lo
    // más liviano. Bind del PasswordBox a este flag (Visible cuando NO toggle).
    public bool IsApiKeyHidden => !_isApiKeyVisible;

    public bool IsBusyTesting
    {
        get => _isBusyTesting;
        set
        {
            if (SetProperty(ref _isBusyTesting, value))
            {
                OnPropertyChanged(nameof(IsNotBusyTesting));
            }
        }
    }

    // Bind a IsEnabled de los inputs: están enabled cuando NO estamos probando conexión.
    // EP-3.3 setea IsBusyTesting=true durante el "Probar conexión" para freezear el form.
    public bool IsNotBusyTesting => !_isBusyTesting;

    private void SelectPreset(ProviderPreset preset, bool clearFieldsForCustom)
    {
        if (!SetProperty(ref _selectedPreset, preset, nameof(SelectedPreset))) return;

        _isApplyingPreset = true;
        try
        {
            if (preset == ProviderPreset.Custom)
            {
                if (clearFieldsForCustom)
                {
                    BaseUrl = string.Empty;
                    Model = string.Empty;
                    _logger.LogDebug("Preset → Custom (campos vaciados por selección manual del dropdown)");
                }
                else
                {
                    _logger.LogDebug("Preset → Custom (degradado por edición manual; campos preservados)");
                }
                return;
            }

            var defaults = ProviderPresetDefaults.For(preset);
            BaseUrl = defaults.BaseUrl;
            Model = defaults.Model;
            _logger.LogDebug("Preset → {Preset} (BaseUrl={BaseUrl}, Model={Model})",
                preset, defaults.BaseUrl, defaults.Model);
        }
        finally
        {
            _isApplyingPreset = false;
        }
    }

    // Si el usuario edita BaseUrl o Model y el resultado ya no coincide con los defaults
    // del preset actual, degradamos a Custom — sin tocar los campos (eran intencionales).
    // Es el camino opuesto a la selección manual del dropdown: ahí Custom limpia, acá preserva.
    private void DemotePresetToCustomIfManualEdit()
    {
        if (_isApplyingPreset || _selectedPreset == ProviderPreset.Custom) return;

        var current = ProviderPresetDefaults.For(_selectedPreset);
        if (_baseUrl == current.BaseUrl && _model == current.Model) return;

        SelectPreset(ProviderPreset.Custom, clearFieldsForCustom: false);
    }
}

// DTO para el ItemSource del ComboBox: combina valor enum + label localizado.
public sealed record ProviderPresetOption(ProviderPreset Value, string Label);
