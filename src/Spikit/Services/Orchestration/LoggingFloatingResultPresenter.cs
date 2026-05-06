using Microsoft.Extensions.Logging;
using Spikit.Services.Insertion;

namespace Spikit.Services.Orchestration;

// Stub temporal hasta que llegue FloatingResultWindow (sub-task #7).
// Loguea el resultado y permite seguir el flow sin bloquear el sprint.
internal sealed class LoggingFloatingResultPresenter : IFloatingResultPresenter
{
    private readonly ILogger<LoggingFloatingResultPresenter> _logger;

    public LoggingFloatingResultPresenter(ILogger<LoggingFloatingResultPresenter> logger)
    {
        _logger = logger;
    }

    public void Show(string text, InsertionResult reason)
    {
        _logger.LogWarning(
            "FloatingResultPresenter STUB — paste {Reason}, texto: {Text}",
            reason, text);
    }

    public void Hide()
    {
        // No-op: el stub no muestra nada.
    }
}
