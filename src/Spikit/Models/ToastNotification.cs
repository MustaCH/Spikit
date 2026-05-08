namespace Spikit.Models;

// Una notificación de toast lista para mostrar. La key opcional sirve para deduplicación
// FIFO: si llega un toast con la misma key que uno ya visible (ej. dos "Sin audio detectado"
// seguidos), reemplaza al anterior reseteando el timer en vez de apilar — flows.md FLOW 5.
public sealed record ToastNotification(
    ToastSeverity Severity,
    string Title,
    string? Message,
    ToastAction? Action,
    TimeSpan AutoDismiss,
    string? DedupeKey = null);
