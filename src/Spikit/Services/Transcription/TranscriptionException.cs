using System.Net;

namespace Spikit.Services.Transcription;

public sealed class TranscriptionException : Exception
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
