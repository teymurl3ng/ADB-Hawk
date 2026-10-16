namespace AdbView.State;

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using AdbView.Adb.Model;

/// <summary>
/// Effective discovery set (decision 0016, superseding the whole-set rule of 0008/0011):
/// instance-level union by <see cref="Adb.Model.RawMdnsService.DiscoveryKey"/> — adb rows win per
/// key, fallback rows fill only keys adb does not report. Key identity is exactly the offer/
/// binding identity, so the merge cannot create duplicate offers. Order is stable (adb rows
/// first, then novel fallback rows) so repeated merges are deterministic.
/// </summary>
internal static class MdnsEffectiveSet {
    public static ImmutableArray<RawMdnsService> Merge(
        ImmutableArray<RawMdnsService> adbServices,
        ImmutableArray<RawMdnsService> fallbackServices) {
        if (fallbackServices.IsDefaultOrEmpty) {
            return adbServices.IsDefault ? ImmutableArray<RawMdnsService>.Empty : adbServices;
        }

        if (adbServices.IsDefaultOrEmpty) {
            return fallbackServices;
        }

        var keys = new HashSet<string>(adbServices.Select(static service => service.DiscoveryKey));
        var merged = adbServices.ToBuilder();
        foreach (var row in fallbackServices) {
            if (keys.Add(row.DiscoveryKey)) {
                merged.Add(row);
            }
        }

        return merged.ToImmutable();
    }
}
