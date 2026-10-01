namespace AdbView.Host;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.State;
using Serilog;

/// <summary>
/// Owns the first-party DNS-SD fallback browser session lifetime against supervisor generation
/// (decision 0008 follow-through). Emits complete soft-state lists as
/// <see cref="MdnsFallbackReceivedEvent"/>; the reducer fills only the ADB server's gap.
/// </summary>
internal sealed class MdnsFallbackLifecycle : IDisposable {
    private readonly Func<MdnsFallbackBrowser> _browserFactory;
    private readonly Func<IReadOnlyList<IPAddress>> _interfaces;
    private readonly Action<AdbViewEvent> _post;
    private readonly Func<DateTimeOffset> _getUtcNow;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private MdnsFallbackBrowser? _browser;
    private int? _runningGeneration;
    private int _disposed;

    public MdnsFallbackLifecycle(
        Action<AdbViewEvent> post,
        Func<IReadOnlyList<IPAddress>> interfaces,
        IUdpSocketFactory? socketFactory = null,
        Func<DateTimeOffset>? getUtcNow = null) {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(interfaces);
        _post = post;
        _interfaces = interfaces;
        _getUtcNow = getUtcNow ?? (() => App.SystemClock.UtcNow);
        var factory = socketFactory ?? new WindowsUdpSocketFactory();
        _browserFactory = () => new MdnsFallbackBrowser(
            factory,
            _interfaces,
            _getUtcNow,
            OnInstances);
    }

    public int? RunningGeneration {
        get {
            lock (_gate) {
                return _runningGeneration;
            }
        }
    }

    public bool IsRunning {
        get {
            lock (_gate) {
                return _runTask is { IsCompleted: false };
            }
        }
    }

    public void OnServerStateChanged(AdbServerStateChanged state) {
        ArgumentNullException.ThrowIfNull(state);

        if (Volatile.Read(ref _disposed) != 0) {
            return;
        }

        if (state.Phase == AdbServerPhase.Up) {
            EnsureRunning(state.Generation);
        }
        else {
            Stop();
        }
    }

    public void EnsureRunning(int generation) {
        lock (_gate) {
            if (Volatile.Read(ref _disposed) != 0) {
                return;
            }

            if (_runningGeneration == generation && _runTask is { IsCompleted: false }) {
                return;
            }

            StopLocked();
            StartLocked(generation);
        }
    }

    public void Stop() {
        lock (_gate) {
            StopLocked();
        }
    }

    private void StartLocked(int generation) {
        var browser = _browserFactory();
        var cts = new CancellationTokenSource();

        _browser = browser;
        _cts = cts;
        _runningGeneration = generation;
        _runTask = Task.Run(() => browser.RunAsync(cts.Token));
        Log.Information("MdnsFallbackBrowser started for server generation {Generation}", generation);
    }

    private void StopLocked() {
        var cts = _cts;
        var browser = _browser;
        var task = _runTask;
        _cts = null;
        _browser = null;
        _runTask = null;
        _runningGeneration = null;

        if (cts is not null) {
            try {
                cts.Cancel();
            }
            finally {
                cts.Dispose();
            }
        }

        if (task is not null && !task.IsCompleted) {
            try {
                task.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) when (ex is OperationCanceledException or AggregateException) {
                // Expected on cancel.
            }
        }

        // The cancel path publishes an empty list from the browser's finally block; post one
        // authoritative empty list too in case the session never got to run.
        _post(new MdnsFallbackReceivedEvent(ImmutableArray<RawMdnsService>.Empty, _getUtcNow()));
    }

    private void OnInstances(ImmutableArray<MdnsFallbackInstance> instances) {
        _post(new MdnsFallbackReceivedEvent(MdnsFallbackMapper.Map(instances), _getUtcNow()));
    }

    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) {
            return;
        }

        Stop();
    }
}

/// <summary>Maps browser soft-state rows onto the tracker's RawMdnsService shape.</summary>
internal static class MdnsFallbackMapper {
    public static ImmutableArray<RawMdnsService> Map(ImmutableArray<MdnsFallbackInstance> instances) {
        if (instances.IsDefaultOrEmpty) {
            return ImmutableArray<RawMdnsService>.Empty;
        }

        return instances.Select(instance => new RawMdnsService {
            InstanceName = instance.InstanceName,
            ServiceType = instance.ServiceType,
            Kind = MdnsServiceTypes.Classify(instance.ServiceType),
            Ipv4 = instance.Ipv4,
            Port = instance.Port,
            Serial = ExtractSerialFromInstance(instance.InstanceName),
            ProductModel = instance.ProductModel,
            BuildVersionSdkFull = instance.BuildVersionSdkFull,
            Format = RawMdnsFormat.Fallback,
        }).ToImmutableArray();
    }

    /// <summary>Serial extraction delegates to the identity layer (AOSP instance formats).</summary>
    public static string? ExtractSerialFromInstance(string instanceName) =>
        DeviceIdentity.ExtractSerialFromInstanceName(instanceName);
}
