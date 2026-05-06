namespace Spikit.Services.Audio;

internal static class WavWriter
{
    private const int HeaderSize = 44;

    // Escribe un WAV PCM 16-bit completo (header + data) a partir de samples short[].
    // Formato: RIFF/WAVE/fmt chunk (16 bytes) + data chunk. Compatible con Whisper API.
    public static byte[] WriteWavFromPcm16(short[] samples, int sampleRate, int channels)
    {
        if (samples is null) throw new ArgumentNullException(nameof(samples));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels < 1 || channels > 2) throw new ArgumentOutOfRangeException(nameof(channels));

        const int bitsPerSample = 16;
        const int bytesPerSample = bitsPerSample / 8;

        int dataSize = samples.Length * bytesPerSample;
        int fileSize = HeaderSize + dataSize;
        int byteRate = sampleRate * channels * bytesPerSample;
        short blockAlign = (short)(channels * bytesPerSample);

        var buffer = new byte[fileSize];
        int p = 0;

        // RIFF header
        WriteAscii(buffer, ref p, "RIFF");
        WriteInt32(buffer, ref p, fileSize - 8);   // ChunkSize = total - 8
        WriteAscii(buffer, ref p, "WAVE");

        // fmt subchunk (PCM)
        WriteAscii(buffer, ref p, "fmt ");
        WriteInt32(buffer, ref p, 16);             // Subchunk1Size = 16 para PCM
        WriteInt16(buffer, ref p, 1);              // AudioFormat = 1 (PCM)
        WriteInt16(buffer, ref p, (short)channels);
        WriteInt32(buffer, ref p, sampleRate);
        WriteInt32(buffer, ref p, byteRate);
        WriteInt16(buffer, ref p, blockAlign);
        WriteInt16(buffer, ref p, bitsPerSample);

        // data subchunk
        WriteAscii(buffer, ref p, "data");
        WriteInt32(buffer, ref p, dataSize);

        // PCM samples (little-endian)
        for (int i = 0; i < samples.Length; i++)
        {
            short s = samples[i];
            buffer[p++] = (byte)(s & 0xFF);
            buffer[p++] = (byte)((s >> 8) & 0xFF);
        }

        return buffer;
    }

    private static void WriteAscii(byte[] buffer, ref int pos, string text)
    {
        for (int i = 0; i < text.Length; i++) buffer[pos++] = (byte)text[i];
    }

    private static void WriteInt32(byte[] buffer, ref int pos, int value)
    {
        buffer[pos++] = (byte)(value & 0xFF);
        buffer[pos++] = (byte)((value >> 8) & 0xFF);
        buffer[pos++] = (byte)((value >> 16) & 0xFF);
        buffer[pos++] = (byte)((value >> 24) & 0xFF);
    }

    private static void WriteInt16(byte[] buffer, ref int pos, short value)
    {
        buffer[pos++] = (byte)(value & 0xFF);
        buffer[pos++] = (byte)((value >> 8) & 0xFF);
    }
}
