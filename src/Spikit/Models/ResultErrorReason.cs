namespace Spikit.Models;

// Motivo por el que el FloatingResultWindow se abre. Cada valor mapea a una variante
// visual definida en docs/design-system.md §10.4 + flows.md FLOW 5 (V1-V6).
//
// El orchestrator decide cuál disparar según la combinación de InsertionResult +
// TranscriptionException.StatusCode + texto disponible.
public enum ResultErrorReason
{
    // V1: paste falló (TargetGone o Failed) y hay texto que el usuario puede copiar.
    PasteFailed,

    // V2: paste falló sin texto recuperable (caso defensivo, hoy no se dispara desde
    // el orchestrator real porque CB-4/CB-8 cortan antes — incluido para completitud).
    PasteFailedNoText,

    // V3: TranscriptionException con StatusCode 401 (key inválida o expirada).
    AuthFailed,

    // V4: TranscriptionException con StatusCode 5xx o errores de red. Sin texto
    // (audio descartado por RN-1, sin reintentar transcripción — decisión EP-6.5).
    ServerError,

    // V5: TranscriptionException con StatusCode 429 (rate limit del provider).
    RateLimit,

    // V6: Whisper devolvió 200 con texto vacío/whitespace. Migrado de toast a
    // FloatingResult por decisión EP-6.5 (alineado con resto de errores del provider).
    EmptyResult,
}
