// 2.12.0: event chunks go on the wire BROTLI-COMPRESSED (owner 2026-09-22: "why gzip when we already
// use brotli?"). The logs api accepts `Content-Encoding: br` on the three chunk routes as of StellarLogs
// main d85d194c (live on api.stellarresonance.app and on dev) and stores the client's bytes AS RECEIVED —
// chunks were already stored brotli at rest, so compressing here removes the server's re-compression and
// shrinks the upload ~27x (measured on a real 759,194 B dmg chunk).
//
// This file holds ONLY the encoder + the two never-lose-an-upload fallbacks. It touches nothing in
// capture / archive / spool / the send filter: the same bytes are sent, in the same order, to the same
// endpoints — only the transfer encoding changes.

using System;
using System.Buffers;
using System.IO.Compression;
using System.Threading;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>
/// Brotli transfer-encoding for chunk POSTs (<see cref="ChunkUploader"/>), plus the two switches that
/// guarantee an upload is NEVER lost to compression:
/// <list type="number">
/// <item>the encoder throwing (the native <c>System.IO.Compression.Native</c> library missing or failing
/// under BepInEx's .NET 6 runtime on Wine) disables brotli for the rest of the process and sends the
/// chunk plain — <see cref="TryEncode"/>;</item>
/// <item>the server answering <b>415</b> (an api that predates the `br` chunk routes) makes the caller
/// resend that same chunk plain immediately and disables brotli for the rest of the process —
/// <see cref="DisableAfterUnsupported"/>. This is the compatibility switch; no capability endpoint and
/// no version negotiation is needed.</item>
/// </list>
/// Both log exactly ONE line per process (<see cref="Disable"/>'s latch) so a broken runtime cannot spam
/// the log once per chunk.
/// </summary>
internal static class ChunkCompressor
{
    /// <summary>The one transfer encoding these routes accept. `gzip` is a 415 there (the summary
    /// <c>/upload</c> route keeps its own gzip handling — a different path entirely).</summary>
    internal const string ContentEncoding = "br";

    /// <summary>Brotli quality. 9 is the knee for this payload: it reaches the same ~27x as q11 on event
    /// JSON at a fraction of the CPU, and it stays comfortably inside the server's "re-encode if the
    /// client's stream is less than 8x smaller than the raw JSON" guard.</summary>
    internal const int Quality = 9;

    /// <summary>Brotli window (log2 bytes). 22 = the 4 MiB maximum, which matters here: a chunk's events
    /// repeat their field names thousands of times, so a bigger window is most of the ratio.</summary>
    internal const int Window = 22;

    /// <summary>The encoder itself, as a swappable delegate — the TEST SEAM for the "native library is
    /// missing" path (<see cref="TryEncode"/>'s catch), which cannot be provoked by breaking the BCL.
    /// Production never reassigns it; <see cref="ResetForTests"/> restores it.</summary>
    internal static Func<byte[], byte[]> Encoder = Compress;

    private static int _off;      // 0 = brotli on; 1 = plain for the rest of this process
    private static int _logged;   // one line per process, whichever switch fired

    /// <summary>False once either fallback has fired — from then on every chunk goes plain.</summary>
    internal static bool Enabled => Volatile.Read(ref _off) == 0;

    /// <summary>Compresses <paramref name="raw"/>, or returns <c>null</c> to mean "send this plain".
    /// Never throws: an encoder fault is the FIRST never-lose-an-upload switch.</summary>
    internal static byte[]? TryEncode(byte[] raw, Action<string>? logWarn)
    {
        if (!Enabled) return null;
        try
        {
            return Encoder(raw);
        }
        catch (Exception ex)
        {
            Disable($"[CombatMeter.SP1] chunk brotli unavailable ({ex.GetType().Name}): sending plain", logWarn);
            return null;
        }
    }

    /// <summary>The SECOND never-lose-an-upload switch: the server answered 415, so it does not accept
    /// `br` on this route. The caller resends that chunk plain immediately (not as one of its retries) and
    /// every later chunk in this process goes plain.</summary>
    internal static void DisableAfterUnsupported(Action<string>? logWarn)
        => Disable(
            "[CombatMeter.SP1] chunk brotli not accepted by server (415): resending this chunk plain and sending plain for the rest of this session",
            logWarn);

    private static void Disable(string line, Action<string>? logWarn)
    {
        Volatile.Write(ref _off, 1);
        if (Interlocked.Exchange(ref _logged, 1) == 0) logWarn?.Invoke(line);
    }

    /// <summary>One-shot brotli into a pooled buffer — no streams, no intermediate <c>MemoryStream</c>,
    /// one exact-sized array handed back to the HTTP content. <c>TryCompress</c> returning false (the
    /// destination could not hold the result, which <see cref="BrotliEncoder.GetMaxCompressedLength"/>
    /// rules out) is treated as an encoder fault, i.e. it falls back to plain like any other failure.</summary>
    internal static byte[] Compress(byte[] raw)
    {
        var rented = ArrayPool<byte>.Shared.Rent(BrotliEncoder.GetMaxCompressedLength(raw.Length));
        try
        {
            if (!BrotliEncoder.TryCompress(raw, rented, out var written, Quality, Window))
                throw new InvalidOperationException("BrotliEncoder.TryCompress returned false");
            var packed = new byte[written];
            Buffer.BlockCopy(rented, 0, packed, 0, written);
            return packed;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Test seam: puts the process-wide latches and the encoder back to their boot state. The
    /// latches are deliberately process-wide in production (one broken runtime, one log line), so every
    /// test that exercises a fallback must restore them in a <c>finally</c>.</summary>
    internal static void ResetForTests()
    {
        Volatile.Write(ref _off, 0);
        Volatile.Write(ref _logged, 0);
        Encoder = Compress;
    }
}
