namespace Spikit.Services.Audio;

internal static class PcmHelpers
{
    // Convierte un buffer PCM 16-bit little-endian (mono) en un short[].
    // bytesRecorded debe ser par; si llega impar (no debería) se ignora el último byte.
    public static short[] BytesToShorts(byte[] buffer, int bytesRecorded)
    {
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        if (bytesRecorded < 0 || bytesRecorded > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(bytesRecorded));

        var sampleCount = bytesRecorded / 2;
        var samples = new short[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            samples[i] = (short)(buffer[i * 2] | (buffer[i * 2 + 1] << 8));
        }
        return samples;
    }

    // RMS normalizado al rango [0, 1]. Útil para alimentar la waveform sin
    // exponer al consumer el rango de short.
    public static float Rms(ReadOnlySpan<short> samples)
    {
        if (samples.Length == 0) return 0f;

        double sumSquares = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            double s = samples[i] / 32768.0;
            sumSquares += s * s;
        }
        return (float)Math.Sqrt(sumSquares / samples.Length);
    }

    // Detecta si hay al menos un sample no-cero en el buffer crudo (16-bit LE PCM).
    // Mismo criterio que la POC EP-1 para distinguir Initializing → Recording.
    public static bool HasNonZeroSample(byte[] buffer, int bytesRecorded)
    {
        for (var i = 0; i + 1 < bytesRecorded; i += 2)
        {
            if (buffer[i] != 0 || buffer[i + 1] != 0) return true;
        }
        return false;
    }
}
