using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>Blob naming + gzip for the event spool. Names obey IPluginDataStore's one-slash rule.</summary>
internal static class SpoolCodec
{
    internal const string Prefix = "spool/";

    internal static string BlobName(string segmentId, string track, int index)
        => Prefix + segmentId + "-" + track + "-" + index.ToString("D3", CultureInfo.InvariantCulture) + ".gz";

    internal static byte[] Gzip(string utf8Json)
    {
        var raw = Encoding.UTF8.GetBytes(utf8Json);
        using var ms = new MemoryStream(raw.Length / 4 + 64);
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    internal static string Gunzip(byte[] gz)
    {
        using var src = new MemoryStream(gz);
        using var un = new GZipStream(src, CompressionMode.Decompress);
        using var dst = new MemoryStream(gz.Length * 6);
        un.CopyTo(dst);
        return Encoding.UTF8.GetString(dst.ToArray());
    }
}
