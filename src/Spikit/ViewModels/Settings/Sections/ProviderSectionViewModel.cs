using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Spikit.Models;
using Spikit.Services.Provider;
using Spikit.Services.Secrets;
using Spikit.Services.Settings;
using Spikit.ViewModels.Provider;

namespace Spikit.ViewModels.Settings.Sections;

// VM de la sección Provider de Settings (EP-4.3). Extiende ProviderFormViewModelBase y
// agrega lo que diferencia esta sección del step del onboarding:
//
//   1. Precarga: al construir, lee settings.json + DPAPI para que el form arranque con la
//      config actual del usuario (no en blanco como el wizard).
//   2. Badge "Estás usando: {preset} · {model}" arriba de todo (US-3.2). Refresca tras
//      guardar — un Save exitoso muta los runtime singletons + persiste, así que la próxima
//      lectura ya refleja lo nuevo.
//   3. UX "Reemplazar key": por seguridad la key real nunca pasa al campo ApiKey del form.
//      Se mantiene en `_existingKey` (privado) y la UI muestra placeholder enmascarado.
//      Para cambiarla, el usuario aprieta "Reemplazar" → IsReplacingKey=true → el campo
//      se vuelve editable. Mientras `IsReplacingKey=false`, todas las operaciones que
//      necesitan la key (test, save) usan `_existingKey` vía override de
//      ResolveEffectiveApiKey().
//   4. SaveCommand standalone: el step del onboarding lo dispara el shell (al avanzar);
//      acá tenemos un botón "Guardar cambios" propio. Si la conexión no fue probada,
//      lo hace automáticamente antes de persistir (acceptance criteria del ticket).
public sealed class ProviderSectionViewModel : ProviderFormViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly ISecretStore _secrets;
    private readonly ILogger<ProviderSectionViewModel> _logger;
    private readonly IProviderConnectionTester _connectionTester;

    // Snapshot del último Save persistido — fuente de verdad para el badge superior y para
    // detectar pending changes (HasPendingChanges). Se refresca al precargar y tras
    // OnSaveSucceeded. Mantenerlo separado de las propiedades editables del form evita que
    // el badge cambie mientras el usuario edita sin guardar.
    private string _badgePresetLabel = string.Empty;
    private string _badgeModel = string.Empty;
    private string _persistedPresetId = string.Empty;
    private string _persistedBaseUrl = string.Empty;
    private string _persistedModel = string.Empty;

    // Key existente leída de DPAPI al precargar. Se usa transparentemente como key efectiva
    // cuando el usuario NO está en modo "Reemplazar". Nunca se expone via property pública —
    // mantener la key fuera del binding tree es el delta de seguridad de esta UX.
    private string _existingKey = string.Empty;

    private bool _hasExistingKey;
    private bool _isReplacingKey;

    public ProviderSectionViewModel(
        ILogger<ProviderSectionViewModel> logger,
        IProviderConnectionTester connectionTester,
        IProviderConfigWriter configWriter,
        ISettingsService settingsService,
        ISecretStore secrets)
        : base(logger, connectionTester, configWriter)
    {
        _logger = logger;
        _connectionTester = connectionTester;
        _settingsService = settingsService;
        _secrets = secrets;

        ReplaceKeyCommand = new RelayCommand(ReplaceKey);
        SaveCommand = new RelayCommand(
            execute: () => _ = SaveCommandAsync(),
            canExecute: () => !IsSaving && HasPendingChanges);

        // Recomputar HasPendingChanges + invalidar el SaveCommand cuando cambian los campos
        // editables o el modo Replace. La base ya hace InvalidateRequerySuggested al cambiar
        // IsSaving / IsBusyTesting; acá agregamos los triggers específicos del SectionVM.
        PropertyChanged += OnAnyPropertyChanged;

        LoadFromPersistence();
    }

    private void OnAnyPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SelectedPreset)
                            or nameof(BaseUrl)
                            or nameof(Model)
                            or nameof(ApiKey)
                            or nameof(IsReplacingKey))
        {
            OnPropertyChanged(nameof(HasPendingChanges));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    // ============ UX Reemplazar key ============

    // True si DPAPI tenía una key al precargar. Mientras es true Y IsReplacingKey=false,
    // el form está en estado "ya hay una key configurada, mostrá placeholder enmascarado".
    public bool HasExistingKey
    {
        get => _hasExistingKey;
        private set
        {
            if (SetProperty(ref _hasExistingKey, value))
            {
                OnPropertyChanged(nameof(IsKeyMaskedPlaceholderVisible));
                OnPropertyChanged(nameof(IsReplaceButtonVisible));
            }
        }
    }

    // True después de que el user apretó "Reemplazar key" (o si nunca hubo key — ahí
    // arrancamos directamente en estado de captura).
    public bool IsReplacingKey
    {
        get => _isReplacingKey;
        private set
        {
            if (SetProperty(ref _isReplacingKey, value))
            {
                OnPropertyChanged(nameof(IsKeyMaskedPlaceholderVisible));
                OnPropertyChanged(nameof(IsReplaceButtonVisible));
                OnPropertyChanged(nameof(IsKeyEditable));
                OnPropertyChanged(nameof(CanTestConnection));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    // Visibility de cada elemento del UX:
    //   - Placeholder masked  : visible cuando hay key existente y NO estás reemplazando.
    //   - Botón "Reemplazar"  : visible cuando hay key existente y NO estás reemplazando.
    //   - PasswordBox editable: visible cuando estás reemplazando o nunca hubo key.
    public bool IsKeyMaskedPlaceholderVisible => HasExistingKey && !IsReplacingKey;
    public bool IsReplaceButtonVisible => HasExistingKey && !IsReplacingKey;
    public bool IsKeyEditable => IsReplacingKey || !HasExistingKey;

    public ICommand ReplaceKeyCommand { get; }

    private void ReplaceKey()
    {
        if (IsReplacingKey) return;
        IsReplacingKey = true;
        ApiKey = string.Empty;
        // Invalidamos el "Conexión OK" previo: la key efectiva acaba de cambiar
        // conceptualmente (ahora va a usar lo que tipee el user, no la existente).
        InvalidateConnectionStatus();
        _logger.LogDebug("Provider section: usuario entró en modo Reemplazar key");
    }

    // ============ Badge "Estás usando…" ============

    public string BadgeText
    {
        get
        {
            if (string.IsNullOrEmpty(_badgePresetLabel) || string.IsNullOrEmpty(_badgeModel))
            {
                return "Sin configurar";
            }
            return $"{_badgePresetLabel} · {_badgeModel}";
        }
    }

    // ============ Save command standalone ============

    // True si hay diferencias entre el form y el último estado persistido (settings.json).
    // - Cambio de preset / baseUrl / model vs snapshot persistido → pending.
    // - IsReplacingKey con un ApiKey no vacío → pending (intención de rotar credencial).
    // - IsReplacingKey activo pero campo ApiKey vacío → NO pending (el user entró al modo
    //   pero todavía no tipeó nada; guardar sin key fallaría la validación igual).
    // El SaveCommand usa esto en su CanExecute para deshabilitar el botón cuando no hay
    // cambios reales que persistir.
    public bool HasPendingChanges
    {
        get
        {
            if (IsReplacingKey && !string.IsNullOrEmpty(ApiKey)) return true;
            if (ProviderPresetDefaults.ToPresetId(SelectedPreset) != _persistedPresetId) return true;
            if (BaseUrl != _persistedBaseUrl) return true;
            if (Model != _persistedModel) return true;
            return false;
        }
    }

    public ICommand SaveCommand { get; }

    private async Task SaveCommandAsync()
    {
        // Si la conexión actual no está probada con los valores vigentes, probamos primero.
        // Si pasa, encadenamos el Save heredado de la base. Si falla, no guardamos.
        if (!IsConnectionOk)
        {
            if (!CanTestConnection)
            {
                SaveError = "Completá la API key antes de guardar.";
                return;
            }

            await TestConnectionFromCommandAsync().ConfigureAwait(true);
            if (!IsConnectionOk) return;
        }

        await SaveAsync().ConfigureAwait(true);
    }

    // Permite invocar el TestConnectionAsync protected de la base desde el SaveCommand.
    // (TestConnectionAsync expuesto via TestConnectionCommand también, pero acá necesitamos
    // await directo para encadenar con el Save.)
    private async Task TestConnectionFromCommandAsync()
    {
        var key = ResolveEffectiveApiKey();
        if (!CanTestConnection || string.IsNullOrEmpty(key)) return;

        IsBusyTesting = true;
        ConnectionStatus = ProviderConnectionStatus.Testing;
        ConnectionMessage = "Probando…";
        try
        {
            var result = await _connectionTester.TestAsync(BaseUrl, key).ConfigureAwait(true);
            if (result.IsOk)
            {
                ConnectionTestedAt = DateTime.Now;
                ConnectionMessage = "Conexión OK";
                ConnectionStatus = ProviderConnectionStatus.Ok;
            }
            else
            {
                ConnectionTestedAt = null;
                ConnectionMessage = result.Message;
                ConnectionStatus = ProviderConnectionStatus.Error;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inesperado al probar conexión desde Save");
            ConnectionTestedAt = null;
            ConnectionMessage = "Error inesperado al probar la conexión.";
            ConnectionStatus = ProviderConnectionStatus.Error;
        }
        finally
        {
            IsBusyTesting = false;
        }
    }

    // ============ Overrides de la base ============

    protected override string ResolveEffectiveApiKey()
    {
        // Si el user no apretó "Reemplazar" y había key existente, esa es la efectiva.
        // Caso contrario (replace-mode o nunca hubo key) usamos lo del campo ApiKey.
        return !IsReplacingKey && HasExistingKey ? _existingKey : ApiKey;
    }

    public override bool CanTestConnection
    {
        get
        {
            // En modo masked (no replacing, hay key existente): habilitamos test si BaseUrl
            // tiene contenido (la key efectiva viene del cache, no del campo).
            if (!IsReplacingKey && HasExistingKey)
            {
                return !IsBusyTesting && !string.IsNullOrWhiteSpace(BaseUrl);
            }
            return base.CanTestConnection;
        }
    }

    protected override void OnSaveSucceeded()
    {
        // Save persistió: la key que se acaba de guardar pasa a ser la "existente". Salimos
        // del modo replace y dejamos el form en estado masked igual que al precargar.
        if (IsReplacingKey)
        {
            _existingKey = ApiKey;
            ApiKey = string.Empty;
            IsReplacingKey = false;
            HasExistingKey = !string.IsNullOrEmpty(_existingKey);
        }

        // El snapshot persistido pasa a ser lo que acabamos de guardar (valores actuales del
        // form). Si lo re-leyéramos del settings service podríamos quedar atrasados un tick
        // contra el evento SettingsChanged, y los tests con FakeConfigWriter que no toca el
        // FakeSettingsService verían un snapshot stale.
        _persistedPresetId = ProviderPresetDefaults.ToPresetId(SelectedPreset);
        _persistedBaseUrl = BaseUrl;
        _persistedModel = Model;
        _badgePresetLabel = ProviderPresetDefaults.DisplayName(SelectedPreset);
        _badgeModel = Model;
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(HasPendingChanges));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    // ============ Precarga ============

    private void LoadFromPersistence()
    {
        var settings = _settingsService.Load();
        var preset = ParsePresetId(settings.Provider.PresetId);
        var baseUrl = !string.IsNullOrWhiteSpace(settings.Provider.BaseUrl)
            ? settings.Provider.BaseUrl
            : ProviderPresetDefaults.For(preset).BaseUrl;
        var model = !string.IsNullOrWhiteSpace(settings.Provider.Model)
            ? settings.Provider.Model
            : ProviderPresetDefaults.For(preset).Model;

        ApplyPresetInternal(preset, baseUrl, model);

        // DPAPI: si la lectura tira (CB-14, perfil distinto) lo tratamos como "no había key".
        // El usuario va a tener que reingresarla.
        try
        {
            _existingKey = _secrets.Read(ProviderConfigWriter.ApiKeySecretName) ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DPAPI no pudo leer la key existente — caemos a estado sin key");
            _existingKey = string.Empty;
        }

        HasExistingKey = !string.IsNullOrEmpty(_existingKey);
        IsReplacingKey = !HasExistingKey;

        RefreshBadge();
    }

    private void RefreshBadge()
    {
        var settings = _settingsService.Load();
        var preset = ParsePresetId(settings.Provider.PresetId);

        _badgePresetLabel = ProviderPresetDefaults.DisplayName(preset);
        _badgeModel = !string.IsNullOrWhiteSpace(settings.Provider.Model)
            ? settings.Provider.Model
            : ProviderPresetDefaults.For(preset).Model;

        // Snapshot persistido — fuente de verdad para HasPendingChanges. Lo refrescamos
        // junto al badge porque ambos derivan del mismo Load().
        _persistedPresetId = ProviderPresetDefaults.ToPresetId(preset);
        _persistedBaseUrl = !string.IsNullOrWhiteSpace(settings.Provider.BaseUrl)
            ? settings.Provider.BaseUrl
            : ProviderPresetDefaults.For(preset).BaseUrl;
        _persistedModel = _badgeModel;

        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(HasPendingChanges));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private static ProviderPreset ParsePresetId(string presetId) => presetId switch
    {
        "openai" => ProviderPreset.OpenAI,
        "groq" => ProviderPreset.Groq,
        "custom" => ProviderPreset.Custom,
        _ => ProviderPreset.OpenAI,
    };
}
