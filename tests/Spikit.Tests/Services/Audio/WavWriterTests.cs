using System.Text;
using Spikit.Services.Audio;

namespace Spikit.Tests.Services.Audio;

public class WavWriterTests
{
    [Fact]
    public void Header_starts_with_RIFF_and_WAVE_markers()
    {
        var samples = new short[] { 0, 0, 0 };
        var wav = WavWriter.WriteWavFromPcm16(samples, 16_000, 1);

        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
    }

    [Fact]
    public void Total_size_equals_44_byte_header_plus_2_bytes_per_sample()
    {
        var samples = new short[100];
        var wav = WavWriter.WriteWavFromPcm16(samples, 16_000, 1);

        Assert.Equal(44 + 100 * 2, wav.Length);
    }

    [Fact]
    public void Fmt_chunk_encodes_pcm_16khz_mono_correctly()
    {
        var samples = new short[10];
        var wav = WavWriter.WriteWavFromPcm16(samples, 16_000, 1);

        // AudioFormat (offset 20) = 1 (PCM)
        Assert.Equal(1, BitConverter.ToInt16(wav, 20));
        // NumChannels (offset 22) = 1
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));
        // SampleRate (offset 24) = 16000
        Assert.Equal(16_000, BitConverter.ToInt32(wav, 24));
        // ByteRate (offset 28) = 16000 * 1 * 2
        Assert.Equal(32_000, BitConverter.ToInt32(wav, 28));
        // BlockAlign (offset 32) = 1 * 2
        Assert.Equal(2, BitConverter.ToInt16(wav, 32));
        // BitsPerSample (offset 34) = 16
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));
    }

    [Fact]
    public void Data_chunk_size_matches_actual_sample_bytes()
    {
        var samples = new short[200];
        var wav = WavWriter.WriteWavFromPcm16(samples, 16_000, 1);

        // data chunk size at offset 40
        Assert.Equal(200 * 2, BitConverter.ToInt32(wav, 40));
    }

    [Fact]
    public void Riff_chunk_size_matches_total_minus_8()
    {
        var samples = new short[50];
        var wav = WavWriter.WriteWavFromPcm16(samples, 16_000, 1);

        Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4));
    }

    [Fact]
    public void Samples_are_written_little_endian_after_header()
    {
        var samples = new short[] { 1, -1, 256 };
        var wav = WavWriter.WriteWavFromPcm16(samples, 16_000, 1);

        // 44-byte header, then samples: 1 → 0x01 0x00, -1 → 0xFF 0xFF, 256 → 0x00 0x01
        Assert.Equal(0x01, wav[44]);
        Assert.Equal(0x00, wav[45]);
        Assert.Equal(0xFF, wav[46]);
        Assert.Equal(0xFF, wav[47]);
        Assert.Equal(0x00, wav[48]);
        Assert.Equal(0x01, wav[49]);
    }

    [Fact]
    public void Throws_on_invalid_inputs()
    {
        Assert.Throws<ArgumentNullException>(() => WavWriter.WriteWavFromPcm16(null!, 16_000, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => WavWriter.WriteWavFromPcm16(new short[1], 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => WavWriter.WriteWavFromPcm16(new short[1], 16_000, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => WavWriter.WriteWavFromPcm16(new short[1], 16_000, 3));
    }
}
