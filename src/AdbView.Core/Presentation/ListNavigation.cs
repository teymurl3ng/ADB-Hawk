namespace AdbView.Presentation;

using System.Collections.Immutable;
using AdbView.State;

internal sealed record ListBookmark(string? AnchorId, double Offset, int AnchorIndex, string? FocusId, int FocusIndex);

internal sealed class ListNavigation {
    public string? DetailsId { get; private set; }
    public ListBookmark Bookmark { get; private set; } = new(null, 0, 0, null, 0);

    public void Remember(ListBookmark bookmark) => Bookmark = bookmark;

    public bool Open(string id, AppSnapshot snapshot) {
        if (snapshot.Health.Kind != GlobalHealthKind.Healthy || !snapshot.Devices.Any(device => device.Id == id)) {
            return false;
        }
        DetailsId = id;
        return true;
    }

    public void Back() => DetailsId = null;

    public bool Reconcile(AppSnapshot snapshot) {
        if (DetailsId is not null && (snapshot.Health.Kind != GlobalHealthKind.Healthy
            || !snapshot.Devices.Any(device => device.Id == DetailsId))) {
            Back();
            return true;
        }
        return false;
    }

    public ListBookmark Resolve(ImmutableArray<string> ids) => Bookmark with {
        AnchorId = ResolveId(Bookmark.AnchorId, Bookmark.AnchorIndex, ids),
        FocusId = ResolveId(Bookmark.FocusId, Bookmark.FocusIndex, ids),
    };

    private static string? ResolveId(string? id, int index, ImmutableArray<string> ids) =>
        ids.IsEmpty ? null : id is not null && ids.Contains(id, StringComparer.Ordinal)
            ? id : ids[Math.Clamp(index, 0, ids.Length - 1)];
}
