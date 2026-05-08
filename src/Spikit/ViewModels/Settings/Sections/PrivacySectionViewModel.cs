using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Spikit.Services.Provider;
using Spikit.Services.Secrets;
using Spikit.Services.Settings;
using Spikit.Services.Transcription;

namespace Spikit.ViewModels.Settings.Sections;

// VM de la sección Privacidad de Settings (EP-4.7).
//
// Bloque 1 — Toggle "Historial local". Persiste privacy.historyEnabled en settings.json.
// Default OFF (RN-2). El cableado runtime (que la orquestación REALMENTE escriba history.json
// cuando el toggle está ON) vive en EP-4.10; acá solo persistimos el flag.
//
// Bloque 2 — Borrar API key. Click → confirm modal → DPAPI.Delete + WhisperApiKey.Update("")
// + flag InlineStatus para feedback. NO se reabre el onboarding automáticamente: la próxima
// transcripción va a fallar con 401 hasta que el usuario reconfigure el provider en
// Settings → Provider (decisión explícita del ticket EP-4.7).
//
// La invocación del modal se inyecta vía IConfirmationDialogService: el VM no conoce la
// Window. Esto mantiene el VM testeable (fakeable) y respeta la separación clásica MVVM
// donde la View (la dialog) es responsabilidad del shell, no del VM.
public sealed class PrivacySectionViewModel : ViewModelBase
{
    private readonly ILogger<PrivacySectionViewModel> _logger;
    private readonly ISettingsService _settingsService;
    private readonly ISecretStore _secrets;
    private readonly WhisperApiKey _runtimeKey;
    private readonly IConfirmationDialogService _confirmationDialog;

    private bool _historyEnabled;
    private string? _deleteFeedbackMessage;
    private bool _suppressEffects;

    public PrivacySectionViewModel(
        ILogger<PrivacySectionViewModel> logger,
        ISettingsService settingsService,
        ISecretStore secrets,
        WhisperApiKey runtimeKey,
        IConfirmationDialogService confirmationDialog)
    {
        _logger = logger;
        _settingsService = settingsService;
        _secrets = secrets;
        _runtimeKey = runtimeKey;
        _confirmationDialog = confirmationDialog;

        DeleteApiKeyCommand = new RelayCommand(DeleteApiKey);
        LoadFromPersistence();
    }

    // ============ Bloque 1 — Historial ============

    // Bindings TwoWay con los radios "OFF" / "ON" del XAML. Mismo patrón mutually-exclusive
    // que IsLanguageAuto/Spanish/English del AudioSectionViewModel.
    public bool IsHistoryOff
    {
        get => !_historyEnabled;
        set { if (value) HistoryEnabled = false; }
    }

    public bool IsHistoryOn
    {
        get => _historyEnabled;
        set { if (value) HistoryEnabled = true; }
    }

    public bool HistoryEnabled
    {
        get => _historyEnabled;
        set
        {
            if (!SetProperty(ref _historyEnabled, value)) return;
            OnPropertyChanged(nameof(IsHistoryOff));
            OnPropertyChanged(nameof(IsHistoryOn));

            if (_suppressEffects) return;

            PersistHistoryEnabled(value);
            _logger.LogDebug("Privacy.historyEnabled → {Value}", value);
        }
    }

    // ============ Bloque 2 — Borrar API key ============

    // Mensaje de feedback inline (design-system §9.11 — la app no usa toast popup global).
    // Se setea a "API key borrada" tras un delete exitoso, o a un mensaje de error si la
    // operación falla. Null = sin feedback. La XAML lo muestra con un estilo InlineStatus
    // y un fade-out automático (después de N segundos limpia el VM o el usuario navega).
    public string? DeleteFeedbackMessage
    {
        get => _deleteFeedbackMessage;
        private set
        {
            if (SetProperty(ref _deleteFeedbackMessage, value))
            {
                OnPropertyChanged(nameof(HasDeleteFeedback));
                OnPropertyChanged(nameof(IsDeleteFeedbackError));
            }
        }
    }

    public bool HasDeleteFeedback => !string.IsNullOrEmpty(_deleteFeedbackMessage);

    private bool _isDeleteFeedbackError;
    public bool IsDeleteFeedbackError
    {
        get => _isDeleteFeedbackError;
        private set => SetProperty(ref _isDeleteFeedbackError, value);
    }

    public ICommand DeleteApiKeyCommand { get; }

    private void DeleteApiKey()
    {
        var confirmed = _confirmationDialog.Confirm(new ConfirmationRequest(
            Title: "Borrar API key del sistema",
            Message: "Vas a tener que reconfigurar tu provider para volver a usar Spikit. ¿Continuar?",
            ConfirmLabel: "Borrar",
            CancelLabel: "Cancelar",
            IsDestructive: true));

        if (!confirmed)
        {
            _logger.LogDebug("Privacy: borrado de API key cancelado por el usuario");
            return;
        }

        try
        {
            _secrets.Delete(ProviderConfigWriter.ApiKeySecretName);
            _runtimeKey.Update(string.Empty);

            IsDeleteFeedbackError = false;
            DeleteFeedbackMessage = "API key borrada del sistema. Reconfigurá tu provider para volver a usar Spikit.";
            _logger.LogInformation("Privacy: API key borrada por el usuario");
        }
        catch (Exception ex)
        {
            IsDeleteFeedbackError = true;
            DeleteFeedbackMessage = "No se pudo borrar la API key. Probá de nuevo o revisá los permisos del perfil.";
            _logger.LogWarning(ex, "Privacy: error borrando la API key");
        }
    }

    // ============ Persistencia ============

    private void LoadFromPersistence()
    {
        _suppressEffects = true;
        try
        {
            var settings = _settingsService.Load();
            _historyEnabled = settings.Privacy.HistoryEnabled;
            OnPropertyChanged(nameof(HistoryEnabled));
            OnPropertyChanged(nameof(IsHistoryOff));
            OnPropertyChanged(nameof(IsHistoryOn));
        }
        finally
        {
            _suppressEffects = false;
        }
    }

    private void PersistHistoryEnabled(bool value)
    {
        var settings = _settingsService.Load();
        settings.Privacy.HistoryEnabled = value;
        _settingsService.Save(settings);
    }
}

// Contrato del modal — el VM no conoce la Window. La implementación WPF
// (WpfConfirmationDialogService) instancia ConfirmDialog y lo muestra modal.
// Los tests pueden inyectar un fake que devuelva true/false sin UI.
public interface IConfirmationDialogService
{
    bool Confirm(ConfirmationRequest request);
}

public sealed record ConfirmationRequest(
    string Title,
    string Message,
    string ConfirmLabel,
    string CancelLabel,
    bool IsDestructive);
