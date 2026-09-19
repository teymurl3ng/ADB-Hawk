namespace AdbView.Tests.Fakes;

using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb;

internal static class FakeAdbMdns {
    public static async Task SendOkayAsync(Socket socket, CancellationToken ct = default) =>
        await FakeAdbTrackDevices.SendOkayAsync(socket, ct).ConfigureAwait(false);

    public static async Task SendFrameAsync(Socket socket, ReadOnlyMemory<byte> payload, CancellationToken ct = default) =>
        await FakeAdbTrackDevices.SendFrameAsync(socket, payload, ct).ConfigureAwait(false);

    public static async Task SendFailAsync(Socket socket, string reason, CancellationToken ct = default) =>
        await FakeAdbTrackDevices.SendFailAsync(socket, reason, ct).ConfigureAwait(false);

    public static byte[] EncodeMdnsService(
        string instance,
        string serviceType,
        int port,
        string? ipv4 = null,
        IEnumerable<string>? ipv6 = null,
        string? domain = null,
        string? productModel = null,
        string? buildVersionSdkFull = null,
        string? givenName = null,
        string? serial = null,
        string? mdnsServiceVersion = null,
        string? hostname = null) {
        var ms = new List<byte>();
        WriteString(ms, 1, instance);
        WriteString(ms, 2, serviceType);
        if (domain != null) {
            WriteString(ms, 3, domain);
        }

        if (ipv4 != null) {
            WriteString(ms, 4, ipv4);
        }

        if (ipv6 != null) {
            foreach (var addr in ipv6) {
                WriteString(ms, 5, addr);
            }
        }

        WriteVarintField(ms, 6, port);
        if (productModel != null) {
            WriteString(ms, 7, productModel);
        }

        if (buildVersionSdkFull != null) {
            WriteString(ms, 8, buildVersionSdkFull);
        }

        if (givenName != null) {
            WriteString(ms, 9, givenName);
        }

        if (serial != null) {
            WriteString(ms, 10, serial);
        }

        if (mdnsServiceVersion != null) {
            WriteString(ms, 11, mdnsServiceVersion);
        }

        if (hostname != null) {
            WriteString(ms, 12, hostname);
        }

        return ms.ToArray();
    }

    public static byte[] EncodeWrappedService(byte[] mdnsService) {
        var ms = new List<byte>();
        WriteTag(ms, 1, 2);
        WriteVarint(ms, (ulong)mdnsService.Length);
        ms.AddRange(mdnsService);
        return ms.ToArray();
    }

    public static byte[] EncodeTlsService(byte[] mdnsService, bool? knownDevice = null) {
        var ms = new List<byte>();
        WriteTag(ms, 1, 2);
        WriteVarint(ms, (ulong)mdnsService.Length);
        ms.AddRange(mdnsService);
        if (knownDevice is bool known) {
            WriteTag(ms, 2, 0);
            WriteVarint(ms, known ? 1u : 0u);
        }

        return ms.ToArray();
    }

    public static byte[] EncodeMdnsServices(
        IEnumerable<byte[]>? tcp = null,
        IEnumerable<byte[]>? tls = null,
        IEnumerable<byte[]>? pair = null) {
        var ms = new List<byte>();
        if (tcp != null) {
            foreach (var item in tcp) {
                WriteTag(ms, 1, 2);
                WriteVarint(ms, (ulong)item.Length);
                ms.AddRange(item);
            }
        }

        if (tls != null) {
            foreach (var item in tls) {
                WriteTag(ms, 2, 2);
                WriteVarint(ms, (ulong)item.Length);
                ms.AddRange(item);
            }
        }

        if (pair != null) {
            foreach (var item in pair) {
                WriteTag(ms, 3, 2);
                WriteVarint(ms, (ulong)item.Length);
                ms.AddRange(item);
            }
        }

        return ms.ToArray();
    }

    public static byte[] EncodeLegacy(string instance, int port, string? ipv4 = null, IEnumerable<string>? ipv6 = null, string? productModel = null) =>
        EncodeWrappedService(EncodeMdnsService(instance, MdnsServiceTypes.Legacy, port, ipv4, ipv6, productModel: productModel));

    public static byte[] EncodePairing(string instance, int port, string? ipv4 = null, IEnumerable<string>? ipv6 = null, string? givenName = null) =>
        EncodeWrappedService(EncodeMdnsService(instance, MdnsServiceTypes.Pairing, port, ipv4, ipv6, givenName: givenName));

    public static byte[] EncodeConnect(string instance, int port, string? ipv4 = null, IEnumerable<string>? ipv6 = null, bool? knownDevice = null, string? serial = null) =>
        EncodeTlsService(EncodeMdnsService(instance, MdnsServiceTypes.Connect, port, ipv4, ipv6, serial: serial), knownDevice);

    private static void WriteString(List<byte> ms, int fieldNumber, string value) {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteTag(ms, fieldNumber, 2);
        WriteVarint(ms, (ulong)bytes.Length);
        ms.AddRange(bytes);
    }

    private static void WriteVarintField(List<byte> ms, int fieldNumber, int value) {
        WriteTag(ms, fieldNumber, 0);
        WriteVarint(ms, unchecked((ulong)(uint)value));
    }

    private static void WriteTag(List<byte> ms, int fieldNumber, int wireType) {
        WriteVarint(ms, ((ulong)fieldNumber << 3) | (uint)wireType);
    }

    private static void WriteVarint(List<byte> ms, ulong value) {
        while (value >= 0x80) {
            ms.Add((byte)(value | 0x80));
            value >>= 7;
        }

        ms.Add((byte)value);
    }
}

internal sealed class FakeMdnsTrackServicesCli : IMdnsTrackServicesCli {
    private readonly Func<string, CancellationToken, IAsyncEnumerable<ReadOnlyMemory<byte>>> _factory;

    public List<string> ExecutablePaths { get; } = new();

    public FakeMdnsTrackServicesCli(params ReadOnlyMemory<byte>[] documents)
        : this((_, ct) => ToAsync(documents, ct)) {
    }

    public FakeMdnsTrackServicesCli(Func<string, CancellationToken, IAsyncEnumerable<ReadOnlyMemory<byte>>> factory) {
        _factory = factory;
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadProtoTextDocumentsAsync(
        string executablePath,
        [EnumeratorCancellation] CancellationToken ct) {
        ExecutablePaths.Add(executablePath);
        await foreach (var doc in _factory(executablePath, ct).WithCancellation(ct).ConfigureAwait(false)) {
            yield return doc;
        }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ToAsync(
        ReadOnlyMemory<byte>[] documents,
        [EnumeratorCancellation] CancellationToken ct = default) {
        foreach (var doc in documents) {
            ct.ThrowIfCancellationRequested();
            yield return doc;
            await Task.Yield();
        }
    }
}

internal sealed class FakeAdbExecutableLocator : IAdbExecutableLocator {
    private readonly string? _path;

    public FakeAdbExecutableLocator(string? path = @"C:\fake\platform-tools\adb.exe") {
        _path = path;
    }

    public string? Locate() => _path;
}
