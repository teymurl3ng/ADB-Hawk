#pragma warning disable CA1707 // Test method names use underscores by project convention.
#pragma warning disable CA2007 // Test project does not use ConfigureAwait.

namespace AdbView.Tests.Notifications;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Notifications;
using AdbView.State;
using Xunit;

/// <summary>Virtual clock + delay for notification timeline tests (no wall-clock sleeps).</summary>
internal sealed class VirtualClock {
    private readonly object _gate = new();
    private readonly List<(DateTimeOffset Due, TaskCompletionSource Tcs, CancellationToken Ct)> _waits = new();

    public VirtualClock(DateTimeOffset start) {
        Now = start;
    }

    public DateTimeOffset Now { get; private set; }

    public Task Delay(TimeSpan delay, CancellationToken ct) {
        if (delay <= TimeSpan.Zero) {
            return Task.CompletedTask;
        }

        // Synchronous continuations so Advance drains hold due-work before returning;
        // async continuations made negative asserts race ahead of Emit.
        var tcs = new TaskCompletionSource();
        DateTimeOffset due;
        lock (_gate) {
            due = Now.Add(delay);
            _waits.Add((due, tcs, ct));
        }

        if (ct.CanBeCanceled) {
            ct.Register(() => tcs.TrySetCanceled(ct));
        }

        return tcs.Task;
    }

    public void Advance(TimeSpan delta) {
        List<TaskCompletionSource> ready;
        lock (_gate) {
            Now = Now.Add(delta);
            ready = new List<TaskCompletionSource>();
            for (int i = _waits.Count - 1; i >= 0; i--) {
                (DateTimeOffset due, TaskCompletionSource tcs, CancellationToken ct) = _waits[i];
                if (ct.IsCancellationRequested) {
                    _waits.RemoveAt(i);
                    tcs.TrySetCanceled(ct);
                    continue;
                }

                if (due <= Now) {
                    _waits.RemoveAt(i);
                    ready.Add(tcs);
                }
            }
        }

        foreach (TaskCompletionSource tcs in ready) {
            tcs.TrySetResult();
        }
    }

    public async Task AdvanceAndFlushAsync(TimeSpan delta) {
        Advance(delta);
        await Task.Yield();
        await Task.Delay(1); // allow continuations to run
    }
}

internal static class NotificationFixtures {
    public static readonly DateTimeOffset T0 = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    public static AppSnapshot Empty(long revision = 1) =>
        StateEngine.CreateDefaultSnapshot(revision: revision) with { GeneratedAt = T0 };

    public static DeviceView Device(
        string id,
        string name,
        DeviceStateKind state,
        TransportKind transport = TransportKind.Usb,
        int? battery = null,
        string? exactModel = null,
        string? androidVersion = null) {
        string transportLabel = transport switch {
            TransportKind.Usb => Vocabulary.Device.TransportUsb,
            TransportKind.WiFi => Vocabulary.Device.TransportWifi,
            _ => Vocabulary.Device.TransportUnknown,
        };
        string stateLabel = state switch {
            DeviceStateKind.Connected => Vocabulary.Device.ConnectedStateLabel,
            DeviceStateKind.Unauthorized => Vocabulary.Device.UnauthorizedStateLabel,
            DeviceStateKind.Offline => Vocabulary.Device.OfflineStateLabel,
            _ => Vocabulary.Device.UnknownStateLabel,
        };
        return new DeviceView {
            Id = id,
            DisplayName = name,
            State = state,
            Severity = SeverityKind.Positive,
            StateLabel = stateLabel,
            StatusLine = stateLabel + " · " + transportLabel,
            Guidance = state == DeviceStateKind.Unauthorized
                ? Vocabulary.Device.UnauthorizedGuidance
                : state == DeviceStateKind.Offline
                    ? Vocabulary.Device.OfflineGuidance
                    : null,
            Transport = transport,
            TransportLabel = transportLabel,
            BatteryPercent = battery,
            MetaTemplate = Vocabulary.Device.ConnectedMetaTemplate,
            StateSince = T0,
            ConnectedSince = state == DeviceStateKind.Connected ? T0 : null,
            Detail = new DeviceDetailView {
                Serial = id.Contains(':', StringComparison.Ordinal)
                    ? id[(id.IndexOf(':', StringComparison.Ordinal) + 1)..]
                    : id,
                ExactModel = exactModel,
                AndroidVersion = androidVersion,
                Address = transport == TransportKind.WiFi ? "192.168.1.1:5555" : null,
                CopyFields = ImmutableArray<CopyFieldView>.Empty,
            },
        };
    }

    public static AppSnapshot WithDevices(this AppSnapshot snap, params DeviceView[] devices) =>
        snap with {
            Devices = devices.ToImmutableArray(),
            Revision = snap.Revision + 1,
            DeviceCountLabel = devices.Length >= 2 ? devices.Length + " devices" : null,
            Health = snap.Health with {
                Tray = devices.Any(d => d.State == DeviceStateKind.Connected)
                    ? TrayStatusKind.Connected
                    : snap.Health.Tray,
                TrayTooltip = devices.Length == 0
                    ? Vocabulary.Health.NoDevicesTrayTooltip
                    : "Connected: " + devices[0].DisplayName,
            },
        };

    public static AppSnapshot WithHealth(this AppSnapshot snap, GlobalHealthKind kind) =>
        snap with {
            Revision = snap.Revision + 1,
            Health = kind switch {
                GlobalHealthKind.AdbUnavailable => new HealthView {
                    Kind = kind,
                    Title = Vocabulary.Health.AdbUnavailableTitle,
                    Detail = Vocabulary.Health.AdbUnavailableDetail,
                    Hint = Vocabulary.Health.AdbUnavailableHint,
                    Tray = TrayStatusKind.Failure,
                    TrayTooltip = Vocabulary.Health.AdbUnavailableTrayTooltip,
                },
                GlobalHealthKind.AdbRestoring => new HealthView {
                    Kind = kind,
                    Title = Vocabulary.Health.AdbRestoringTitle,
                    Detail = Vocabulary.Health.AdbRestoringDetail,
                    Hint = null,
                    Tray = TrayStatusKind.Idle,
                    TrayTooltip = Vocabulary.Health.AdbRestoringTitle,
                },
                _ => new HealthView {
                    Kind = GlobalHealthKind.Healthy,
                    Title = null,
                    Detail = null,
                    Hint = null,
                    Tray = TrayStatusKind.Idle,
                    TrayTooltip = Vocabulary.Health.NoDevicesTrayTooltip,
                },
            },
        };

    public static AppSnapshot WithOffer(this AppSnapshot snap, string offerId, string name = "Phone") =>
        snap with {
            Revision = snap.Revision + 1,
            PairingOffers = ImmutableArray.Create(new PairingOfferView {
                Id = offerId,
                DisplayName = name,
                Heading = Vocabulary.Pairing.OfferHeading,
                Detail = Vocabulary.Pairing.OfferDetail,
                ActionLabel = Vocabulary.Pairing.ActionLabel,
            }),
        };

    public static AppSnapshot WithPairing(
        this AppSnapshot snap,
        string offerId,
        string name,
        PairingPhase phase) =>
        snap with {
            Revision = snap.Revision + 1,
            Pairing = new PairingOperationView {
                OfferId = offerId,
                DisplayName = name,
                Phase = phase,
                Title = phase == PairingPhase.PairedWaitingForDevice
                    ? Vocabulary.Pairing.SuccessTitle(name)
                    : phase == PairingPhase.Failed
                        ? Vocabulary.Pairing.FailedTitle
                        : Vocabulary.Pairing.ProgressTitle(name),
                Detail = phase == PairingPhase.PairedWaitingForDevice
                    ? Vocabulary.Pairing.SuccessDetail
                    : phase == PairingPhase.Failed
                        ? Vocabulary.Pairing.FailedDetail
                        : Vocabulary.Pairing.ProgressDetail,
                CanSubmitCode = false,
                CanCancel = true,
                CanRetry = phase == PairingPhase.Failed,
                CodeLength = 6,
            },
        };

    public static AppSnapshot WithWarning(this AppSnapshot snap) =>
        snap with {
            Revision = snap.Revision + 1,
            Warnings = ImmutableArray.Create(new SubsystemWarningView {
                Subsystem = SubsystemKind.WirelessDiscovery,
                Severity = SeverityKind.Attention,
                Title = Vocabulary.Wireless.UnavailableTitle,
                Detail = Vocabulary.Wireless.UnavailableDetail,
            }),
        };
}

public sealed class NotificationTimelineTests {
    private static async Task<(NotificationCoordinator Coord, FakeNotificationSink Sink, VirtualClock Clock)>
        CreateAfterBaselineAsync(AppSnapshot? baseline = null) {
        var clock = new VirtualClock(NotificationFixtures.T0);
        var sink = new FakeNotificationSink();
        var coord = new NotificationCoordinator(() => clock.Now, clock.Delay, sink);
        coord.AcceptSnapshot(baseline ?? NotificationFixtures.Empty());
        await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(5)); // exit startup suppress
        sink.Clear();
        return (coord, sink, clock);
    }

    private static async Task WaitUntilAsync(Func<bool> pred, int ms = 2000) {
        var start = Environment.TickCount64;
        while (!pred()) {
            if (Environment.TickCount64 - start > ms) {
                throw new TimeoutException("condition not met");
            }

            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task A_B_Startup_ThreeDevices_Zero() {
        var clock = new VirtualClock(NotificationFixtures.T0);
        var sink = new FakeNotificationSink();
        using var coord = new NotificationCoordinator(() => clock.Now, clock.Delay, sink);
        var three = NotificationFixtures.Empty().WithDevices(
            NotificationFixtures.Device("usb:1", "A", DeviceStateKind.Connected),
            NotificationFixtures.Device("usb:2", "B", DeviceStateKind.Connected),
            NotificationFixtures.Device("wifi:3", "C", DeviceStateKind.Connected, TransportKind.WiFi));
        coord.AcceptSnapshot(three);
        await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(sink.Emitted);
    }

    [Fact]
    public async Task C_D_Connected_Emits_After_2_5s_Without_Later_Snapshot() {
        var (coord, sink, clock) = await CreateAfterBaselineAsync();
        using (coord) {
            coord.AcceptSnapshot(NotificationFixtures.Empty(2).WithDevices(
                NotificationFixtures.Device("usb:R8", "SM_A065F", DeviceStateKind.Connected)));
            Assert.Empty(sink.Emitted);
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2.4));
            Assert.Empty(sink.Emitted);
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(0.2));
            await WaitUntilAsync(() => sink.Emitted.Count >= 1);
            Assert.Single(sink.Emitted);
            Assert.Equal(NotificationKind.DeviceConnected, sink.Emitted[0].Key.Kind);
            Assert.Equal("usb:R8", sink.Emitted[0].Key.Subject);
            Assert.Equal("SM_A065F connected", sink.Emitted[0].Title);
            Assert.Equal("Connected over USB", sink.Emitted[0].Body);
        }
    }

    [Fact]
    public async Task E_Connected_Flap_Before_Hold_Zero() {
        var (coord, sink, clock) = await CreateAfterBaselineAsync();
        using (coord) {
            coord.AcceptSnapshot(NotificationFixtures.Empty(2).WithDevices(
                NotificationFixtures.Device("usb:R8", "Phone", DeviceStateKind.Connected)));
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(1));
            coord.AcceptSnapshot(NotificationFixtures.Empty(3)); // gone
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(sink.Emitted, i => i.Key.Kind == NotificationKind.DeviceConnected);
        }
    }

    [Fact]
    public async Task F_Disconnect_After_2_5s() {
        var baseline = NotificationFixtures.Empty().WithDevices(
            NotificationFixtures.Device("usb:R8", "Phone", DeviceStateKind.Connected));
        var (coord, sink, clock) = await CreateAfterBaselineAsync(baseline);
        using (coord) {
            coord.AcceptSnapshot(NotificationFixtures.Empty(2));
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2.5));
            await WaitUntilAsync(() => sink.Emitted.Any(i => i.Key.Kind == NotificationKind.DeviceDisconnected));
            Assert.Contains(sink.Emitted, i =>
                i.Key.Kind == NotificationKind.DeviceDisconnected
                && i.Title == "Phone disconnected");
        }
    }

    [Fact]
    public async Task G_Retained_Device_No_Disconnect() {
        // Accepted snapshots keep the device (tracker grace already applied upstream).
        var baseline = NotificationFixtures.Empty().WithDevices(
            NotificationFixtures.Device("usb:R8", "Phone", DeviceStateKind.Connected));
        var (coord, sink, clock) = await CreateAfterBaselineAsync(baseline);
        using (coord) {
            coord.AcceptSnapshot(baseline with { Revision = 2 }); // same device retained
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(sink.Emitted, i => i.Key.Kind == NotificationKind.DeviceDisconnected);
        }
    }

    [Fact]
    public async Task H_I_J_Unauthorized_Hold_Flap_Dedup() {
        var (coord, sink, clock) = await CreateAfterBaselineAsync();
        using (coord) {
            coord.AcceptSnapshot(NotificationFixtures.Empty(2).WithDevices(
                NotificationFixtures.Device("usb:R8", "Phone", DeviceStateKind.Unauthorized)));
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(1.0));
            Assert.Empty(sink.Emitted);
            // flap away
            coord.AcceptSnapshot(NotificationFixtures.Empty(3).WithDevices(
                NotificationFixtures.Device("usb:R8", "Phone", DeviceStateKind.Connected)));
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(3));
            Assert.DoesNotContain(sink.Emitted, i => i.Key.Kind == NotificationKind.AuthorizationRequired);

            sink.Clear();
            coord.AcceptSnapshot(NotificationFixtures.Empty(4).WithDevices(
                NotificationFixtures.Device("usb:R8", "Phone", DeviceStateKind.Unauthorized)));
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(1.5));
            await WaitUntilAsync(() => sink.Emitted.Count >= 1);
            Assert.Equal(NotificationKind.AuthorizationRequired, sink.Emitted[0].Key.Kind);
            Assert.Equal(Vocabulary.Device.UnauthorizedGuidance, sink.Emitted[0].Body);

            int count = sink.Emitted.Count;
            coord.AcceptSnapshot(NotificationFixtures.Empty(5).WithDevices(
                NotificationFixtures.Device("usb:R8", "Phone", DeviceStateKind.Unauthorized)));
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(count, sink.Emitted.Count); // no duplicate
        }
    }

    [Fact]
    public async Task K_L_Offline_DeviceProblem_Not_Global() {
        var (coord, sink, clock) = await CreateAfterBaselineAsync();
        using (coord) {
            coord.AcceptSnapshot(NotificationFixtures.Empty(2).WithDevices(
                NotificationFixtures.Device("usb:R8", "Phone", DeviceStateKind.Offline)));
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2.5));
            await WaitUntilAsync(() => sink.Emitted.Count >= 1);
            Assert.All(sink.Emitted, i => Assert.Equal(NotificationKind.DeviceProblem, i.Key.Kind));
            Assert.DoesNotContain(sink.Emitted, i => i.Key.Kind == NotificationKind.AdbUnavailable);
            Assert.Equal(Vocabulary.Device.OfflineGuidance, sink.Emitted[0].Body);
        }
    }

    [Fact]
    public async Task M_N_O_P_Q_AdbUnavailable_Hold_Recover_Once_LaterOutage() {
        var (coord, sink, clock) = await CreateAfterBaselineAsync();
        using (coord) {
            coord.AcceptSnapshot(NotificationFixtures.Empty(2).WithHealth(GlobalHealthKind.AdbUnavailable));
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(9));
            Assert.Empty(sink.Emitted);
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(1.1));
            await WaitUntilAsync(() => sink.Emitted.Count >= 1);
            Assert.Single(sink.Emitted);
            Assert.Equal(NotificationKind.AdbUnavailable, sink.Emitted[0].Key.Kind);

            int afterFirst = sink.Emitted.Count;
            coord.AcceptSnapshot(NotificationFixtures.Empty(3).WithHealth(GlobalHealthKind.AdbUnavailable));
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(afterFirst, sink.Emitted.Count);

            // recover before would-be hold on a fresh transition — first recover silently
            coord.AcceptSnapshot(NotificationFixtures.Empty(4).WithHealth(GlobalHealthKind.Healthy));
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(afterFirst, sink.Emitted.Count); // no restored toast

            // recover mid-hold
            sink.Clear();
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(61)); // clear dedup
            coord.AcceptSnapshot(NotificationFixtures.Empty(5).WithHealth(GlobalHealthKind.AdbUnavailable));
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(5));
            coord.AcceptSnapshot(NotificationFixtures.Empty(6).WithHealth(GlobalHealthKind.Healthy));
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(sink.Emitted);

            // later independent outage after dedup window
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(61));
            coord.AcceptSnapshot(NotificationFixtures.Empty(7).WithHealth(GlobalHealthKind.AdbUnavailable));
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => sink.Emitted.Count >= 1);
            Assert.Contains(sink.Emitted, i => i.Key.Kind == NotificationKind.AdbUnavailable);
        }
    }

    [Fact]
    public async Task R_S_T_PairingOffer_Cooldown() {
        var (coord, sink, clock) = await CreateAfterBaselineAsync();
        using (coord) {
            coord.AcceptSnapshot(NotificationFixtures.Empty(2).WithOffer("offer-1", "Galaxy"));
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(1.5));
            await WaitUntilAsync(() => sink.Emitted.Count >= 1);
            Assert.Equal(NotificationKind.PairingAvailable, sink.Emitted[0].Key.Kind);

            sink.Clear();
            coord.AcceptSnapshot(NotificationFixtures.Empty(3)); // withdraw
            coord.AcceptSnapshot(NotificationFixtures.Empty(4).WithOffer("offer-1", "Galaxy"));
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2));
            Assert.Empty(sink.Emitted); // 10 min cooldown

            await clock.AdvanceAndFlushAsync(TimeSpan.FromMinutes(10));
            coord.AcceptSnapshot(NotificationFixtures.Empty(5)); // withdraw
            coord.AcceptSnapshot(NotificationFixtures.Empty(6).WithOffer("offer-1", "Galaxy"));
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(1.5));
            await WaitUntilAsync(() => sink.Emitted.Count >= 1);
        }
    }

    [Fact]
    public async Task U_V_W_X_Y_AK_PairingResult_Immediate_Dedup_Copy() {
        var (coord, sink, clock) = await CreateAfterBaselineAsync();
        using (coord) {
            var awaiting = NotificationFixtures.Empty(2).WithPairing("o1", "Galaxy", PairingPhase.AwaitingCode);
            coord.AcceptSnapshot(awaiting);
            var success = awaiting.WithPairing("o1", "Galaxy", PairingPhase.PairedWaitingForDevice);
            coord.AcceptSnapshot(success);
            await WaitUntilAsync(() => sink.Emitted.Count >= 1);
            Assert.Equal(NotificationKind.PairingResult, sink.Emitted[0].Key.Kind);
            Assert.Equal(Vocabulary.Pairing.SuccessTitle("Galaxy"), sink.Emitted[0].Title);
            Assert.Equal(Vocabulary.Pairing.SuccessDetail, sink.Emitted[0].Body);
            Assert.DoesNotContain("Connected", sink.Emitted[0].Title, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Connected", sink.Emitted[0].Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("123456", sink.Emitted[0].Title, StringComparison.Ordinal);
            Assert.DoesNotContain("123456", sink.Emitted[0].Body, StringComparison.Ordinal);

            int n = sink.Emitted.Count;
            coord.AcceptSnapshot(success with { Revision = success.Revision + 1 });
            Assert.Equal(n, sink.Emitted.Count); // dedup / no re-entry

            // fail path
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(61));
            sink.Clear();
            var failFrom = NotificationFixtures.Empty(10).WithPairing("o2", "Galaxy", PairingPhase.Pairing);
            coord.AcceptSnapshot(failFrom);
            coord.AcceptSnapshot(failFrom.WithPairing("o2", "Galaxy", PairingPhase.Failed));
            await WaitUntilAsync(() => sink.Emitted.Count >= 1);
            Assert.Equal(Vocabulary.Pairing.FailedTitle, sink.Emitted[0].Title);
            Assert.Equal(Vocabulary.Pairing.FailedDetail, sink.Emitted[0].Body);

            // cause-selected body (decision 0007): timeout detail flows to the notification
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(61));
            sink.Clear();
            var timedOut = NotificationFixtures.Empty(30).WithPairing("o3", "Galaxy", PairingPhase.Pairing);
            var timedOutFailed = timedOut with {
                Pairing = timedOut.Pairing! with {
                    Phase = PairingPhase.Failed,
                    Detail = Vocabulary.Pairing.FailedDetailFor(AdbView.Adb.Model.PairingFailureKind.Timeout),
                },
            };
            coord.AcceptSnapshot(timedOut);
            coord.AcceptSnapshot(timedOutFailed);
            await WaitUntilAsync(() => sink.Emitted.Count >= 1);
            Assert.Equal(
                Vocabulary.Pairing.FailedDetailFor(AdbView.Adb.Model.PairingFailureKind.Timeout),
                sink.Emitted[0].Body);

            // PairingResult bypasses burst: flood devices then pairing still emits
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(61));
            sink.Clear();
            var many = NotificationFixtures.Empty(20);
            for (int i = 0; i < 5; i++) {
                many = many.WithDevices(
                    Enumerable.Range(0, i + 1)
                        .Select(k => NotificationFixtures.Device("usb:" + k, "D" + k, DeviceStateKind.Connected))
                        .ToArray());
                coord.AcceptSnapshot(many);
            }

            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2.5));
            await WaitUntilAsync(() => sink.Emitted.Count >= 1);
            int beforePair = sink.Emitted.Count;
            coord.AcceptSnapshot(many.WithPairing("oBurst", "X", PairingPhase.Pairing));
            coord.AcceptSnapshot(many.WithPairing("oBurst", "X", PairingPhase.PairedWaitingForDevice));
            await WaitUntilAsync(() => sink.Emitted.Count > beforePair);
            Assert.Contains(sink.Emitted, i => i.Key.Kind == NotificationKind.PairingResult);
        }
    }

    [Fact]
    public async Task Z_AA_Metadata_And_Wireless_Zero() {
        var connected = NotificationFixtures.Empty().WithDevices(
            NotificationFixtures.Device("usb:R8", "Phone", DeviceStateKind.Connected));
        var (coord, sink, clock) = await CreateAfterBaselineAsync(connected);
        using (coord) {
            var meta = connected.WithDevices(
                NotificationFixtures.Device(
                    "usb:R8",
                    "Phone",
                    DeviceStateKind.Connected,
                    battery: 99,
                    exactModel: "SM-A065F",
                    androidVersion: "16"));
            coord.AcceptSnapshot(meta);
            coord.AcceptSnapshot(meta.WithDevices(
                NotificationFixtures.Device(
                    "usb:R8",
                    "Phone",
                    DeviceStateKind.Connected,
                    battery: 100,
                    exactModel: "SM-A065F",
                    androidVersion: "16")));
            coord.AcceptSnapshot(meta.WithWarning());
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(15));
            Assert.Empty(sink.Emitted);
        }
    }

    [Fact]
    public async Task AB_Startup_Suppress_Absorbs_MidWindow_Connect() {
        var clock = new VirtualClock(NotificationFixtures.T0);
        var sink = new FakeNotificationSink();
        using var coord = new NotificationCoordinator(() => clock.Now, clock.Delay, sink);
        coord.AcceptSnapshot(NotificationFixtures.Empty());
        await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2));
        coord.AcceptSnapshot(NotificationFixtures.Empty(2).WithDevices(
            NotificationFixtures.Device("usb:1", "Phone", DeviceStateKind.Connected)));
        await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(20));
        Assert.Empty(sink.Emitted); // absorbed; not deferred
    }

    [Fact]
    public async Task AC_AD_Resume_Rebaseline_And_8s_Suppress() {
        var baseline = NotificationFixtures.Empty().WithDevices(
            NotificationFixtures.Device("usb:R8", "Phone", DeviceStateKind.Connected));
        var (coord, sink, clock) = await CreateAfterBaselineAsync(baseline);
        using (coord) {
            coord.OnSuspend();
            coord.RebaselineForResume(baseline with { Revision = 9 });
            // reconfirm same device after resume
            coord.AcceptSnapshot(baseline with { Revision = 10 });
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(7));
            Assert.Empty(sink.Emitted);
            // mid-suppress new device absorbed
            coord.AcceptSnapshot(baseline.WithDevices(
                NotificationFixtures.Device("usb:R8", "Phone", DeviceStateKind.Connected),
                NotificationFixtures.Device("usb:NEW", "Other", DeviceStateKind.Connected)));
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(20));
            Assert.Empty(sink.Emitted);
        }
    }

    [Fact]
    public async Task AE_Shutdown_Discards_Pending() {
        var (coord, sink, clock) = await CreateAfterBaselineAsync();
        using (coord) {
            coord.AcceptSnapshot(NotificationFixtures.Empty(2).WithDevices(
                NotificationFixtures.Device("usb:R8", "Phone", DeviceStateKind.Connected)));
            coord.BeginShutdown();
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(sink.Emitted);
        }
    }

    [Fact]
    public async Task AF_AG_AH_Burst_And_Ordering_IndependentIds() {
        var (coord, sink, clock) = await CreateAfterBaselineAsync();
        using (coord) {
            var devices = Enumerable.Range(0, 5)
                .Select(i => NotificationFixtures.Device(
                    i % 2 == 0 ? "usb:" + i : "wifi:" + i,
                    "D" + i,
                    DeviceStateKind.Connected,
                    i % 2 == 0 ? TransportKind.Usb : TransportKind.WiFi))
                .ToArray();
            coord.AcceptSnapshot(NotificationFixtures.Empty(2).WithDevices(devices));
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2.5));
            await WaitUntilAsync(() => sink.Emitted.Count >= 4);

            var connected = sink.Emitted.Where(i => i.Key.Kind == NotificationKind.DeviceConnected).ToList();
            var changed = sink.Emitted.Where(i => i.Key.Kind == NotificationKind.DevicesChanged).ToList();
            Assert.Equal(3, connected.Count);
            Assert.Single(changed);
            Assert.Equal("2 devices changed", changed[0].Title); // excess = 2

            var subjects = connected.Select(c => c.Key.Subject).ToList();
            Assert.Equal(subjects.OrderBy(s => s, StringComparer.Ordinal).ToList(), subjects);
        }
    }

    [Fact]
    public async Task AI_Semantic_Duplicate_Zero() {
        var snap = NotificationFixtures.Empty().WithDevices(
            NotificationFixtures.Device("usb:R8", "Phone", DeviceStateKind.Connected));
        var (coord, sink, clock) = await CreateAfterBaselineAsync(snap);
        using (coord) {
            coord.AcceptSnapshot(snap with { Revision = 2 });
            coord.AcceptSnapshot(snap with { Revision = 3 });
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(sink.Emitted);
        }
    }

    [Fact]
    public void AJ_Vocabulary_Parity() {
        Assert.Equal(Vocabulary.Device.UnauthorizedGuidance, Vocabulary.Notifications.AuthorizationRequiredBody);
        Assert.Equal(Vocabulary.Device.OfflineGuidance, Vocabulary.Notifications.DeviceProblemBody);
        Assert.Equal(Vocabulary.Pairing.SuccessDetail, Vocabulary.Notifications.PairingSuccessBody);
        Assert.Equal(Vocabulary.Pairing.FailedTitle, Vocabulary.Notifications.PairingFailedTitle);
        Assert.Equal(Vocabulary.Health.AdbUnavailableTitle, Vocabulary.Notifications.AdbUnavailableTitle);
        Assert.Equal(Vocabulary.Device.TransportUsb, "USB");
        Assert.Equal(Vocabulary.Device.TransportWifi, "Wi-Fi");
        Assert.Equal(
            Vocabulary.Pairing.SuccessTitle("Galaxy"),
            Vocabulary.Notifications.PairingSuccessTitle("Galaxy"));
    }

    [Fact]
    public async Task Sink_Exception_Does_Not_Corrupt_Further_Emits() {
        var (coord, sink, clock) = await CreateAfterBaselineAsync();
        using (coord) {
            sink.ThrowOnEmit = new InvalidOperationException("balloon failed");
            coord.AcceptSnapshot(NotificationFixtures.Empty(2).WithPairing("o", "P", PairingPhase.Pairing));
            coord.AcceptSnapshot(NotificationFixtures.Empty(3).WithPairing("o", "P", PairingPhase.Failed));
            await Task.Yield();
            sink.ThrowOnEmit = null;
            await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(61));
            coord.AcceptSnapshot(NotificationFixtures.Empty(4).WithPairing("o2", "P", PairingPhase.Pairing));
            coord.AcceptSnapshot(NotificationFixtures.Empty(5).WithPairing("o2", "P", PairingPhase.PairedWaitingForDevice));
            await WaitUntilAsync(() => sink.Emitted.Count >= 1);
        }
    }
}
