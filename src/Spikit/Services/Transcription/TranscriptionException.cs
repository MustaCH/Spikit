using System.Net;

namespace Spikit.Services.Transcription;

public class TranscriptionException : Exception
{
    public HttpStatusCode? StatusCode { get; }
    public string? ResponseBody { get; }

    public TranscriptionException(string message, HttpStatusCode? statusCode = null, string? responseBody = null)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public TranscriptionException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

// El proxy server devolvió 402 — el entitlement del user no autoriza la transcripción
// (típicamente tier=Expired o BYOK degradado). El orchestrator atrapa esto distinto a
// un TranscriptionException genérico: muestra toast con CTA "Upgrade to Pro" en lugar
// del FloatingResultWindow de errores transitorios.
public sealed class SubscriptionRequiredException : TranscriptionException
{
    public SubscriptionRequiredException(string message, string? responseBody = null)
        : base(message, HttpStatusCode.PaymentRequired, responseBody)
    {
    }
}
