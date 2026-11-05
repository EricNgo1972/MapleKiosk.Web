// Vendored from the monorepo: MapleKiosk/Infrastructure/VoiceAgent/SPC.Infrastructure.VoiceAgent/Twilio/G711.cs
// Kept as close to the original as possible (it is tuned); only the namespace was changed.

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// G.711 μ-law, the codec every telephone stream still speaks. Twilio Media Streams carry 8 kHz mono μ-law
/// in both directions and nothing else, so the phone side of the relay transcodes at this seam.
///
/// <para>Hand-rolled because it is forty lines and the alternative is an audio SDK for one table lookup.
/// The encoder is the classic segment/mantissa algorithm from the ITU reference; the decoder is its exact
/// inverse, precomputed for all 256 codes.</para>
/// </summary>
internal static class G711
{
    private const int Bias = 0x84;
    private const int Clip = 32635;

    private static readonly short[] MuLawToPcmTable = BuildDecodeTable();

    /// <summary>One 16-bit signed sample → one μ-law byte.</summary>
    public static byte EncodeMuLaw(short sample)
    {
        int pcm = sample;
        var sign = (pcm >> 8) & 0x80;
        if (sign != 0) pcm = -pcm;
        if (pcm > Clip) pcm = Clip;
        pcm += Bias;

        var exponent = 7;
        for (var mask = 0x4000; (pcm & mask) == 0 && exponent > 0; exponent--, mask >>= 1) { }

        var mantissa = (pcm >> (exponent + 3)) & 0x0F;
        return (byte)~(sign | (exponent << 4) | mantissa);
    }

    /// <summary>One μ-law byte → one 16-bit signed sample.</summary>
    public static short DecodeMuLaw(byte code) => MuLawToPcmTable[code];

    /// <summary>16-bit little-endian mono PCM → μ-law bytes, one per sample.</summary>
    public static byte[] EncodeMuLaw(ReadOnlySpan<byte> pcm16)
    {
        var samples = pcm16.Length / 2;
        var result = new byte[samples];

        for (var i = 0; i < samples; i++)
            result[i] = EncodeMuLaw((short)(pcm16[2 * i] | (pcm16[2 * i + 1] << 8)));

        return result;
    }

    /// <summary>μ-law bytes → 16-bit little-endian mono PCM, two bytes per code.</summary>
    public static byte[] DecodeMuLaw(ReadOnlySpan<byte> mulaw)
    {
        var result = new byte[mulaw.Length * 2];

        for (var i = 0; i < mulaw.Length; i++)
        {
            var sample = MuLawToPcmTable[mulaw[i]];
            result[2 * i] = (byte)sample;
            result[2 * i + 1] = (byte)(sample >> 8);
        }

        return result;
    }

    private static short[] BuildDecodeTable()
    {
        var table = new short[256];

        for (var i = 0; i < 256; i++)
        {
            var code = ~i;
            var sign = code & 0x80;
            var exponent = (code >> 4) & 0x07;
            var mantissa = code & 0x0F;

            var sample = (((mantissa << 3) + Bias) << exponent) - Bias;
            table[i] = (short)(sign != 0 ? -sample : sample);
        }

        return table;
    }
}
