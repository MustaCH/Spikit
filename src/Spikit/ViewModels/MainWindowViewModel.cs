using Microsoft.Extensions.Logging;
using Spikit.Models;
using Spikit.Services.Hotkey;

namespace Spikit.ViewModels;

// NOTA TEMPORAL: este VM hace de smoke-test del HotkeyService hasta que llegue
// DictationOrchestrator (EP-2 sub-task #5). Cuando exista, mover el Register/eventos
// al orchestrator y dejar este VM acotado al chrome de la MainWindow.
public class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IHotkeyService _hotkey;
    private readonly ILogger<MainWindowViewModel> _logger;

    private string _hotkeyStatus = "Inicializando…";
    private string _hotkeyLabel = HotkeyDefinition.Default.ToString();
    private int _pressCount;
    private bool _disposed;

    public MainWindowViewModel(IHotkeyService hotkey, ILogger<MainWindowViewModel> logger)
    {
        _hotkey = hotkey;
        _logger = logger;

        _hotkey.HotkeyPressed += OnPressed;
        _hotkey.HotkeyReleased += OnReleased;

        try
        {
            _hotkey.Register(HotkeyDefinition.Default);
            HotkeyStatus = $"Esperando press…";
        }
        catch (HotkeyRegistrationException ex)
        {
            _logger.LogWarning(ex, "No se pudo registrar el hotkey");
            HotkeyStatus = $"⚠ {ex.Message}";
        }
    }

    public string HotkeyLabel
    {
        get => _hotkeyLabel;
        private set => SetProperty(ref _hotkeyLabel, value);
    }

    public string HotkeyStatus
    {
        get => _hotkeyStatus;
        private set => SetProperty(ref _hotkeyStatus, value);
    }

    public int PressCount
    {
        get => _pressCount;
        private set => SetProperty(ref _pressCount, value);
    }

    private void OnPressed(object? sender, EventArgs e)
    {
        PressCount++;
        HotkeyStatus = $"● Pressed (#{PressCount})";
        _logger.LogInformation("Hotkey pressed (#{Count})", PressCount);
    }

    private void OnReleased(object? sender, EventArgs e)
    {
        HotkeyStatus = $"○ Released — esperando próximo press…";
        _logger.LogInformation("Hotkey released");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hotkey.HotkeyPressed -= OnPressed;
        _hotkey.HotkeyReleased -= OnReleased;
    }
}
