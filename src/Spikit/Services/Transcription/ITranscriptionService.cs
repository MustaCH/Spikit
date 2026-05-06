namespace Spikit.Services.Transcription;

public interface ITranscriptionService
{
    // Manda el WAV al endpoint de transcripción y devuelve el texto.
    // Lanza TranscriptionException si la API responde con error HTTP.
    // Lanza OperationCanceledException si se cancela.
    Task<string> TranscribeAsync(byte[] wavData, CancellationToken ct);
}
