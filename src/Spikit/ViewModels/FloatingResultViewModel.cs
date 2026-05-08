using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Spikit.Models;
using Spikit.Services.Insertion;
using Spikit.Services.Settings;
using Spikit.Services.Toast;
using Spikit.ViewModels.Settings;

namespace Spikit.ViewModels;

// VM del FloatingResultWindow rediseñado en EP-6.5. Recibe un ResultErrorReason + datos
// opcionales (texto, targetHwnd) y mapea a la variante visual correspondiente
// (V1-V6 de design-system §10.4 + flows.md FLOW 5).
//
// Cada variante define: heading, description, ícono Lucide, color del ícono, visibilidad
// del textbox y set de botones disponibles.
public sealed class FloatingResultViewModel : ViewModelBase
{
    private static readonly TimeSpan CopiedFeedbackDuration = TimeSpan.FromMilliseconds(1500);

    private readonly ILogger<FloatingResultViewModel> _logger;
    private readonly ITextInsertionService _insertion;
    private readonly ISettingsWindowPresenter _settingsPresenter;
    private readonly IToastService _toast;
    private readonly Dispatcher _dispatcher;

    private ResultErrorReason _reason = ResultErrorReason.PasteFailedNoText;
    private string _text = string.Empty;
    private string _heading = string.Empty;
    private string _description = string.Empty;
    private string _iconKey = "LucideBanGeometry";
    private string _iconBrushKey = "SpkStateErrorFgBrush";
    private bool _showText;
    private bool _isCopyVisible;
    private bool _isRetryPasteVisible;
    private bool _isOpenSettingsVisible;
    private bool _isCopied;
    private IntPtr _targetHwnd;
    private DispatcherTimer? _copiedTimer;

    public FloatingResultViewModel(
        ILogger<FloatingResultViewModel> logger,
        ITextInsertionService insertion,
        ISettingsWindowPresenter settingsPresenter,
        IToastService toast)
    {
        _logger = logger;
        _insertion = insertion;
        _settingsPresenter = settingsPresenter;
        _toast = toast;
        _dispatcher = Dispatcher.CurrentDispatcher;

        CopyCommand = new RelayCommand(CopyToClipboard);
        RetryPasteCommand = new RelayCommand(RetryPasteAsync);
        OpenSettingsProviderCommand = new RelayCommand(OpenSettingsProvider);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
    }

    public event EventHandler? CloseRequested;

    public string Text
    {
        get => _text;
        private set => SetProperty(ref _text, value);
    }

    public string Heading
    {
        get => _heading;
        private set => SetProperty(ref _heading, value);
    }

    public string Description
    {
        get => _description;
        private set => SetProperty(ref _description, value);
    }

    // Key del Geometry Lucide en Resources/Icons/Lucide.xaml. El XAML lo resuelve con DynamicResource.
    public string IconKey
    {
        get => _iconKey;
        private set => SetProperty(ref _iconKey, value);
    }

    // Key del SolidColorBrush del state token (error.fg vs warning.fg) que pinta el ícono.
    public string IconBrushKey
    {
        get => _iconBrushKey;
        private set => SetProperty(ref _iconBrushKey, value);
    }

    public bool ShowText
    {
        get => _showText;
        private set => SetProperty(ref _showText, value);
    }

    public bool IsCopyVisible
    {
        get => _isCopyVisible;
        private set => SetProperty(ref _isCopyVisible, value);
    }

    public bool IsRetryPasteVisible
    {
        get => _isRetryPasteVisible;
        private set => SetProperty(ref _isRetryPasteVisible, value);
    }

    public bool IsOpenSettingsVisible
    {
        get => _isOpenSettingsVisible;
        private set => SetProperty(ref _isOpenSettingsVisible, value);
    }

    public bool IsCopied
    {
        get => _isCopied;
        private set
        {
            if (SetProperty(ref _isCopied, value))
            {
                OnPropertyChanged(nameof(CopyButtonLabel));
            }
        }
    }

    public string CopyButtonLabel => IsCopied ? "✓ Copiado" : "Copiar al portapapeles";

    public ICommand CopyCommand { get; }
    public ICommand RetryPasteCommand { get; }
    public ICommand OpenSettingsProviderCommand { get; }
    public ICommand CloseCommand { get; }

    // Configura la window para una variante específica. Cancela cualquier timer de "copiado"
    // pendiente para que el reuso de window entre dictados no auto-cierre antes de tiempo.
    public void Configure(ResultErrorReason reason, string? text, IntPtr targetHwnd)
    {
        _copiedTimer?.Stop();
        _copiedTimer = null;
        IsCopied = false;

        _reason = reason;
        _targetHwnd = targetHwnd;
        Text = text ?? string.Empty;

        switch (reason)
        {
            case ResultErrorReason.PasteFailed:
                Heading = "No pudimos pegar el texto en la app activa.";
                Description = "Copialo desde acá o reintentá pegarlo si la app sigue abierta.";
                IconKey = "LucideBanGeometry";
                IconBrushKey = "SpkStateErrorFgBrush";
                ShowText = true;
                IsCopyVisible = true;
                IsRetryPasteVisible = targetHwnd != IntPtr.Zero;
                IsOpenSettingsVisible = false;
                break;

            case ResultErrorReason.PasteFailedNoText:
                Heading = "No pudimos pegar el texto.";
                Description = "Y tampoco pudimos recuperarlo. Probá dictar de nuevo.";
                IconKey = "LucideBanGeometry";
                IconBrushKey = "SpkStateErrorFgBrush";
                ShowText = false;
                IsCopyVisible = false;
                IsRetryPasteVisible = false;
                IsOpenSettingsVisible = false;
                break;

            case ResultErrorReason.AuthFailed:
                Heading = "Tu API key no es válida.";
                Description = "Configurá una key correcta en Settings → Provider para volver a dictar.";
                IconKey = "LucideKeyRoundGeometry";
                IconBrushKey = "SpkStateErrorFgBrush";
                ShowText = false;
                IsCopyVisible = false;
                IsRetryPasteVisible = false;
                IsOpenSettingsVisible = true;
                break;

            case ResultErrorReason.ServerError:
                Heading = "Hubo un problema con el servicio.";
                Description = "El provider no respondió correctamente. Probá dictar de nuevo en un momento.";
                IconKey = "LucideServerCrashGeometry";
                IconBrushKey = "SpkStateWarningFgBrush";
                ShowText = false;
                IsCopyVisible = false;
                IsRetryPasteVisible = false;
                IsOpenSettingsVisible = false;
                break;

            case ResultErrorReason.RateLimit:
                Heading = "Demasiadas requests al provider.";
                Description = "Esperá unos segundos antes de volver a dictar.";
                IconKey = "LucideClockGeometry";
                IconBrushKey = "SpkStateWarningFgBrush";
                ShowText = false;
                IsCopyVisible = false;
                IsRetryPasteVisible = false;
                IsOpenSettingsVisible = false;
                break;

            case ResultErrorReason.EmptyResult:
                Heading = "El provider devolvió texto vacío.";
                Description = "Probá hablar más claro o más cerca del micrófono.";
                IconKey = "LucideTextCursorGeometry";
                IconBrushKey = "SpkStateWarningFgBrush";
                ShowText = false;
                IsCopyVisible = false;
                IsRetryPasteVisible = false;
                IsOpenSettingsVisible = false;
                break;
        }
    }

    private void CopyToClipboard()
    {
        try
        {
            Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, _text), copy: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo copiar al clipboard desde FloatingResultWindow");
            return;
        }

        IsCopied = true;
        _copiedTimer?.Stop();
        _copiedTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = CopiedFeedbackDuration,
        };
        _copiedTimer.Tick += (_, _) =>
        {
            _copiedTimer?.Stop();
            _copiedTimer = null;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        };
        _copiedTimer.Start();
    }

    private async void RetryPasteAsync()
    {
        if (_targetHwnd == IntPtr.Zero || string.IsNullOrEmpty(_text))
        {
            _logger.LogWarning("RetryPaste invocado sin targetHwnd o sin texto — no-op");
            return;
        }

        try
        {
            var result = await _insertion.InsertIntoForegroundAsync(_text, _targetHwnd).ConfigureAwait(true);
            if (result == InsertionResult.Pasted)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            _logger.LogWarning("RetryPaste falló: {Result}", result);
            _toast.Show(
                ToastSeverity.Error,
                "No pudimos pegar de nuevo. Copialo desde acá.",
                dedupeKey: "retry-paste-failed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RetryPaste lanzó excepción inesperada");
            _toast.Show(
                ToastSeverity.Error,
                "No pudimos pegar de nuevo. Copialo desde acá.",
                dedupeKey: "retry-paste-failed");
        }
    }

    private void OpenSettingsProvider()
    {
        _settingsPresenter.Open(SettingsSection.Provider);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
