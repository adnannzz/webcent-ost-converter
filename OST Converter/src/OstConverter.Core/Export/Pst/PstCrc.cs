namespace OstConverter.Core.Export.Pst;

/// <summary>
/// The CRC-32 used by the header, page trailers and block trailers of a PST ([MS-PST] 5.3). It is the usual reflected
/// CRC-32 polynomial but with no initial or final inversion, so it can't be replaced by System.IO.Hashing.Crc32.
/// </summary>
public static class PstCrc
{
    static readonly uint[] Table = Build();

    static uint[] Build()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Compute(ReadOnlySpan<byte> data, uint crc = 0)
    {
        foreach (var b in data) crc = Table[(byte)(crc ^ b)] ^ (crc >> 8);
        return crc;
    }
}
