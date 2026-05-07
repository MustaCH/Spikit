using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Spikit.Models;
using Spikit.Services.Provider;

namespace Spikit.ViewModels.Onboarding;

// VM del paso 1 del onboarding (Provider). Expone los 4 campos editables (preset, base URL,
// API key, model) + validación + comando "Probar conexión" + estado del feedback.
//
// Comportamiento clave:
// - Cambiar el preset autocompleta BaseUrl + Model con los defaults canónicos.
// - Si el usuario edita BaseUrl o Model manualmente, el preset queda como `Custom` automáticamente.
// - Editar cualquier campo invalida una prueba de conexión exitosa previa (US-1.1: hay que
//   re-probar para volver a habilitar "Siguiente").
// - ApiKey es un string plano en V1; persistir vía DPAPI lo hace EP-3.4.
//
// Validación sync de la API key (capa 1):
//   - Vacía / whitespace puro              → error "API key vacía".
//   - Whitespace interno                   → error "Sin espacios ni saltos de línea".
//   - Longitud fuera de [20, 500]          → error con el rango.
//   - No empieza con "sk-"                 → SOFT WARNING (no bloquea, advierte).
//
// Validación async (capa 2): IProviderConnectionTester contra GET {BaseUrl}/models.
public sealed class ProviderStepViewModel : ViewModelBase
{
    public const int MinApiKeyLength = 20;
    public const int MaxApiKeyLength = 500;

    private readonly ILogger<ProviderStepViewModel> _logger;
    private readonly IProviderConnectionTester _connectionTester;

    // Suprime el auto-set a Custom mientras el VM está autocompletando los campos.
    private bool _isApplyingPreset;

    // True una vez que el usuario tipeó algo en API key. Antes de eso, no mostramos
    // el error "vacía" al recién abrir el form.
    private bool _apiKeyTouched;

    private ProviderPreset _selectedPreset = ProviderPreset.OpenAI;
    private string _baseUrl = string.Empty;
    private string _apiKey = string.Empty;
    private string _model = string.Empty;
    private bool _isApiKeyVisible;
    private bool _isBusyTesting;

    private ProviderConnectionStatus _connectionStatus = ProviderConnectionStatus.Idle;
    private string _connectionMessage = string.Empty;
    private DateTime? _connectionTestedAt;

    public ProviderStepViewModel(
        ILogger<ProviderStepViewModel> logger,
        IProviderConnectionTester connectionTester)
    {
        _logger = logger;
        _connectionTester = connectionTester;

        Presets = new ObservableCollection<ProviderPresetOption>(
            Enum.GetValues<ProviderPreset>()
                .Select(p => new ProviderPresetOption(p, ProviderPresetDefaults.DisplayName(p))));

        AvailableModels = new ObservableCollection<string>();

        var initialDefaults = ProviderPresetDefaults.For(_selectedPreset);
        _baseUrl = initialDefaults.BaseUrl;
        _model = initialDefaults.Model;
        RefreshAvailableModels(_selectedPreset);

        TestConnectionCommand = new RelayCommand(
            execute: () => _ = TestConnectionAsync(),
            canExecute: () => CanTestConnection);
    }

    // Disparado cada vez que IsConnectionOk cambia. El OnboardingViewModel se suscribe
    // para recomputar CanGoNext del shell — no podemos usar PropertyChanged genérico
    // porque hay varias propiedades cambiando juntas y queremos un trigger explícito.
    public event EventHandler? ConnectionStateChanged;

    public ObservableCollection<ProviderPresetOption> Presets { get; }

    // Lista de modelos canónicos del preset actual. Se vacía cuando preset=Custom (ahí
    // `IsCustomPreset` se vuelve true y la UI swappea a un TextBox libre).
    public ObservableCollection<string> AvailableModels { get; }

    // True cuando preset == Custom. La UI lo usa para:
    //   - Mostrar Base URL (oculta para OpenAI/Groq porque su URL es fija y conocida).
    //   - Swap el campo Modelo de ComboBox a TextBox libre.
    public bool IsCustomPreset => _selectedPreset == ProviderPreset.Custom;

    public ICommand TestConnectionCommand { get; }

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
                InvalidateConnectionStatus();
            }
        }
    }

    public string ApiKey
    {
        get => _apiKey;
        set
        {
            if (SetProperty(ref _apiKey, value))
            {
                if (!string.IsNullOrEmpty(value)) _apiKeyTouched = true;
                NotifyApiKeyValidationChanged();
                InvalidateConnectionStatus();
            }
        }
    }

    public string Model
    {
        get => _model;
        set
        {
            if (SetProperty(ref _model, value))
            {
                DemotePresetToCustomIfManualEdit();
                InvalidateConnectionStatus();
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

    public bool IsApiKeyHidden => !_isApiKeyVisible;

    public bool IsBusyTesting
    {
        get => _isBusyTesting;
        set
        {
            if (SetProperty(ref _isBusyTesting, value))
            {
                OnPropertyChanged(nameof(IsNotBusyTesting));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsNotBusyTesting => !_isBusyTesting;

    public ProviderConnectionStatus ConnectionStatus
    {
        get => _connectionStatus;
        private set
        {
            if (SetProperty(ref _connectionStatus, value))
            {
                OnPropertyChanged(nameof(IsConnectionOk));
                OnPropertyChanged(nameof(IsConnectionStatusVisible));
                ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string ConnectionMessage
    {
        get => _connectionMessage;
        private set => SetProperty(ref _connectionMessage, value);
    }

    public DateTime? ConnectionTestedAt
    {
        get => _connectionTestedAt;
        private set
        {
            if (SetProperty(ref _connectionTestedAt, value))
            {
                OnPropertyChanged(nameof(ConnectionTestedAtFormatted));
            }
        }
    }

    public string ConnectionTestedAtFormatted =>
        _connectionTestedAt.HasValue ? _connectionTestedAt.Value.ToString("HH:mm") : string.Empty;

    public bool IsConnectionOk => _connectionStatus == ProviderConnectionStatus.Ok;

    public bool IsConnectionStatusVisible => _connectionStatus != ProviderConnectionStatus.Idle;

    // Validación sync de la API key — derivadas, no tienen setter.

    public string ApiKeyHardError
    {
        get
        {
            if (!_apiKeyTouched && string.IsNullOrEmpty(_apiKey)) return string.Empty;
            return ComputeApiKeyHardError(_apiKey);
        }
    }

    public string ApiKeySoftWarning
    {
        get
        {
            if (string.IsNullOrEmpty(_apiKey)) return string.Empty;
            if (!string.IsNullOrEmpty(ApiKeyHardError)) return string.Empty; // no apilar errores
            return ComputeApiKeySoftWarning(_apiKey);
        }
    }

    public bool HasApiKeyError => !string.IsNullOrEmpty(ApiKeyHardError);
    public bool HasApiKeyWarning => !string.IsNullOrEmpty(ApiKeySoftWarning);

    public bool CanTestConnection =>
        !_isBusyTesting
        && !string.IsNullOrWhiteSpace(_baseUrl)
        && !string.IsNullOrWhiteSpace(_apiKey)
        && !HasApiKeyError;

    private static string ComputeApiKeyHardError(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return "La API key está vacía.";
        if (apiKey.Any(char.IsWhiteSpace)) return "La API key no puede tener espacios ni saltos de línea.";
        if (apiKey.Length < MinApiKeyLength)
            return $"Las keys válidas tienen al menos {MinApiKeyLength} caracteres.";
        if (apiKey.Length > MaxApiKeyLength)
            return $"Las keys válidas tienen como máximo {MaxApiKeyLength} caracteres.";
        return string.Empty;
    }

    private static string ComputeApiKeySoftWarning(string apiKey)
    {
        // Soft warning para keys que no parecen formato OpenAI. Cualquier provider compatible
        // (Groq, custom proxies) puede usar otro prefijo, así que no bloqueamos.
        if (!apiKey.StartsWith("sk-", StringComparison.Ordinal))
        {
            return "Las keys de OpenAI normalmente empiezan con \"sk-\". Si tu provider usa otro formato, ignorá este aviso.";
        }
        return string.Empty;
    }

    private async Task TestConnectionAsync()
    {
        if (!CanTestConnection) return;

        _logger.LogInformation("Probando conexión a {BaseUrl} con preset {Preset}", _baseUrl, _selectedPreset);
        IsBusyTesting = true;
        ConnectionStatus = ProviderConnectionStatus.Testing;
        ConnectionMessage = "Probando…";

        try
        {
            var result = await _connectionTester.TestAsync(_baseUrl, _apiKey).ConfigureAwait(true);

            if (result.IsOk)
            {
                ConnectionTestedAt = DateTime.Now;
                ConnectionMessage = "Conexión OK";
                ConnectionStatus = ProviderConnectionStatus.Ok;
                _logger.LogInformation("Conexión OK con {BaseUrl}", _baseUrl);
            }
            else
            {
                ConnectionTestedAt = null;
                ConnectionMessage = result.Message;
                ConnectionStatus = ProviderConnectionStatus.Error;
                _logger.LogInformation("Conexión falló: {Message}", result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inesperado al probar conexión");
            ConnectionTestedAt = null;
            ConnectionMessage = "Error inesperado al probar la conexión.";
            ConnectionStatus = ProviderConnectionStatus.Error;
        }
        finally
        {
            IsBusyTesting = false;
        }
    }

    private void SelectPreset(ProviderPreset preset, bool clearFieldsForCustom)
    {
        if (!SetProperty(ref _selectedPreset, preset, nameof(SelectedPreset))) return;

        OnPropertyChanged(nameof(IsCustomPreset));
        RefreshAvailableModels(preset);

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

    private void RefreshAvailableModels(ProviderPreset preset)
    {
        AvailableModels.Clear();
        foreach (var m in ProviderPresetDefaults.For(preset).AvailableModels)
        {
            AvailableModels.Add(m);
        }
    }

    private void DemotePresetToCustomIfManualEdit()
    {
        if (_isApplyingPreset || _selectedPreset == ProviderPreset.Custom) return;

        var current = ProviderPresetDefaults.For(_selectedPreset);
        if (_baseUrl == current.BaseUrl && _model == current.Model) return;

        SelectPreset(ProviderPreset.Custom, clearFieldsForCustom: false);
    }

    // Cualquier edición de los campos del form invalida una prueba previa: el "Conexión OK"
    // que tenías arriba ya no aplica al nuevo input. Volvemos a Idle.
    private void InvalidateConnectionStatus()
    {
        if (_connectionStatus == ProviderConnectionStatus.Idle) return;
        ConnectionStatus = ProviderConnectionStatus.Idle;
        ConnectionMessage = string.Empty;
        ConnectionTestedAt = null;
    }

    private void NotifyApiKeyValidationChanged()
    {
        OnPropertyChanged(nameof(ApiKeyHardError));
        OnPropertyChanged(nameof(ApiKeySoftWarning));
        OnPropertyChanged(nameof(HasApiKeyError));
        OnPropertyChanged(nameof(HasApiKeyWarning));
    }
}

// DTO para el ItemSource del ComboBox: combina valor enum + label localizado.
public sealed record ProviderPresetOption(ProviderPreset Value, string Label);
