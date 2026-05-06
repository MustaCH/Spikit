using Spikit.Services.Audio;

namespace Spikit.Tests.Services.Audio;

public class PcmHelpersTests
{
    [Fact]
    public void BytesToShorts_decodes_little_endian_pairs()
    {
        // 0x0100 LE = 1, 0x00FF LE = -256, 0xFFFF LE = -1
        var buffer = new byte[] { 0x01, 0x00, 0x00, 0xFF, 0xFF, 0xFF };

        var samples = PcmHelpers.BytesToShorts(buffer, buffer.Length);

        Assert.Equal(new short[] { 1, -256, -1 }, samples);
    }

    [Fact]
    public void BytesToShorts_respects_bytesRecorded_below_buffer_length()
    {
        var buffer = new byte[] { 0x0A, 0x00, 0x14, 0x00, 0x99, 0x99 };

        var samples = PcmHelpers.BytesToShorts(buffer, 4);

        Assert.Equal(new short[] { 10, 20 }, samples);
    }

    [Fact]
    public void BytesToShorts_throws_on_invalid_bytesRecorded()
    {
        var buffer = new byte[] { 0, 0 };
        Assert.Throws<ArgumentOutOfRangeException>(() => PcmHelpers.BytesToShorts(buffer, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PcmHelpers.BytesToShorts(buffer, 99));
    }

    [Fact]
    public void Rms_is_zero_for_silence()
    {
        var silence = new short[480];
        Assert.Equal(0f, PcmHelpers.Rms(silence));
    }

    [Fact]
    public void Rms_of_full_scale_dc_is_one()
    {
        // Sample fijo en max positive (32767) ~ 1.0 normalizado.
        var samples = Enumerable.Repeat(short.MaxValue, 100).ToArray();
        Assert.InRange(PcmHelpers.Rms(samples), 0.999f, 1.001f);
    }

    [Fact]
    public void Rms_of_alternating_full_scale_is_about_one()
    {
        // ±MaxValue alternados → RMS = MaxValue / 32768 ≈ 1.
        var samples = new short[1000];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = i % 2 == 0 ? short.MaxValue : (short)-short.MaxValue;
        }
        var rms = PcmHelpers.Rms(samples);
        Assert.InRange(rms, 0.999f, 1.001f);
    }

    [Fact]
    public void Rms_of_empty_span_is_zero()
    {
        Assert.Equal(0f, PcmHelpers.Rms(ReadOnlySpan<short>.Empty));
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 0, 0 }, 4, false)]
    [InlineData(new byte[] { 0, 0, 0x01, 0 }, 4, true)]
    [InlineData(new byte[] { 0, 0, 0, 0x80 }, 4, true)]
    [InlineData(new byte[] { 0, 0, 0, 0 }, 0, false)]
    public void HasNonZeroSample_detects_first_meaningful_byte(byte[] buf, int len, bool expected)
    {
        Assert.Equal(expected, PcmHelpers.HasNonZeroSample(buf, len));
    }
}
