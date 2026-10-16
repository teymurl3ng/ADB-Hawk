namespace AdbView.State;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using AdbView.Adb;
using AdbView.Adb.Model;

/// <summary>
/// Maps pairing-kind mDNS services to PairingOfferView (ENGINEERING.md §6.6, DESIGN.md §14).
/// </summary>
internal static class PairingOfferMapper {
    public static (ImmutableArray<PairingOfferView> Offers, ImmutableDictionary<string, PairingOfferBinding> Bindings)
        Map(ImmutableArray<RawMdnsService> services, ImmutableArray<DeviceView> devices = default) {
        if (services.IsDefaultOrEmpty) {
            return (ImmutableArray<PairingOfferView>.Empty, ImmutableDictionary<string, PairingOfferBinding>.Empty);
        }

        if (devices.IsDefault) {
            devices = ImmutableArray<DeviceView>.Empty;
        }

        var offers = ImmutableArray.CreateBuilder<PairingOfferView>();
        var bindings = ImmutableDictionary.CreateBuilder<string, PairingOfferBinding>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (RawMdnsService service in services) {
            if (service.Kind != MdnsServiceKind.Pairing
                || !string.Equals(service.ServiceType, MdnsServiceTypes.Pairing, StringComparison.Ordinal)) {
                continue;
            }

            if (!PairingEndpoint.TryFromPairingService(service, out PairingEndpoint? endpoint) || endpoint is null) {
                continue;
            }

            string id = service.DiscoveryKey;
            if (!seen.Add(id)) {
                continue;
            }

            string displayName = PairingDisplayName.Resolve(service, devices);
            offers.Add(new PairingOfferView {
                Id = id,
                DisplayName = displayName,
                Heading = Vocabulary.Pairing.OfferHeading,
                Detail = Vocabulary.Pairing.OfferDetail,
                ActionLabel = Vocabulary.Pairing.ActionLabel,
            });

            bindings[id] = new PairingOfferBinding(
                id,
                endpoint.Host,
                endpoint.Port,
                string.IsNullOrEmpty(service.Serial) ? null : service.Serial,
                displayName);
        }

        return (offers.ToImmutable(), bindings.ToImmutable());
    }

    /// <summary>Legacy overload for tests that only assert service-field priority without devices.</summary>
    internal static string ResolveDisplayName(RawMdnsService service) =>
        PairingDisplayName.Resolve(service, ImmutableArray<DeviceView>.Empty);

    internal static bool OffersSemanticallyEqual(
        ImmutableArray<PairingOfferView> left,
        ImmutableArray<PairingOfferView> right) {
        if (left.IsDefault) {
            left = ImmutableArray<PairingOfferView>.Empty;
        }

        if (right.IsDefault) {
            right = ImmutableArray<PairingOfferView>.Empty;
        }

        if (left.Length != right.Length) {
            return false;
        }

        for (int i = 0; i < left.Length; i++) {
            if (left[i].Id != right[i].Id
                || left[i].DisplayName != right[i].DisplayName
                || left[i].Heading != right[i].Heading
                || left[i].Detail != right[i].Detail
                || left[i].ActionLabel != right[i].ActionLabel) {
                return false;
            }
        }

        return true;
    }
}
