using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Spikit.Services.Insertion;

namespace Spikit.ViewModels;

// VM de la FloatingResultWindow (sub-task #7 de EP-2). Recibe el texto + razón del
// fallback (TargetGone / Failed) y maneja los commands de copiar y cerrar.
public sealed class FloatingResultViewModel : ViewModelBase
{
    private static readonly TimeSpan CopiedFeedbackDuration = TimeSpan.FromMilliseconds(1500);

    private readonly ILogger<FloatingResultViewModel> _logger;
    private readonly Dispatcher _dispatcher;

    private string _text = string.Empty;
    private string _heading = string.Empty;
    private string _description = string.Empty;
    private bool _isCopied;
    private DispatcherTimer? _copiedTimer;

    public FloatingResultViewModel(ILogger<FloatingResultViewModel> logger)
    {
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;
        CopyCommand = new RelayCommand(CopyToClipboard);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
    }

    public event EventHandler? CloseRequested;

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    public string Heading
    {
        get => _heading;
        set => SetProperty(ref _heading, value);
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
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
    public ICommand CloseCommand { get; }

    public void Configure(string text, InsertionResult reason)
    {
        Text = text;
        IsCopied = false;
        (Heading, Description) = reason switch
        {
            InsertionResult.TargetGone =>
                ("La ventana destino se cerró",
                 "No pudimos pegar porque la app target ya no está activa. Copialo desde acá."),
            _ =>
                ("No pudimos pegar el texto",
                 "Algo bloqueó el paste en la app activa. Copialo desde acá."),
        };
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
}
