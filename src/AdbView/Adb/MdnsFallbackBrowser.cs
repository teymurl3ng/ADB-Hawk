namespace AdbView.Adb;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb.Model;
using Serilog;

internal sealed record MdnsFallbackInstance {
    public required string ServiceType { get; init; }   // "_adb-tls-connect._tcp"
    public required string InstanceName { get; init; }  // first label of the instance FQDN
    public int Port { get; init; }
    public string? Ipv4 { get; init; }
    public string? Hostname { get; init; }              // SRV target (A records attach by this)
    public string? ProductModel { get; init; }          // TXT "name"
    public string? BuildVersionSdkFull { get; init; }   // TXT "api"
    public required DateTimeOffset ExpiresAt { get; init; }

    internal string Key => ServiceType + "\u001f" + InstanceName;
}

/// <summary>
/// First-party DNS-SD fallback browser (decision 0008 follow-through,
/// research/2026-10-01-dnssd-fallback-browser.md). Dual-path: a passive shared-bind listener on
/// 5353 (joined to 224.0.0.251 per interface; tolerated to fail — e.g. declined firewall prompt —
/// leaving active-only mode) plus active ephemeral-port legacy queries. Emits the full soft-state
/// instance list on every change; the ADB server's stream stays authoritative upstream. The
/// sweep/requery pass below is the single TTL maintenance loop sanctioned for this subsystem.
/// </summary>
internal sealed class MdnsFallbackBrowser {
    internal static readonly TimeSpan LoopSlice = TimeSpan.FromMilliseconds(200);
    internal static readonly TimeSpan RequeryFloor = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan ExpiryFloor = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan ExpiryCeiling = TimeSpan.FromMinutes(75);
    internal static readonly TimeSpan RequeryDelay = TimeSpan.FromSeconds(1); // RFC 6762: ≥2 queries ~1 s apart

    internal static readonly string[] ServiceDomains = [
        "_adb-tls-connect._tcp.local",
        "_adb-tls-pairing._tcp.local",
        "_adb._tcp.local",
    ];

    private static readonly IPEndPoint MulticastEndpoint = new(IPAddress.Parse("224.0.0.251"), 5353);
    private static readonly IPEndPoint SharedBindEndpoint = new(IPAddress.Any, 5353);

    private readonly IUdpSocketFactory _socketFactory;
    private readonly Func<IReadOnlyList<IPAddress>> _getInterfaceAddresses;
    private readonly Func<DateTimeOffset> _getUtcNow;
    private readonly Action<ImmutableArray<MdnsFallbackInstance>> _onInstances;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public MdnsFallbackBrowser(
        IUdpSocketFactory socketFactory,
        Func<IReadOnlyList<IPAddress>> getInterfaceAddresses,
        Func<DateTimeOffset> getUtcNow,
        Action<ImmutableArray<MdnsFallbackInstance>> onInstances,
        Func<TimeSpan, CancellationToken, Task>? delay = null) {
        _socketFactory = socketFactory;
        _getInterfaceAddresses = getInterfaceAddresses;
        _getUtcNow = getUtcNow;
        _onInstances = onInstances;
        _delay = delay ?? Task.Delay;
    }

    internal Dictionary<string, MdnsFallbackInstance> Instances { get; } =
        new(StringComparer.Ordinal);

    internal bool PassiveBound { get; private set; }

    /// <summary>Runs until cancelled; returns on shutdown or a fatal setup error.</summary>
    public async Task RunAsync(CancellationToken cancellationToken) {
        using var passive = _socketFactory.Create();
        PassiveBound = TryBindPassive(passive);

        using var active = _socketFactory.Create();
        try {
            active.Bind(new IPEndPoint(IPAddress.Any, 0), reuse: false);
        }
#pragma warning disable CA1031 // R-08: fatal setup failure is logged AND ends the session cleanly.
        catch (Exception ex) {
            Log.Warning(ex, "Fallback browser: ephemeral bind failed; session cannot run.");
            return;
        }
#pragma warning restore CA1031

        Instances.Clear();
        _nextRequeryAt = _getUtcNow(); // burst immediately on session start
        Log.Information("Fallback browser started (passive={Passive}).", PassiveBound);

        try {
            var buffer = new byte[1500];
            while (!cancellationToken.IsCancellationRequested) {
                var now = _getUtcNow();

                bool changed = ReceiveInto(passive, PassiveBound, buffer, now);
                changed |= ReceiveInto(active, true, buffer, now);
                changed |= SweepExpired(now);

                if (Interlocked.Exchange(ref _triggerPending, 0) == 1) {
                    Log.Information("Fallback browser: query burst triggered (QR session start).");
                    SendQueryBurst(active);
                }

                if (_nextRequeryAt is null || now >= _nextRequeryAt) {
                    SendQueryBurst(active);
                }

                if (changed) {
                    Publish();
                }

                await _delay(LoopSlice, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) {
            // Expected on shutdown or server-generation change.
        }
        finally {
            Instances.Clear();
            Publish();
            Log.Information("Fallback browser stopped.");
        }
    }

    private DateTimeOffset? _nextRequeryAt;

    private bool TryBindPassive(IUdpSocket socket) {
        try {
            socket.Bind(SharedBindEndpoint, reuse: true);
        }
#pragma warning disable CA1031 // R-08: bind failure is logged AND downgrades to active-only mode.
        catch (Exception ex) {
            // Shared 5353 exhausted (adb/msedge) or firewall-declined: active-only mode.
            Log.Information(
                ex,
                "Fallback browser: shared 5353 bind unavailable; running active-only (no passive announcements).");
            return false;
        }
#pragma warning restore CA1031

        bool joinedAny = false;
        foreach (IPAddress address in _getInterfaceAddresses()) {
            try {
                socket.JoinMulticastGroup(MulticastEndpoint.Address, address);
                joinedAny = true;
            }
#pragma warning disable CA1031 // R-08: per-interface join failure is logged and tolerated.
            catch (Exception ex) {
                Log.Debug(ex, "Fallback browser: multicast join failed on {Address}.", address);
            }
#pragma warning restore CA1031
        }

        if (!joinedAny) {
            Log.Information("Fallback browser: no multicast join succeeded; passive path inactive.");
        }

        return true;
    }

    private int _triggerPending;

    /// <summary>Schedules an immediate query burst (decision 0016, research
    /// 2026-10-03-fallback-discovery-requery-and-merge): a QR-session start must not wait out
    /// the empty-set requery floor while the phone advertises for only a short window.
    /// User-initiated and human-rate-limited; not a timer (R-05).</summary>
    public void TriggerQuery() => Interlocked.Exchange(ref _triggerPending, 1);

    private void SendQueryBurst(IUdpSocket active) {
        ushort messageId = (ushort)Environment.TickCount;
        foreach (string domain in ServiceDomains) {
            try {
                active.SendTo(DnsMessageCodec.BuildQuery(messageId, domain), MulticastEndpoint);
            }
#pragma warning disable CA1031 // R-08: per-domain send failure is logged; the loop continues.
            catch (Exception ex) {
                Log.Debug(ex, "Fallback browser: query send failed for {Domain}.", domain);
            }
#pragma warning restore CA1031
        }

        _nextRequeryAt = _getUtcNow().Add(ComputeRequeryDelay());
    }

    private TimeSpan ComputeRequeryDelay() {
        DateTimeOffset now = _getUtcNow();
        TimeSpan soonest = TimeSpan.MaxValue;
        foreach (MdnsFallbackInstance instance in Instances.Values) {
            // Requery around 80 % of each entry's remaining TTL (RFC 6762 §4.1 requery guidance).
            TimeSpan span = TimeSpan.FromTicks((instance.ExpiresAt - now).Ticks * 4 / 5);
            if (span < soonest) {
                soonest = span;
            }
        }

        if (soonest == TimeSpan.MaxValue) {
            return RequeryFloor;
        }

        return soonest < RequeryFloor ? RequeryFloor : soonest;
    }

    private bool ReceiveInto(IUdpSocket socket, bool usable, byte[] buffer, DateTimeOffset now) {
        if (!usable) {
            return false;
        }

        bool changed = false;
        for (int i = 0; i < 8; i++) { // drain without starving the loop
            int received;
            try {
                received = socket.Receive(buffer, (int)LoopSlice.TotalMilliseconds);
            }
#pragma warning disable CA1031 // R-08: receive failure is logged and ends this drain slice.
            catch (Exception ex) {
                Log.Debug(ex, "Fallback browser: receive failed.");
                break;
            }
#pragma warning restore CA1031

            if (received <= 0) {
                break;
            }

            changed |= Absorb(buffer.AsSpan(0, received), now);
        }

        return changed;
    }

    /// <summary>Absorbs one datagram; returns true when the instance map changed.</summary>
    internal bool Absorb(ReadOnlySpan<byte> datagram, DateTimeOffset now) {
        var records = new List<DnsMessageCodec.DnsRecord>();
        if (!DnsMessageCodec.TryCollectRecords(datagram, records)) {
            return false;
        }

        bool changed = false;
        foreach (var record in records) {
            switch (record.Type) {
                case DnsMessageCodec.TypePtr when record.PtrTarget is not null:
                    changed |= AbsorbPtr(record.PtrTarget, record.Ttl, now);
                    break;

                case DnsMessageCodec.TypeSrv when record.SrvTarget is not null:
                    if (TrySplitInstance(record.Name, out string? srvInstance, out string? srvType)) {
                        changed |= Touch(srvType!, srvInstance!, record.SrvPort ?? 0, record.Ttl, now, record.SrvTarget);
                    }

                    break;

                case DnsMessageCodec.TypeA when record.Address is not null:
                    changed |= AttachAddress(record.Name, record.Address, record.Ttl, now);
                    break;

                case DnsMessageCodec.TypeTxt when record.TxtPairs is not null:
                    changed |= AttachTxt(record.Name, record.TxtPairs, record.Ttl, now);
                    break;
            }
        }

        return changed;
    }

    private bool AbsorbPtr(string target, uint ttl, DateTimeOffset now) {
        if (!TrySplitInstance(target, out string? instance, out string? serviceType)
            || !IsTrackedService(serviceType!)) {
            return false;
        }

        string key = new MdnsFallbackInstance {
            ServiceType = serviceType!,
            InstanceName = instance!,
            ExpiresAt = now,
        }.Key;

        if (ttl == 0) {
            // RFC 6762 goodbye: remove if we know the instance; a lone goodbye is a no-op.
            if (Instances.Remove(key)) {
                _nextRequeryAt = _getUtcNow().Add(ComputeRequeryDelay());
                return true;
            }

            return false;
        }

        return Touch(serviceType!, instance!, port: 0, ttl, now);
    }

    private bool Touch(string serviceType, string instanceName, int port, uint ttl, DateTimeOffset now, string? hostname = null) {
        string key = new MdnsFallbackInstance {
            ServiceType = serviceType,
            InstanceName = instanceName,
            ExpiresAt = now,
        }.Key;
        DateTimeOffset expiresAt = ExpiryFor(now, ttl);

        if (Instances.TryGetValue(key, out MdnsFallbackInstance? existing)) {
            var updated = existing with {
                Port = port != 0 ? port : existing.Port,
                Hostname = hostname ?? existing.Hostname,
                ExpiresAt = expiresAt,
            };
            if (updated == existing) {
                return false;
            }

            Instances[key] = updated;
            return true;
        }

        Instances[key] = new MdnsFallbackInstance {
            ServiceType = serviceType,
            InstanceName = instanceName,
            Port = port,
            Hostname = hostname,
            ExpiresAt = expiresAt,
        };
        _nextRequeryAt = _getUtcNow().Add(ComputeRequeryDelay());
        return true;
    }

    private bool AttachAddress(string recordName, IPAddress address, uint ttl, DateTimeOffset now) {
        if (address.AddressFamily != AddressFamily.InterNetwork) {
            return false;
        }

        // A records are named by the SRV target hostname (typically "<guid>.local"), not by the
        // service instance FQDN — match on the hostname recorded from SRV (RFC 6763 §4.3/§12).
        MdnsFallbackInstance? entry = Instances.Values.FirstOrDefault(i =>
            (i.Hostname is not null && string.Equals(i.Hostname, recordName, StringComparison.OrdinalIgnoreCase))
            || string.Equals(
                i.InstanceName + "." + i.ServiceType,
                recordName,
                StringComparison.OrdinalIgnoreCase));
        if (entry is null) {
            return false;
        }

        string text = address.ToString();
        var updated = entry with { Ipv4 = text, ExpiresAt = ExpiryFor(now, ttl) };
        if (updated == entry) {
            return false;
        }

        Instances[entry.Key] = updated;
        return true;
    }

    private bool AttachTxt(string recordName, IReadOnlyList<KeyValuePair<string, string>> pairs, uint ttl, DateTimeOffset now) {
        if (!TrySplitInstance(recordName, out string? instance, out string? serviceType)
            || !Instances.TryGetValue(Key(serviceType!, instance!), out MdnsFallbackInstance? entry)) {
            return false;
        }

        string? name = null;
        string? api = null;
        foreach (var pair in pairs) {
            if (string.Equals(pair.Key, "name", StringComparison.Ordinal)) {
                name = pair.Value;
            }
            else if (string.Equals(pair.Key, "api", StringComparison.Ordinal)) {
                api = pair.Value;
            }
        }

        var updated = entry with {
            ProductModel = name ?? entry.ProductModel,
            BuildVersionSdkFull = api ?? entry.BuildVersionSdkFull,
            ExpiresAt = ExpiryFor(now, ttl),
        };
        if (updated == entry) {
            return false;
        }

        Instances[entry.Key] = updated;
        return true;
    }

    private static string Key(string serviceType, string instanceName) => serviceType + "\u001f" + instanceName;

    internal bool SweepExpired(DateTimeOffset now) {
        List<string>? expired = null;
        foreach (var pair in Instances) {
            if (pair.Value.ExpiresAt <= now) {
                (expired ??= new List<string>()).Add(pair.Key);
            }
        }

        if (expired is null) {
            return false;
        }

        foreach (string key in expired) {
            Instances.Remove(key);
        }

        return true;
    }

    private static DateTimeOffset ExpiryFor(DateTimeOffset now, uint ttlSeconds) {
        TimeSpan span = TimeSpan.FromSeconds(Math.Clamp(ttlSeconds, 0, uint.MaxValue));
        if (span < ExpiryFloor) {
            span = ExpiryFloor;
        }

        if (span > ExpiryCeiling) {
            span = ExpiryCeiling;
        }

        return now.Add(span);
    }

    private void Publish() {
        var snapshot = Instances.Values
            .OrderBy(i => i.ServiceType, StringComparer.Ordinal)
            .ThenBy(i => i.InstanceName, StringComparer.Ordinal)
            .ToImmutableArray();
        _onInstances(snapshot);
    }

    private static bool IsTrackedService(string serviceType) =>
        string.Equals(serviceType, "_adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase)
        || string.Equals(serviceType, "_adb-tls-pairing._tcp", StringComparison.OrdinalIgnoreCase)
        || string.Equals(serviceType, "_adb._tcp", StringComparison.OrdinalIgnoreCase);

    internal static bool TrySplitInstance(string fqdn, out string? instanceName, out string? serviceType) {
        instanceName = null;
        serviceType = null;
        if (string.IsNullOrEmpty(fqdn)) {
            return false;
        }

        // "<instance>.<service-type>.local" — instance labels never contain dots.
        string trimmed = fqdn.TrimEnd('.');
        int firstDot = trimmed.IndexOf('.', StringComparison.Ordinal);
        if (firstDot <= 0 || firstDot >= trimmed.Length - 1) {
            return false;
        }

        string remainder = trimmed[(firstDot + 1)..];
        if (!remainder.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        instanceName = trimmed[..firstDot];
        serviceType = remainder[..^".local".Length];
        return true;
    }
}
