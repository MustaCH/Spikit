using Microsoft.Extensions.Logging;
using Spikit.Models;

namespace Spikit.Services.Toast;

// Orquestador puro de toasts (EP-5.3 / FLOW 5). Mantiene la lista de toasts visibles
// (max 3 — cuando llega un 4° se cierra el más antiguo, FIFO), aplica deduplicación por
// key, y dispara los timers de auto-dismiss. Delega la creación/cierre visual al IToastHost.
//
// Hover-pause y click-to-dismiss general quedan fuera de V1 (FLOW 5 los menciona pero el
// scope mínimo del ticket EP-5.3 no los requiere). La acción opcional sí se ejecuta.
internal sealed class ToastService : IToastService, IDisposable
{
    private const int MaxVisible = 3;

    private readonly IToastHost _host;
    private readonly ILogger<ToastService> _logger;
    private readonly object _lock = new();
    private readonly List<ToastEntry> _visible = new();
    private bool _disposed;

    public ToastService(IToastHost host, ILogger<ToastService> logger)
    {
        _host = host;
        _logger = logger;
        _host.Dismissed += OnHostDismissed;
    }

    private void OnHostDismissed(object? sender, Guid id)
    {
        // El host cerró una window (click en acción o cierre programático tras Dismiss).
        // Limpiamos nuestra lista — idempotente, si el id ya no está es no-op.
        NotifyDismissed(id);
    }

    public void Show(
        ToastSeverity severity,
        string title,
        string? message = null,
        ToastAction? action = null,
        TimeSpan? autoDismiss = null,
        string? dedupeKey = null)
    {
        if (_disposed) return;

        var notification = new ToastNotification(
            severity,
            title,
            message,
            action,
            autoDismiss ?? IToastService.DefaultAutoDismiss,
            dedupeKey);

        Guid? evictedId = null;
        Guid targetId;
        bool isRefresh;

        lock (_lock)
        {
            var existing = dedupeKey is not null
                ? _visible.FirstOrDefault(e => e.Notification.DedupeKey == dedupeKey)
                : null;

            if (existing is not null)
            {
                // Dedupe hit: refrescamos el toast existente y reseteamos el timer.
                existing.Notification = notification;
                ResetTimer(existing);
                targetId = existing.Id;
                isRefresh = true;
                _logger.LogDebug("Toast dedupe hit ({Key}) — refrescado", dedupeKey);
            }
            else
            {
                // Max 3: si ya hay 3, cerramos el más antiguo (FIFO) antes de mostrar el nuevo.
                if (_visible.Count >= MaxVisible)
                {
                    var oldest = _visible[0];
                    _visible.RemoveAt(0);
                    oldest.Timer.Dispose();
                    evictedId = oldest.Id;
                    _logger.LogDebug("Toast evicted (max-3 FIFO): {Id}", oldest.Id);
                }

                var entry = new ToastEntry { Id = Guid.NewGuid(), Notification = notification };
                entry.Timer = new Timer(OnTimerElapsed, entry.Id, notification.AutoDismiss, Timeout.InfiniteTimeSpan);
                _visible.Add(entry);
                targetId = entry.Id;
                isRefresh = false;
            }
        }

        // Llamamos al host fuera del lock — su impl WPF bouncea al UI thread y no queremos
        // contención sobre _lock mientras el dispatcher procesa.
        if (evictedId is { } eId) _host.Dismiss(eId);
        if (isRefresh) _host.Refresh(targetId, notification);
        else _host.Show(targetId, notification);
    }

    // Llamado por el host cuando el toast termina su animación de salida o el usuario lo cerró
    // manualmente. Sirve para limpiar entradas que ya no están visibles. Idempotente —
    // si el id ya no está en la lista (timer ya disparó), es no-op.
    private void NotifyDismissed(Guid id)
    {
        lock (_lock)
        {
            var index = _visible.FindIndex(e => e.Id == id);
            if (index < 0) return;
            _visible[index].Timer.Dispose();
            _visible.RemoveAt(index);
        }
    }

    private void OnTimerElapsed(object? state)
    {
        if (state is not Guid id) return;

        lock (_lock)
        {
            var index = _visible.FindIndex(e => e.Id == id);
            if (index < 0) return;
            _visible[index].Timer.Dispose();
            _visible.RemoveAt(index);
        }

        _host.Dismiss(id);
    }

    private static void ResetTimer(ToastEntry entry)
    {
        entry.Timer.Change(entry.Notification.AutoDismiss, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _host.Dismissed -= OnHostDismissed;

        lock (_lock)
        {
            foreach (var entry in _visible)
            {
                entry.Timer.Dispose();
            }
            _visible.Clear();
        }
    }

    private sealed class ToastEntry
    {
        public Guid Id { get; init; }
        public ToastNotification Notification { get; set; } = null!;
        public Timer Timer { get; set; } = null!;
    }
}
