using System.Text.Json.Nodes;

namespace Login.StandIn;

// standin-control 2.3: fake PCs. pc_id is the fake device_id/kid and must start with "standin-".
public sealed record StandInPc(string PcId, string Mode, string LocationSlug);

public static class Pcs
{
    public static readonly HashSet<string> Modes = new(StringComparer.Ordinal)
    { "valid", "missing", "bad_signature", "expired", "replayed", "unknown_device", "remote_session", "revoked_device", "other_location" };

    // [ASSUMPTION A16] pc_id is "standin-" followed by 1-64 characters from [A-Za-z0-9._-], so it is cookie-safe.
    public static bool ValidId(string id) =>
        id.StartsWith("standin-", StringComparison.Ordinal) && id.Length is > 8 and <= 72 &&
        id.Skip(8).All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}
