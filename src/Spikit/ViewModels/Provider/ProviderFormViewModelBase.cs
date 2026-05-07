using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Spikit.Models;
using Spikit.Services.Provider;

namespace Spikit.ViewModels.Provider;

// Base compartida entre el form Provider del onboarding (EP-3.2/3.3/3.4 — wizard step) y
// el de Settings (EP-4.3 — sección post-onboarding). Extrae todo lo que es idéntico:
// presets canónicos, validación sync de la API key, comando de Probar conexión, status del
// test, demote-a-Custom al editar manualmente, y SaveAsync vía IProviderConfigWriter.
//
// Lo que cada subclase agrega:
//   - StepVM (wizard): `ConnectionStateChanged` event para que el shell del onboarding
//     recompute CanGoNext.
//   - SectionVM (settings): precarga desde JsonSettings + DPAPI, badge "Estás usando…",
//     UX de "Reemplazar key" (key existente oculta vs editando una nueva), comando
//     SaveCommand standalone (en onboarding lo dispara el shell).
//
// Validación de la API key (capa 1):
//   - Vacía / whitespace puro              → error "API key vacía".
//   - Whitespace interno                   → error "Sin espacios ni saltos de línea".
//   - Longitud fuera de [20, 500]          → error con el rango.
//   - No empieza con "sk-"                 → SOFT WARNING (no bloquea, advierte).
//
// Validación async (capa 2): IProviderConnectionTester contra GET {BaseUrl}/models.
public abstract class ProviderFormViewModelBase : ViewModelBase
{
    public const int MinApiKeyLength = 20;
    public const int MaxApiKeyLength = 500;

    private readonly ILogger _logger;
    private readonly IProviderConnectionTester _connectionTester;
    private readonly IProviderConfigWriter _configWriter;

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

    private bool _isSaving;
    private string _saveError = string.Empty;

    protected ProviderFormViewModelBase(
        ILogger logger,
        IProviderConnectionTester connectionTester,
        IProviderConfigWriter configWriter)
    {
        _logger = logger;
        _connectionTester = connectionTester;
        _configWriter = configWriter;

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

    public ObservableCollection<ProviderPresetOption> Presets { get; }

    // Lista de modelos canónicos del preset actual. Se vacía cuando preset=Custom (ahí
    // `IsCustomPreset` se vuelve true y la UI swappea a un TextBox libre).
    public ObservableCollection<string> AvailableModels { get; }

    // True cuando preset == Custom. La UI lo usa para:
    //   - Mostrar Base URL editable (oculta para OpenAI/Groq porque su URL es fija).
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
        protected set
        {
            if (SetProperty(ref _connectionStatus, value))
            {
                OnPropertyChanged(nameof(IsConnectionOk));
                OnPropertyChanged(nameof(IsConnectionStatusVisible));
                OnConnectionStatusChanged();
            }
        }
    }

    public string ConnectionMessage
    {
        get => _connectionMessage;
        protected set => SetProperty(ref _connectionMessage, value);
    }

    public DateTime? ConnectionTestedAt
    {
        get => _connectionTestedAt;
        protected set
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

    // Subclases pueden override para incorporar consideraciones extra (ej. "Reemplazar key"
    // del SectionVM, donde la key existente cuenta como válida aunque ApiKey property esté vacío).
    public virtual bool CanTestConnection =>
        !_isBusyTesting
        && !string.IsNullOrWhiteSpace(_baseUrl)
        && !string.IsNullOrWhiteSpace(_apiKey)
        && !HasApiKeyError;

    public bool IsSaving
    {
        get => _isSaving;
        protected set
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
        protected set
        {
            if (SetProperty(ref _saveError, value))
            {
                OnPropertyChanged(nameof(HasSaveError));
            }
        }
    }

    public bool HasSaveError => !string.IsNullOrEmpty(_saveError);

    // Hook para subclases: se dispara cuando ConnectionStatus cambia. La base no hace nada;
    // el StepVM lo usa para emitir su `ConnectionStateChanged` event público.
    protected virtual void OnConnectionStatusChanged()
    {
    }

    // Hook para subclases: la subclase devuelve la key efectiva a usar al testear/guardar.
    // Por default es el contenido del campo ApiKey. El SectionVM lo override para devolver
    // la key existente (de DPAPI) si el usuario no apretó "Reemplazar".
    protected virtual string ResolveEffectiveApiKey() => _apiKey;

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

    protected async Task TestConnectionAsync()
    {
        if (!CanTestConnection) return;

        var effectiveKey = ResolveEffectiveApiKey();
        _logger.LogInformation("Probando conexión a {BaseUrl} con preset {Preset}", _baseUrl, _selectedPreset);
        IsBusyTesting = true;
        ConnectionStatus = ProviderConnectionStatus.Testing;
        ConnectionMessage = "Probando…";

        try
        {
            var result = await _connectionTester.TestAsync(_baseUrl, effectiveKey).ConfigureAwait(true);

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
    protected void InvalidateConnectionStatus()
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

    // Persiste la config actual del form vía IProviderConfigWriter. El StepVM y el SectionVM
    // comparten esta lógica: el StepVM la dispara desde el shell del onboarding al avanzar
    // de paso; el SectionVM la dispara desde un comando "Guardar" propio.
    //
    // Devuelve true si OK, false si hubo error (SaveError tiene el mensaje listo para mostrar).
    public async Task<bool> SaveAsync(CancellationToken ct = default)
    {
        if (!IsConnectionOk)
        {
            SaveError = "Tenés que probar la conexión antes de guardar.";
            return false;
        }

        SaveError = string.Empty;
        IsSaving = true;
        try
        {
            var config = new ProviderConfig(
                PresetId: ProviderPresetDefaults.ToPresetId(_selectedPreset),
                BaseUrl: _baseUrl,
                Model: _model,
                ApiKey: ResolveEffectiveApiKey());

            await _configWriter.SaveAsync(config, ct).ConfigureAwait(true);
            _logger.LogInformation("Provider config persistida ({Preset})", config.PresetId);
            OnSaveSucceeded();
            return true;
        }
        catch (ProviderConfigSaveException ex)
        {
            _logger.LogWarning(ex, "Guardado de provider config falló");
            SaveError = ex.Message;
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inesperado al guardar provider config");
            SaveError = "Error inesperado al guardar la configuración. Probá de nuevo.";
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }

    // Hook para subclases que quieren reaccionar a un Save exitoso (ej. SectionVM refresca
    // el badge + sale del modo "Reemplazar key").
    protected virtual void OnSaveSucceeded()
    {
    }

    // Permite a subclases setear el preset internamente sin disparar el clearFieldsForCustom
    // del setter público. Lo usa el SectionVM al precargar desde JsonSettings.
    protected void ApplyPresetInternal(ProviderPreset preset, string baseUrl, string model)
    {
        _isApplyingPreset = true;
        try
        {
            if (SetProperty(ref _selectedPreset, preset, nameof(SelectedPreset)))
            {
                OnPropertyChanged(nameof(IsCustomPreset));
                RefreshAvailableModels(preset);
            }
            BaseUrl = baseUrl;
            Model = model;
        }
        finally
        {
            _isApplyingPreset = false;
        }
    }
}

// DTO para el ItemSource del ComboBox: combina valor enum + label localizado.
public sealed record ProviderPresetOption(ProviderPreset Value, string Label);
