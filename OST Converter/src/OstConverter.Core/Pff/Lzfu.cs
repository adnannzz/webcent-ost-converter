using System.Buffers.Binary;
using System.Text;

namespace OstConverter.Core.Pff;

/// <summary>Decompressor for the "compressed RTF" format (MS-OXRTFCP, LZFu / MELA).</summary>
public static class Lzfu
{
    internal const string Dictionary =
        "{\\rtf1\\ansi\\mac\\deff0\\deftab720{\\fonttbl;}" +
        "{\\f0\\fnil \\froman \\fswiss \\fmodern \\fscript \\fdecor MS Sans SerifSymbolArialTimes New RomanCourier" +
        "{\\colortbl\\red0\\green0\\blue0\r\n\\par \\pard\\plain\\f0\\fs20\\b\\i\\u\\tab\\tx";

    public static bool TryDecompress(ReadOnlySpan<byte> src, out byte[] result)
    {
        result = [];
        if (src.Length < 16) return false;
        uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(src[4..]);
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(src[8..]);
        if (rawSize > 256 * 1024 * 1024) return false;

        if (type == 0x414C454D) // 'MELA': stored uncompressed
        {
            result = src[16..].ToArray();
            return true;
        }
        if (type != 0x75465A4C) return false; // 'LZFu'

        var dict = new byte[4096];
        Encoding.ASCII.GetBytes(Dictionary).CopyTo(dict, 0);
        int w = Dictionary.Length;
        var output = new byte[rawSize];
        int o = 0, i = 16;
        while (i < src.Length && o < output.Length)
        {
            byte control = src[i++];
            for (int bit = 0; bit < 8 && o < output.Length; bit++)
            {
                if ((control & (1 << bit)) == 0)
                {
                    if (i >= src.Length) break;
                    byte b = src[i++];
                    output[o++] = b;
                    dict[w] = b;
                    w = (w + 1) & 4095;
                }
                else
                {
                    if (i + 1 >= src.Length) { i = src.Length; break; }
                    int token = (src[i] << 8) | src[i + 1];
                    i += 2;
                    int offset = token >> 4, length = (token & 0xF) + 2;
                    if (offset == w) { result = output[..o]; return true; } // end marker
                    for (int k = 0; k < length && o < output.Length; k++)
                    {
                        byte b = dict[(offset + k) & 4095];
                        output[o++] = b;
                        dict[w] = b;
                        w = (w + 1) & 4095;
                    }
                }
            }
        }
        result = output[..o];
        return true;
    }
}
