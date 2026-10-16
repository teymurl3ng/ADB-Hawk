namespace AdbView.Adb;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net.Sockets;

/// <summary>Record-level DNS-SD cache. Network I/O and the single maintenance scheduler stay
/// in MdnsFallbackBrowser; records can arrive in any packet order.</summary>
internal sealed class MdnsRecordCache {
    private sealed record CachedRecord(DnsMessageCodec.DnsRecord Record, DateTimeOffset ReceivedAt,
        DateTimeOffset ExpiresAt, DateTimeOffset NextRefreshAt, int RefreshAttempt);
    private sealed record PendingQuestion(int Attempts, DateTimeOffset NextAt);
    private readonly Dictionary<string, CachedRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Name, ushort Type), PendingQuestion> _questions = new();
    private const int RecordLimit = 4096;
    private bool _recordLimitLogged;

    public Dictionary<string, MdnsFallbackInstance> Instances { get; } = new(StringComparer.Ordinal);

    public void Clear() {
        _records.Clear();
        _questions.Clear();
        Instances.Clear();
        _recordLimitLogged = false;
    }

    public bool Absorb(IReadOnlyList<DnsMessageCodec.DnsRecord> records, DateTimeOffset now) {
        foreach (var incoming in records.Where(record => record.CacheFlush && record.Type is DnsMessageCodec.TypeA or DnsMessageCodec.TypeAaaa)) {
            foreach (var stale in _records.Where(pair => pair.Value.Record.Type == incoming.Type
                && Same(pair.Value.Record.Name, incoming.Name)
                && pair.Value.ReceivedAt < now.AddSeconds(-1)
                && !records.Any(record => RecordKey(record) == pair.Key)).Select(pair => pair.Key).ToArray()) {
                // RFC 6762 cache-flush grace permits other interfaces/packets to refresh the set.
                _records[stale] = _records[stale] with { ExpiresAt = now.AddSeconds(1) };
            }
        }
        foreach (var record in records) {
            bool serviceRecord = record.Type == DnsMessageCodec.TypePtr
                ? record.PtrTarget is not null && TrackedInstance(record.PtrTarget)
                : record.Type is DnsMessageCodec.TypeSrv or DnsMessageCodec.TypeTxt && TrackedInstance(record.Name);
            bool addressRecord = record.Type is DnsMessageCodec.TypeA or DnsMessageCodec.TypeAaaa
                && record.Address is not null && record.Name.TrimEnd('.').EndsWith(".local", StringComparison.OrdinalIgnoreCase);
            if (!serviceRecord && !addressRecord) {
                continue;
            }

            string key = RecordKey(record);
            if (record.Ttl == 0) {
                _records.Remove(key);
                if (record.Type is DnsMessageCodec.TypePtr or DnsMessageCodec.TypeSrv) {
                    string withdrawn = Canonical(record.PtrTarget ?? record.Name);
                    foreach (var stale in _records.Where(pair => Canonical(pair.Value.Record.Name) == withdrawn
                        || Canonical(pair.Value.Record.PtrTarget ?? "") == withdrawn).Select(pair => pair.Key).ToArray()) {
                        _records.Remove(stale);
                    }
                }
                continue;
            }

            if (_records.Count >= RecordLimit && !_records.ContainsKey(key)) {
                if (!_recordLimitLogged) {
                    Serilog.Log.Warning("Fallback discovery record limit reached; ignoring excess records.");
                    _recordLimitLogged = true;
                }
                continue;
            }
            double ttl = Math.Min(record.Ttl, 4500u);
            _records[key] = new CachedRecord(record, now, now.AddSeconds(ttl), now.AddSeconds(ttl * 0.8), 0);
        }
        return Rebuild(now);
    }

    public bool Sweep(DateTimeOffset now) {
        foreach (var expired in _records.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray()) {
            _records.Remove(expired);
        }
        return Rebuild(now);
    }

    private bool Rebuild(DateTimeOffset now) {
        var live = _records.Values.Where(record => record.ExpiresAt > now).ToArray();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in live) {
            string? name = item.Record.Type switch {
                DnsMessageCodec.TypePtr => item.Record.PtrTarget,
                DnsMessageCodec.TypeSrv => item.Record.Name,
                _ => null,
            };
            if (name is not null && TrackedInstance(name)) {
                names.TryAdd(Canonical(name), name);
            }
        }

        bool changed = false;
        var next = new Dictionary<string, MdnsFallbackInstance>(StringComparer.Ordinal);
        var required = new HashSet<(string Name, ushort Type)>();
        foreach (var name in names.Values) {
            MdnsFallbackBrowser.TrySplitInstance(name, out string? instance, out string? type);
            var ptr = live.FirstOrDefault(row => row.Record.Type == DnsMessageCodec.TypePtr
                && Same(row.Record.PtrTarget, name));
            var srv = live.FirstOrDefault(row => row.Record.Type == DnsMessageCodec.TypeSrv && Same(row.Record.Name, name));
            var txt = live.FirstOrDefault(row => row.Record.Type == DnsMessageCodec.TypeTxt && Same(row.Record.Name, name));
            var addresses = live.Where(row => row.Record.Address is not null && Same(row.Record.Name, srv?.Record.SrvTarget))
                .OrderByDescending(row => row.ReceivedAt).ToArray();
            string? ipv4 = addresses.FirstOrDefault(row => row.Record.Address!.AddressFamily == AddressFamily.InterNetwork)?.Record.Address?.ToString();
            var ipv6 = addresses.Where(row => row.Record.Address!.AddressFamily == AddressFamily.InterNetworkV6)
                .Select(row => row.Record.Address!.ToString()).Order(StringComparer.Ordinal).ToImmutableArray();
            string? model = txt?.Record.TxtPairs?.FirstOrDefault(pair => pair.Key == "name").Value;
            string? api = txt?.Record.TxtPairs?.FirstOrDefault(pair => pair.Key == "api").Value;
            DateTimeOffset expiry = ptr?.ExpiresAt ?? srv!.ExpiresAt;
            if (srv is not null && srv.ExpiresAt < expiry) {
                expiry = srv.ExpiresAt;
            }
            foreach (var address in addresses) {
                if (address.ExpiresAt < expiry) {
                    expiry = address.ExpiresAt;
                }
            }

            var entry = new MdnsFallbackInstance {
                InstanceName = instance!,
                ServiceType = CanonicalService(type!),
                Port = srv?.Record.SrvPort ?? 0,
                Hostname = srv?.Record.SrvTarget,
                Ipv4 = ipv4,
                Ipv6 = ipv6,
                ProductModel = model,
                BuildVersionSdkFull = api,
                ExpiresAt = expiry,
            };
            if (Instances.TryGetValue(entry.Key, out var previous) && previous.Ipv6.SequenceEqual(ipv6)) {
                entry = entry with { Ipv6 = previous.Ipv6 };
            }
            next[entry.Key] = entry;
            changed |= !Instances.TryGetValue(entry.Key, out previous) || previous != entry;

            if (srv is null) {
                required.Add((Canonical(name), DnsMessageCodec.TypeSrv));
            }
            if (txt is null) {
                required.Add((Canonical(name), DnsMessageCodec.TypeTxt));
            }
            if (srv?.Record.SrvTarget is string host && addresses.Length == 0) {
                required.Add((Canonical(host), DnsMessageCodec.TypeA));
                required.Add((Canonical(host), DnsMessageCodec.TypeAaaa));
            }
        }

        changed |= Instances.Keys.Any(key => !next.ContainsKey(key));
        Instances.Clear();
        foreach (var entry in next) {
            Instances.Add(entry.Key, entry.Value);
        }
        foreach (var obsolete in _questions.Keys.Where(key => !required.Contains(key)).ToArray()) {
            _questions.Remove(obsolete);
        }
        foreach (var question in required) {
            _questions.TryAdd(question, new PendingQuestion(0, now.AddSeconds(1)));
        }
        return changed;
    }

    public ImmutableArray<(string Name, ushort Type)> TakeDueQuestions(DateTimeOffset now) {
        var due = _questions.Where(pair => pair.Value.NextAt <= now).Select(pair => pair.Key).ToImmutableArray();
        foreach (var key in due) {
            var question = _questions[key];
            int attempts = Math.Min(question.Attempts + 1, 6);
            _questions[key] = new PendingQuestion(attempts, now.AddSeconds(Math.Min(1 << attempts, 60)));
        }
        return due;
    }

    public TimeSpan RefreshDelay(DateTimeOffset now) {
        TimeSpan delay = MdnsFallbackBrowser.RequeryFloor;
        bool found = false;
        foreach (var item in _records.Values) {
            if (item.Record.Type is DnsMessageCodec.TypeA or DnsMessageCodec.TypeAaaa
                && !Instances.Values.Any(instance => Same(instance.Hostname, item.Record.Name))) {
                continue;
            }
            TimeSpan remaining = item.NextRefreshAt - now;
            if (!found || remaining < delay) {
                delay = remaining;
                found = true;
            }
        }
        foreach (var question in _questions.Values) {
            if (question.NextAt - now < delay) {
                delay = question.NextAt - now;
            }
        }
        return delay < MdnsFallbackBrowser.LoopSlice ? MdnsFallbackBrowser.LoopSlice : delay;
    }

    public void MarkRefreshSent(DateTimeOffset now) {
        foreach (var key in _records.Keys.ToArray()) {
            var item = _records[key];
            if (item.NextRefreshAt > now) {
                continue;
            }
            int attempt = Math.Min(item.RefreshAttempt + 1, 4);
            double fraction = attempt < 4 ? 0.8 + attempt * 0.05 : 1;
            _records[key] = item with {
                RefreshAttempt = attempt,
                NextRefreshAt = item.ReceivedAt.AddTicks((long)((item.ExpiresAt - item.ReceivedAt).Ticks * fraction)),
            };
        }
    }

    private static string Canonical(string name) => name.TrimEnd('.').ToUpperInvariant();
    private static bool Same(string? first, string? second) => first is not null && second is not null
        && string.Equals(Canonical(first), Canonical(second), StringComparison.Ordinal);
    private static bool TrackedInstance(string name) => MdnsFallbackBrowser.TrySplitInstance(name, out _, out string? type)
        && (string.Equals(type, "_adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "_adb-tls-pairing._tcp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "_adb._tcp", StringComparison.OrdinalIgnoreCase));
    private static string CanonicalService(string type) => string.Equals(type, "_adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase)
        ? "_adb-tls-connect._tcp" : string.Equals(type, "_adb-tls-pairing._tcp", StringComparison.OrdinalIgnoreCase)
            ? "_adb-tls-pairing._tcp" : "_adb._tcp";
    private static string RecordKey(DnsMessageCodec.DnsRecord record) => Canonical(record.Name) + "\u001f" + record.Type
        + "\u001f" + (record.Type == DnsMessageCodec.TypePtr ? Canonical(record.PtrTarget!) : record.Address?.ToString() ?? "");
}
