using Microsoft.Extensions.Logging;
using Spikit.Models;

namespace Spikit.Services.Orchestration;

// Stub para tests / arranque early: loguea sin levantar window real.
internal sealed class LoggingFloatingResultPresenter : IFloatingResultPresenter
{
    private readonly ILogger<LoggingFloatingResultPresenter> _logger;

    public LoggingFloatingResultPresenter(ILogger<LoggingFloatingResultPresenter> logger)
    {
        _logger = logger;
    }

    public void Show(ResultErrorReason reason, string? text = null, IntPtr targetHwnd = default)
    {
        _logger.LogWarning(
            "FloatingResultPresenter STUB — reason={Reason}, hasText={HasText}, hasHwnd={HasHwnd}",
            reason, !string.IsNullOrEmpty(text), targetHwnd != IntPtr.Zero);
    }

    public void Hide()
    {
        // No-op: el stub no muestra nada.
    }
}
