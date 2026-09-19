namespace AdbView.Adb;

using System;
using System.Collections.Immutable;
using AdbView.Adb.Model;
using AdbView.Adb.Protocol;

/// <summary>
/// Parses <c>Devices</c> / <c>Device</c> protobuf payloads for
/// <c>host:track-devices-proto-binary</c>.
/// Schema pin: platform/packages/modules/adb @ 1cf2f017d312f73b3dc53bda85ef2610e35a80e9
/// </summary>
internal static class DevicesProtoParser {
    // Devices
    private const int FieldDevicesDevice = 1;

    // Device
    private const int FieldSerial = 1;
    private const int FieldState = 2;
    // 3 = bus_address (ignored)
    private const int FieldProduct = 4;
    private const int FieldModel = 5;
    private const int FieldDevice = 6;
    private const int FieldConnectionType = 7;
    // 8 = negotiated_speed, 9 = max_speed, 10 = transport_id (ignored)

    private const int MaxNestedDeviceBytes = 64 * 1024;

    public static ImmutableArray<RawDevice> Parse(ReadOnlySpan<byte> payload) {
        if (payload.IsEmpty) {
            return ImmutableArray<RawDevice>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<RawDevice>();
        var reader = new AdbProtoReader(payload);

        while (reader.TryReadTag(out int fieldNumber, out int wireType)) {
            if (fieldNumber == FieldDevicesDevice) {
                if (wireType != 2) {
                    throw new AdbProtocolException(
                        AdbErrorCode.MalformedLength,
                        $"Devices.device (field {FieldDevicesDevice}) expected length-delimited wire type 2, got {wireType}.");
                }

                ReadOnlySpan<byte> nested = reader.ReadLengthDelimited();
                if (nested.Length > MaxNestedDeviceBytes) {
                    throw new AdbProtocolException(
                        AdbErrorCode.PayloadTooLarge,
                        $"Devices.device nested message length {nested.Length} exceeds cap {MaxNestedDeviceBytes}.");
                }

                builder.Add(ParseDevice(nested));
                continue;
            }

            reader.SkipField(wireType);
        }

        return builder.ToImmutable();
    }

    private static RawDevice ParseDevice(ReadOnlySpan<byte> bytes) {
        var reader = new AdbProtoReader(bytes);
        string serial = string.Empty;
        int? protoState = null;
        int? connectionType = null;
        string? product = null;
        string? model = null;
        string? device = null;

        while (reader.TryReadTag(out int fieldNumber, out int wireType)) {
            switch (fieldNumber) {
                case FieldSerial:
                    if (wireType != 2) {
                        throw new AdbProtocolException(
                            AdbErrorCode.MalformedLength,
                            $"Device.serial expected wire type 2, got {wireType}.");
                    }
                    serial = reader.ReadString();
                    break;

                case FieldState:
                    if (wireType != 0) {
                        throw new AdbProtocolException(
                            AdbErrorCode.MalformedLength,
                            $"Device.state expected wire type 0, got {wireType}.");
                    }
                    protoState = reader.ReadInt32();
                    break;

                case FieldProduct:
                    if (wireType != 2) {
                        throw new AdbProtocolException(
                            AdbErrorCode.MalformedLength,
                            $"Device.product expected wire type 2, got {wireType}.");
                    }
                    product = reader.ReadString();
                    break;

                case FieldModel:
                    if (wireType != 2) {
                        throw new AdbProtocolException(
                            AdbErrorCode.MalformedLength,
                            $"Device.model expected wire type 2, got {wireType}.");
                    }
                    model = reader.ReadString();
                    break;

                case FieldDevice:
                    if (wireType != 2) {
                        throw new AdbProtocolException(
                            AdbErrorCode.MalformedLength,
                            $"Device.device expected wire type 2, got {wireType}.");
                    }
                    device = reader.ReadString();
                    break;

                case FieldConnectionType:
                    if (wireType != 0) {
                        throw new AdbProtocolException(
                            AdbErrorCode.MalformedLength,
                            $"Device.connection_type expected wire type 0, got {wireType}.");
                    }
                    connectionType = reader.ReadInt32();
                    break;

                default:
                    reader.SkipField(wireType);
                    break;
            }
        }

        return new RawDevice {
            Serial = serial,
            ProtoState = protoState,
            StateToken = null,
            ConnectionType = connectionType,
            Product = product,
            Model = model,
            Device = device,
            Format = RawDeviceFormat.ProtoBinary,
        };
    }
}
