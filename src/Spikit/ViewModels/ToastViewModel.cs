using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using Spikit.Models;

namespace Spikit.ViewModels;

// VM de un toast individual (EP-5.3 / FLOW 5). Stateful: bindings al XAML para color del
// dot, texto, visibilidad de la acción. La lógica de cola/timing está en ToastService —
// el VM solo expone DismissRequested cuando el usuario click la acción para que el host
// pueda cerrar la window.
public sealed class ToastViewModel : ViewModelBase
{
    // Colores del dot (design-system §9.18 / FLOW 5).
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30));
    private static readonly Brush WarningBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly Brush InfoBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x6B, 0x6B));
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));

    private readonly ILogger<ToastViewModel>? _logger;

    private ToastSeverity _severity;
    private string _title = string.Empty;
    private string? _message;
    private string? _actionLabel;
    private ToastAction? _action;

    public ToastSeverity Severity
    {
        get => _severity;
        private set
        {
            if (SetProperty(ref _severity, value))
            {
                OnPropertyChanged(nameof(DotBrush));
            }
        }
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string? Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public string? ActionLabel
    {
        get => _actionLabel;
        private set
        {
            if (SetProperty(ref _actionLabel, value))
            {
                OnPropertyChanged(nameof(HasAction));
            }
        }
    }

    public bool HasAction => !string.IsNullOrEmpty(ActionLabel);

    public Brush DotBrush => Severity switch
    {
        ToastSeverity.Error => ErrorBrush,
        ToastSeverity.Warning => WarningBrush,
        ToastSeverity.Success => SuccessBrush,
        _ => InfoBrush,
    };

    public ICommand ActionCommand { get; }

    // El host se suscribe acá para cerrar la window cuando el usuario clickea la acción.
    public event EventHandler? DismissRequested;

    public ToastViewModel(ToastNotification notification, ILogger<ToastViewModel>? logger = null)
    {
        _logger = logger;
        Apply(notification);
        ActionCommand = new RelayCommand(InvokeAction, () => HasAction);
    }

    // Refresh para dedupe hit — actualiza contenido in-place sin recrear la window.
    public void Apply(ToastNotification notification)
    {
        Severity = notification.Severity;
        Title = notification.Title;
        Message = notification.Message;
        ActionLabel = notification.Action?.Label;
        _action = notification.Action;
    }

    private void InvokeAction()
    {
        if (_action is null) return;

        try
        {
            _action.OnInvoke();
        }
        catch (NotImplementedException ex)
        {
            // Caso esperado durante desarrollo si EP-5.3 corre antes que la sección Settings
            // de destino exista. No propaguemos — el usuario vería un crash en producción.
            _logger?.LogWarning(ex, "Toast action no implementada todavía: {Label}", ActionLabel);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Toast action lanzó excepción: {Label}", ActionLabel);
        }

        DismissRequested?.Invoke(this, EventArgs.Empty);
    }
}
