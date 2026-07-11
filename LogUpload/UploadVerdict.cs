// P2 (multi-uploader courtesy): the /upload response's merge verdict. `Kept=false` means this
// upload lost the server-side merge — its logId is not a segment's blob, so chunk uploads would
// all 400 ("unknown-log"); `HavePositions=true` means the matched segment already has a positions
// doc. Absent fields (old server) default to today's behavior: send everything.

using System.Text.RegularExpressions;

namespace Stellar.CombatMeter.LogUpload;

internal sealed record UploadVerdict(bool Kept, bool HavePositions)
{
    private static readonly Regex KeptFalse = new("\"kept\"\\s*:\\s*false", RegexOptions.Compiled);
    private static readonly Regex HavePosTrue = new("\"havePositions\"\\s*:\\s*true", RegexOptions.Compiled);

    internal static UploadVerdict Parse(string? body)
    {
        if (string.IsNullOrEmpty(body)) return new UploadVerdict(true, false);
        return new UploadVerdict(
            Kept: !KeptFalse.IsMatch(body),
            HavePositions: HavePosTrue.IsMatch(body));
    }
}
