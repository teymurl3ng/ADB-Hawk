#pragma warning disable CA1707
#pragma warning disable CA2007
#pragma warning disable xUnit1051

namespace AdbView.Tests.Notifications;

using System;
using System.Threading.Tasks;
using AdbView.Notifications;
using AdbView.State;
using Xunit;

/// <summary>Master Notifications preference on NotificationCoordinator.</summary>
public sealed class NotificationMasterSwitchTests {
    private sealed class FakeSink : INotificationSink {
        public int EmitCount { get; private set; }

        public void Emit(NotificationIntent intent) => EmitCount++;

        public void Clear() => EmitCount = 0;
    }

    private static (NotificationCoordinator Coord, FakeSink Sink, VirtualClock Clock) Create() {
        var sink = new FakeSink();
        var clock = new VirtualClock(NotificationFixtures.T0);
        var coord = new NotificationCoordinator(() => clock.Now, clock.Delay, sink);
        return (coord, sink, clock);
    }

    [Fact]
    public async Task On_Qualifying_Event_Reaches_Sink() {
        var (coord, sink, clock) = Create();
        await using (coord) {
            coord.AcceptSnapshot(NotificationFixtures.Empty());
            await clock.AdvanceAndFlushAsync(NotificationPolicy.StartupSuppress);
            sink.Clear();

            coord.AcceptSnapshot(
                NotificationFixtures.Empty().WithDevices(
                    NotificationFixtures.Device("d1", "Phone", DeviceStateKind.Connected)));
            await clock.AdvanceAndFlushAsync(NotificationPolicy.HoldConnected);
            Assert.True(sink.EmitCount >= 1);
        }
    }

    [Fact]
    public async Task Off_Same_Event_Zero_Sink_Calls() {
        var (coord, sink, clock) = Create();
        await using (coord) {
            coord.AcceptSnapshot(NotificationFixtures.Empty());
            await clock.AdvanceAndFlushAsync(NotificationPolicy.StartupSuppress);
            coord.SetNotificationsEnabled(false);
            sink.Clear();

            coord.AcceptSnapshot(
                NotificationFixtures.Empty().WithDevices(
                    NotificationFixtures.Device("d1", "Phone", DeviceStateKind.Connected)));
            await clock.AdvanceAndFlushAsync(NotificationPolicy.HoldConnected);
            Assert.Equal(0, sink.EmitCount);
        }
    }

    [Fact]
    public async Task Off_While_Pending_Drops_Hold() {
        var (coord, sink, clock) = Create();
        await using (coord) {
            coord.AcceptSnapshot(NotificationFixtures.Empty());
            await clock.AdvanceAndFlushAsync(NotificationPolicy.StartupSuppress);
            sink.Clear();

            coord.AcceptSnapshot(
                NotificationFixtures.Empty().WithDevices(
                    NotificationFixtures.Device("d1", "Phone", DeviceStateKind.Connected)));
            // Hold armed; disable before due.
            coord.SetNotificationsEnabled(false);
            await clock.AdvanceAndFlushAsync(NotificationPolicy.HoldConnected);
            Assert.Equal(0, sink.EmitCount);
        }
    }

    [Fact]
    public async Task Off_Events_Then_On_No_Backfill() {
        var (coord, sink, clock) = Create();
        await using (coord) {
            coord.AcceptSnapshot(NotificationFixtures.Empty());
            await clock.AdvanceAndFlushAsync(NotificationPolicy.StartupSuppress);
            coord.SetNotificationsEnabled(false);

            var withDevice = NotificationFixtures.Empty().WithDevices(
                NotificationFixtures.Device("d1", "Phone", DeviceStateKind.Connected));
            coord.AcceptSnapshot(withDevice);
            await clock.AdvanceAndFlushAsync(NotificationPolicy.HoldConnected);

            coord.SetNotificationsEnabled(true);
            sink.Clear();

            // Same roster again — rebaseline means no edge.
            coord.AcceptSnapshot(withDevice with { Revision = withDevice.Revision + 1 });
            await clock.AdvanceAndFlushAsync(NotificationPolicy.HoldConnected);
            Assert.Equal(0, sink.EmitCount);
        }
    }

    [Fact]
    public async Task On_After_Disable_Future_Event_Notifies() {
        var (coord, sink, clock) = Create();
        await using (coord) {
            coord.AcceptSnapshot(NotificationFixtures.Empty());
            await clock.AdvanceAndFlushAsync(NotificationPolicy.StartupSuppress);
            coord.SetNotificationsEnabled(false);
            coord.AcceptSnapshot(
                NotificationFixtures.Empty().WithDevices(
                    NotificationFixtures.Device("d1", "Phone", DeviceStateKind.Connected)));
            await clock.AdvanceAndFlushAsync(NotificationPolicy.HoldConnected);

            coord.SetNotificationsEnabled(true);
            sink.Clear();

            coord.AcceptSnapshot(
                NotificationFixtures.Empty().WithDevices(
                    NotificationFixtures.Device("d1", "Phone", DeviceStateKind.Connected),
                    NotificationFixtures.Device("d2", "Tablet", DeviceStateKind.Connected)));
            await clock.AdvanceAndFlushAsync(NotificationPolicy.HoldConnected);
            Assert.True(sink.EmitCount >= 1);
        }
    }

    [Fact]
    public async Task PairingResult_Obeys_Master_Off() {
        var (coord, sink, clock) = Create();
        await using (coord) {
            coord.AcceptSnapshot(NotificationFixtures.Empty());
            await clock.AdvanceAndFlushAsync(NotificationPolicy.StartupSuppress);
            coord.SetNotificationsEnabled(false);
            sink.Clear();

            var snap = NotificationFixtures.Empty() with {
                Revision = 2,
                Pairing = new PairingOperationView {
                    OfferId = "o1",
                    DisplayName = "Galaxy",
                    Phase = PairingPhase.PairedWaitingForDevice,
                    Title = Vocabulary.Notifications.PairingSuccessTitle("Galaxy"),
                    Detail = Vocabulary.Pairing.SuccessDetail,
                    CanSubmitCode = false,
                    CanCancel = false,
                    CanRetry = false,
                    CodeLength = 6,
                },
            };
            coord.AcceptSnapshot(snap);
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(0, sink.EmitCount);
        }
    }

    [Fact]
    public void Rapid_Toggle_No_Throw() {
        var (coord, sink, clock) = Create();
        using (coord) {
            coord.AcceptSnapshot(NotificationFixtures.Empty());
            for (int i = 0; i < 50; i++) {
                coord.SetNotificationsEnabled(i % 2 == 0);
            }

            Assert.False(coord.NotificationsEnabled);
            Assert.Equal(0, sink.EmitCount);
            _ = clock.Now;
        }
    }
}
