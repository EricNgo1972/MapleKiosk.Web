// Vendored from the monorepo: MapleKiosk/Infrastructure/VoiceAgent/SPC.Infrastructure.VoiceAgent/Twilio/PcmResampler.cs
// Kept as close to the original as possible (it is tuned); only the namespace was changed.

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// Changes the sample rate of a 16-bit mono PCM stream, chunk by chunk, keeping its place between calls
/// so chunk boundaries do not click.
///
/// <para>Telephone audio is 8 kHz; the providers want 16 kHz in and hand back 24 kHz (Gemini) or whatever
/// was negotiated (Deepgram). Nothing above 4 kHz survives the phone line either way, so a plain linear
/// interpolator going up and a box average going down is all the fidelity there is to preserve — a
/// windowed-sinc would be a lot of code to sharpen what the trunk already rounded off.</para>
/// </summary>
internal sealed class PcmResampler
{
    private readonly int _from;
    private readonly int _to;

    /// <summary>Integer decimation factor when downsampling by a whole number; 0 otherwise.</summary>
    private readonly int _decimate;

    // Linear interpolation state: the last input sample of the previous chunk, and the fractional read
    // position (in input samples) carried past the end of it.
    private short _last;
    private bool _hasLast;
    private double _position;

    // Decimation state: a partial block carried over from the previous chunk.
    private int _blockSum;
    private int _blockCount;

    private PcmResampler(int from, int to)
    {
        _from = from;
        _to = to;
        _decimate = from > to && from % to == 0 ? from / to : 0;
    }

    /// <summary>Null when the rates already match — callers then pass audio straight through.</summary>
    public static PcmResampler? For(int fromRate, int toRate)
    {
        if (fromRate <= 0 || toRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(fromRate), "Sample rates must be positive.");

        return fromRate == toRate ? null : new PcmResampler(fromRate, toRate);
    }

    public int FromRate => _from;
    public int ToRate => _to;

    /// <summary>16-bit little-endian mono in, the same out at the target rate.</summary>
    public byte[] Resample(ReadOnlySpan<byte> pcm16)
    {
        var count = pcm16.Length / 2;
        if (count == 0) return Array.Empty<byte>();

        var samples = new short[count];
        for (var i = 0; i < count; i++)
            samples[i] = (short)(pcm16[2 * i] | (pcm16[2 * i + 1] << 8));

        var output = _decimate > 0 ? Decimate(samples) : Interpolate(samples);

        var bytes = new byte[output.Count * 2];
        for (var i = 0; i < output.Count; i++)
        {
            bytes[2 * i] = (byte)output[i];
            bytes[2 * i + 1] = (byte)(output[i] >> 8);
        }

        return bytes;
    }

    /// <summary>Averages every N input samples into one — a box low-pass, then decimation.</summary>
    private List<short> Decimate(short[] samples)
    {
        var output = new List<short>(samples.Length / _decimate + 1);

        foreach (var sample in samples)
        {
            _blockSum += sample;
            _blockCount++;

            if (_blockCount == _decimate)
            {
                output.Add((short)(_blockSum / _decimate));
                _blockSum = 0;
                _blockCount = 0;
            }
        }

        return output;
    }

    /// <summary>Linear interpolation for every other ratio, including all upsampling.</summary>
    private List<short> Interpolate(short[] samples)
    {
        // Prepend the previous chunk's tail so the first output samples interpolate across the boundary
        // instead of starting cold.
        var input = _hasLast ? Prepend(_last, samples) : samples;
        var step = (double)_from / _to;
        var output = new List<short>((int)(samples.Length / step) + 2);

        var pos = _position;
        while (pos < input.Length - 1)
        {
            var index = (int)pos;
            var frac = pos - index;
            output.Add((short)(input[index] + (input[index + 1] - input[index]) * frac));
            pos += step;
        }

        // Carry the overshoot relative to the sample that will be prepended next time.
        _position = pos - (input.Length - 1);
        _last = input[^1];
        _hasLast = true;

        return output;
    }

    private static short[] Prepend(short head, short[] rest)
    {
        var result = new short[rest.Length + 1];
        result[0] = head;
        rest.CopyTo(result, 1);
        return result;
    }
}
