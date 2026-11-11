using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Org.BouncyCastle.Crypto.Parameters;
using Stellar.CombatMeter.LogUpload;
using Stellar.CombatMeter.Replay;

namespace Stellar.CombatMeter;

// Keeps the archive's one-off costs out of the combat-event path (raid perf, 2026-11-10 raid log).
//
// The inline boss-phase cut (MaybeCutForBossPhase) runs ManualArchive INSIDE OnCombatEvent. The FIRST archive of
// the session measured combatSubsTop=CombatMeter.Plugin:161.9ms/34 events — later boss cuts in the same raid were
// 0.1–25 ms. That one-off is the cold path: first JIT of the summary assembler / JSON writers, and BouncyCastle's
// first ECDSA P-256 use (curve tables + point precomputation + every BC type it touches), all landing on the frame
// that opens the boss fight. Two fixes here:
//   1. Warm-up on a worker thread at plugin load: a throwaway-key sign round-trip warms BouncyCastle, and
//      RuntimeHelpers.PrepareMethod JITs the archive/upload writers. JIT and BC are process-wide, so the main
//      thread later finds them ready. Only PURE managed types are touched — nothing that references UnityEngine /
//      Il2Cpp, because the JIT may run a type's static constructor while compiling and an IL2CPP interop cctor
//      must never run on an unattached worker thread.
//   2. The per-run history file's disk write moves to an ordered background chain (see QueueHistoryWrite).
public sealed partial class Plugin
{
    // Pure-managed archive/upload types whose methods are JIT-prepared by the warm-up. Keep this list Unity-free.
    private static readonly Type[] ArchiveWarmTypes =
    {
        typeof(CombatLogAssembler), typeof(CombatLogWriter), typeof(JsonWriter), typeof(DerivedBuilder),
        typeof(DerivedBucketBuilder), typeof(EventsJsonWriter), typeof(ChunkCompressor), typeof(ReUploadContainer),
        typeof(LogSigner), typeof(PortraitReport), typeof(HistoryStore), typeof(HistoryJsonWriter),
        typeof(HistoryContainer), typeof(PositionTrackAssembler), typeof(PositionJsonWriter),
    };

    private void StartArchiveWarmup()
    {
        var log = _services.Log;   // ILog is already used from thread-pool upload callbacks
        Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var priv = (ECPrivateKeyParameters)Es256.Generate().Private;
                var reimported = Es256.ImportPkcs8(Es256.ExportPkcs8(priv));   // the path LogSigner takes per sign
                Es256.SignP1363(reimported, new byte[] { 1, 2, 3 });
                Es256.ExportSpki(Es256.DerivePublic(reimported));
            }
            catch (Exception ex) { log.Warning($"[CombatMeter] archive warm-up (crypto) failed: {ex.Message}"); }

            var jitted = 0;
            foreach (var t in ArchiveWarmTypes) jitted += PrepareMethods(t);
            log.Info($"[CombatMeter] archive path warmed off-thread ({jitted} methods, {sw.ElapsedMilliseconds} ms)");
        });
    }

    // JIT every concrete, non-generic method/ctor declared on the type. A method that can't be prepared is skipped
    // (warm-up is best effort — it never changes behaviour, only when the JIT cost is paid).
    private static int PrepareMethods(Type t)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                 BindingFlags.Static | BindingFlags.DeclaredOnly;
        var n = 0;
        foreach (var m in t.GetMethods(all)) n += TryPrepare(m);
        foreach (var c in t.GetConstructors(all)) if (!c.IsStatic) n += TryPrepare(c);
        return n;
    }

    private static int TryPrepare(MethodBase m)
    {
        if (m.IsAbstract || m.ContainsGenericParameters) return 0;
        try { RuntimeHelpers.PrepareMethod(m.MethodHandle); return 1; }
        catch { return 0; }
    }

    // Ordered background writer for per-run history files. Serialising stays on the main thread (the entry is a
    // live, mutable object); only the disk write moves off it — the inline boss cut used to pay it inside the
    // combat-event batch. Writes are chained so a later re-persist of the same file (upload state settling in
    // PersistUploadStateIfDirty) can never be overtaken by an older write. IPluginData is already written from
    // worker threads (PersistReUpload, chunk spool), and a failed write only warns, as a failed sync write did.
    private readonly object _historyWriteLock = new();
    private Task _historyWriteChain = Task.CompletedTask;

    private void QueueHistoryWrite(string name, byte[] bytes) => QueueHistoryIo(name, bytes);

    // Deletes ride the same chain so an evicted run can never be re-created by a write still queued for it.
    private void QueueHistoryDelete(string name) => QueueHistoryIo(name, null);

    private void QueueHistoryIo(string name, byte[]? bytes)
    {
        var data = _services.Data;
        var log = _services.Log;
        lock (_historyWriteLock)
        {
            _historyWriteChain = _historyWriteChain.ContinueWith(_ =>
            {
                try { if (bytes is null) data.Delete(name); else data.Write(name, bytes); }
                catch (Exception ex) { log.Warning($"[CombatMeter] history file write/delete failed ({name}): {ex.Message}"); }
            }, TaskScheduler.Default);
        }
    }

    // Dispose: give queued history writes a moment to land (they are small) so quitting right after an archive
    // still persists it, as the old synchronous write did.
    private void FlushHistoryWrites()
    {
        Task pending;
        lock (_historyWriteLock) pending = _historyWriteChain;
        try { pending.Wait(2000); } catch { /* best effort */ }
    }
}
