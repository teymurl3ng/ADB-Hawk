namespace AdbView.Adb;

using System;
using AdbView.Adb.Protocol;

internal enum AdbMdnsBackend {
    Unknown = 0,
    Bonjour = 1,
    Openscreen = 2,
    Libadbmdns = 3
}

internal sealed record AdbServerStatus {
    public string Version { get; init; } = string.Empty;
    public bool MdnsEnabled { get; init; }
    public AdbMdnsBackend MdnsBackend { get; init; } = AdbMdnsBackend.Unknown;
    public string? LogPath { get; init; }
    public string? ExecutablePath { get; init; }

    public static AdbServerStatus Parse(ReadOnlySpan<byte> bytes) {
        var reader = new AdbProtoReader(bytes);
        string version = string.Empty;
        bool mdnsEnabled = false;
        AdbMdnsBackend mdnsBackend = AdbMdnsBackend.Unknown;
        string? logPath = null;
        string? executablePath = null;

        while (reader.TryReadTag(out int fieldNumber, out int wireType)) {
            switch (fieldNumber) {
                case 3: // mdns_backend (enum MdnsBackend: 0=Unknown, 1=Bonjour, 2=Openscreen, 3=Libadbmdns)
                    if (wireType != 0) {
                        throw new AdbProtocolException(
                            AdbErrorCode.MalformedLength,
                            $"Protobuf field 3 (mdns_backend) expected varint (wire type 0), but got {wireType}.");
                    }
                    int backendVal = reader.ReadInt32();
                    mdnsBackend = backendVal switch {
                        1 => AdbMdnsBackend.Bonjour,
                        2 => AdbMdnsBackend.Openscreen,
                        3 => AdbMdnsBackend.Libadbmdns,
                        _ => AdbMdnsBackend.Unknown
                    };
                    break;

                case 5: // version (string)
                    if (wireType != 2) {
                        throw new AdbProtocolException(
                            AdbErrorCode.MalformedLength,
                            $"Protobuf field 5 (version) expected length-delimited (wire type 2), but got {wireType}.");
                    }
                    version = reader.ReadString();
                    break;

                case 7: // executable_absolute_path (string)
                    if (wireType != 2) {
                        throw new AdbProtocolException(
                            AdbErrorCode.MalformedLength,
                            $"Protobuf field 7 (executable_absolute_path) expected length-delimited (wire type 2), but got {wireType}.");
                    }
                    executablePath = reader.ReadString();
                    break;

                case 8: // log_absolute_path (string)
                    if (wireType != 2) {
                        throw new AdbProtocolException(
                            AdbErrorCode.MalformedLength,
                            $"Protobuf field 8 (log_absolute_path) expected length-delimited (wire type 2), but got {wireType}.");
                    }
                    logPath = reader.ReadString();
                    break;

                case 12: // mdns_enabled (optional bool)
                    if (wireType != 0) {
                        throw new AdbProtocolException(
                            AdbErrorCode.MalformedLength,
                            $"Protobuf field 12 (mdns_enabled) expected varint (wire type 0), but got {wireType}.");
                    }
                    mdnsEnabled = reader.ReadBool();
                    break;

                default: // Unknown field: skip safely by wire type (forward compatibility)
                    reader.SkipField(wireType);
                    break;
            }
        }

        return new AdbServerStatus {
            Version = version,
            MdnsEnabled = mdnsEnabled,
            MdnsBackend = mdnsBackend,
            LogPath = logPath,
            ExecutablePath = executablePath
        };
    }
}
