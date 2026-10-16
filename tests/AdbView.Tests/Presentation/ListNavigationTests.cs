namespace AdbView.Tests.Presentation;

using AdbView.Bridge;
using AdbView.Presentation;
using Xunit;

public sealed class ListNavigationTests {
    [Fact]
    public void BackResolvesBookmarkByIdentityDespiteInsertionAndReordering() {
        var navigation = new ListNavigation();
        navigation.Remember(new ListBookmark("b", 7, 1, "c", 2));
        ListBookmark restored = navigation.Resolve(["new", "c", "a", "b"]);
        Assert.Equal("b", restored.AnchorId);
        Assert.Equal("c", restored.FocusId);
        Assert.Equal(7, restored.Offset);
    }

    [Fact]
    public void RemovedBookmarksChooseNearestSurvivingOrdinalAndEmptyIsSafe() {
        var navigation = new ListNavigation();
        navigation.Remember(new ListBookmark("b", 3, 1, "c", 2));
        ListBookmark restored = navigation.Resolve(["a", "d"]);
        Assert.Equal("d", restored.AnchorId);
        Assert.Equal("d", restored.FocusId);
        Assert.Null(navigation.Resolve([]).FocusId);
        Assert.Null(navigation.Resolve([]).AnchorId);
    }

    [Fact]
    public void RemovedDeviceAndGlobalOutageInvalidateDetailsImmediately() {
        var navigation = new ListNavigation();
        var connected = FixtureSnapshotProvider.GetSnapshot("connected-usb");
        Assert.True(navigation.Open(connected.Devices[0].Id, connected));
        Assert.False(navigation.Reconcile(connected with { Revision = 2 }));
        Assert.True(navigation.Reconcile(FixtureSnapshotProvider.GetSnapshot("empty")));
        Assert.Null(navigation.DetailsId);
        Assert.True(navigation.Open(connected.Devices[0].Id, connected));
        Assert.True(navigation.Reconcile(FixtureSnapshotProvider.GetSnapshot("adb-unavailable") with { Devices = connected.Devices }));
        Assert.Null(navigation.DetailsId);
        Assert.False(navigation.Open("absent", connected));
    }
}
