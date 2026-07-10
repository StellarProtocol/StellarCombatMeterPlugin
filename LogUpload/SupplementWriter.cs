// P3: the tiny payload a non-elected party member sends instead of the full blob — own actor
// detail (the uniquely valuable part the server grafts via graftActorDetail) + identity.
// Body shape: { window: {startMs,endMs}, uploader: {localUid,masterScore}, actors: {<selfKey>: Actor} }.

using System.Collections.Generic;

namespace Stellar.CombatMeter.LogUpload;

internal static class SupplementWriter
{
    internal static string Write(CombatLog log)
    {
        var self = new Dictionary<string, Actor>();
        foreach (var kv in log.Actors)
            if (kv.Value.IsLocal) self[kv.Key] = kv.Value;

        var w = new JsonWriter();
        w.BeginObject();
        w.Name("window");
        w.BeginObject();
        w.Name("startMs").Number(log.Header.Encounter.StartMs);
        w.Name("endMs").Number(log.Header.Encounter.EndMs);
        w.EndObject();
        w.Name("uploader");
        w.BeginObject();
        w.Name("localUid").Number(log.Header.Uploader.LocalUid);
        w.Name("masterScore").Number(log.Header.Uploader.MasterScore);
        w.EndObject();
        w.Name("actors");
        CombatLogWriter.WriteActors(w, self);
        w.EndObject();
        return w.ToString();
    }
}
