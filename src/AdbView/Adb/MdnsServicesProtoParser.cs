namespace AdbView.Adb;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using AdbView.Adb.Model;
using AdbView.Adb.Protocol;

/// <summary>
/// Parses <c>MdnsServices</c> protobuf payloads for <c>host:track-mdns-services</c>.
/// Schema: platform-tools 37.0.1 embedded FileDescriptor (see research note).
/// </summary>
internal static class MdnsServicesProtoParser {
    // MdnsServices
    private const int FieldTcp = 1;
    private const int FieldTls = 2;
    private const int FieldPair = 3;

    // ServiceAdbTcp / ServiceAdbPairing / ServiceAdbTls
    private const int FieldWrappedService = 1;
    private const int FieldKnownDevice = 2;

    // MdnsService
    private const int FieldInstance = 1;
    private const int FieldService = 2;
    private const int FieldDomain = 3;
    private const int FieldIpv4 = 4;
    private const int FieldIpv6 = 5;
    private const int FieldPort = 6;
    private const int FieldProductModel = 7;
    private const int FieldBuildVersionSdkFull = 8;
    private const int FieldGivenName = 9;
    private const int FieldSerial = 10;
    private const int FieldMdnsServiceVersion = 11;
    private const int FieldHostname = 12;

    private const int MaxNestedBytes = 64 * 1024;

    public static ImmutableArray<RawMdnsService> Parse(ReadOnlySpan<byte> payload) {
        if (payload.IsEmpty) {
            return ImmutableArray<RawMdnsService>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<RawMdnsService>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var reader = new AdbProtoReader(payload);

        while (reader.TryReadTag(out int fieldNumber, out int wireType)) {
            if (fieldNumber is FieldTcp or FieldTls or FieldPair) {
                if (wireType != 2) {
                    throw new AdbProtocolException(
                        AdbErrorCode.MalformedLength,
                        $"MdnsServices field {fieldNumber} expected length-delimited wire type 2, got {wireType}.");
                }

                ReadOnlySpan<byte> nested = reader.ReadLengthDelimited();
                if (nested.Length > MaxNestedBytes) {
                    throw new AdbProtocolException(
                        AdbErrorCode.PayloadTooLarge,
                        $"MdnsServices nested message length {nested.Length} exceeds cap {MaxNestedBytes}.");
                }

                RawMdnsService? service = fieldNumber switch {
                    FieldTcp => ParseWrapped(nested, defaultKnownDevice: null),
                    FieldTls => ParseTls(nested),
                    FieldPair => ParseWrapped(nested, defaultKnownDevice: null),
                    _ => null,
                };

                if (service is not null) {
                    AddDeduped(builder, seen, service);
                }

                continue;
            }

            reader.SkipField(wireType);
        }

        return builder.ToImmutable();
    }

    private static RawMdnsService? ParseTls(ReadOnlySpan<byte> bytes) {
        var reader = new AdbProtoReader(bytes);
        ReadOnlySpan<byte> serviceBytes = default;
        bool hasService = false;
        bool? knownDevice = null;

        while (reader.TryReadTag(out int fieldNumber, out int wireType)) {
            switch (fieldNumber) {
                case FieldWrappedService:
                    if (wireType != 2) {
                        throw new AdbProtocolException(
                            AdbErrorCode.MalformedLength,
                            $"ServiceAdbTls.service expected wire type 2, got {wireType}.");
                    }
                    serviceBytes = reader.ReadLengthDelimited();
                    hasService = true;
                    break;

                case FieldKnownDevice:
                    if (wireType != 0) {
                        throw new AdbProtocolException(
                            AdbErrorCode.MalformedLength,
                            $"ServiceAdbTls.known_device expected wire type 0, got {wireType}.");
                    }
                    knownDevice = reader.ReadBool();
                    break;

                default:
                    reader.SkipField(wireType);
                    break;
            }
        }

        if (!hasService) {
            return null;
        }

        return ParseMdnsService(serviceBytes, knownDevice, RawMdnsFormat.ProtoBinary);
    }

    private static RawMdnsService? ParseWrapped(ReadOnlySpan<byte> bytes, bool? defaultKnownDevice) {
        var reader = new AdbProtoReader(bytes);
        ReadOnlySpan<byte> serviceBytes = default;
        bool hasService = false;

        while (reader.TryReadTag(out int fieldNumber, out int wireType)) {
            if (fieldNumber == FieldWrappedService) {
                if (wireType != 2) {
                    throw new AdbProtocolException(
                        AdbErrorCode.MalformedLength,
                        $"Wrapped MdnsService expected wire type 2, got {wireType}.");
                }

                serviceBytes = reader.ReadLengthDelimited();
                hasService = true;
                continue;
            }

            reader.SkipField(wireType);
        }

        if (!hasService) {
            return null;
        }

        return ParseMdnsService(serviceBytes, defaultKnownDevice, RawMdnsFormat.ProtoBinary);
    }

    private static RawMdnsService ParseMdnsService(
        ReadOnlySpan<byte> bytes,
        bool? knownDevice,
        RawMdnsFormat format) {
        var reader = new AdbProtoReader(bytes);
        string instance = string.Empty;
        string serviceType = string.Empty;
        string? domain = null;
        string? ipv4 = null;
        var ipv6 = ImmutableArray.CreateBuilder<string>();
        int port = 0;
        string? productModel = null;
        string? buildVersionSdkFull = null;
        string? givenName = null;
        string? serial = null;
        string? mdnsServiceVersion = null;
        string? hostname = null;

        while (reader.TryReadTag(out int fieldNumber, out int wireType)) {
            switch (fieldNumber) {
                case FieldInstance:
                    RequireWire(wireType, 2, "MdnsService.instance");
                    instance = reader.ReadString();
                    break;
                case FieldService:
                    RequireWire(wireType, 2, "MdnsService.service");
                    serviceType = reader.ReadString();
                    break;
                case FieldDomain:
                    RequireWire(wireType, 2, "MdnsService.domain");
                    domain = reader.ReadString();
                    break;
                case FieldIpv4:
                    RequireWire(wireType, 2, "MdnsService.ipv4");
                    ipv4 = reader.ReadString();
                    break;
                case FieldIpv6:
                    RequireWire(wireType, 2, "MdnsService.ipv6");
                    ipv6.Add(reader.ReadString());
                    break;
                case FieldPort:
                    RequireWire(wireType, 0, "MdnsService.port");
                    port = reader.ReadInt32();
                    break;
                case FieldProductModel:
                    RequireWire(wireType, 2, "MdnsService.product_model");
                    productModel = reader.ReadString();
                    break;
                case FieldBuildVersionSdkFull:
                    RequireWire(wireType, 2, "MdnsService.build_version_sdk_full");
                    buildVersionSdkFull = reader.ReadString();
                    break;
                case FieldGivenName:
                    RequireWire(wireType, 2, "MdnsService.given_name");
                    givenName = reader.ReadString();
                    break;
                case FieldSerial:
                    RequireWire(wireType, 2, "MdnsService.serial");
                    serial = reader.ReadString();
                    break;
                case FieldMdnsServiceVersion:
                    RequireWire(wireType, 2, "MdnsService.mdns_service_version");
                    mdnsServiceVersion = reader.ReadString();
                    break;
                case FieldHostname:
                    RequireWire(wireType, 2, "MdnsService.hostname");
                    hostname = reader.ReadString();
                    break;
                default:
                    reader.SkipField(wireType);
                    break;
            }
        }

        return new RawMdnsService {
            InstanceName = instance,
            ServiceType = serviceType,
            Kind = MdnsServiceTypes.Classify(serviceType),
            Domain = domain,
            Ipv4 = ipv4,
            Ipv6 = ipv6.ToImmutable(),
            Port = port,
            ProductModel = productModel,
            BuildVersionSdkFull = buildVersionSdkFull,
            GivenName = givenName,
            Serial = serial,
            MdnsServiceVersion = mdnsServiceVersion,
            Hostname = hostname,
            KnownDevice = knownDevice,
            Format = format,
        };
    }

    private static void RequireWire(int wireType, int expected, string field) {
        if (wireType != expected) {
            throw new AdbProtocolException(
                AdbErrorCode.MalformedLength,
                $"{field} expected wire type {expected}, got {wireType}.");
        }
    }

    private static void AddDeduped(
        ImmutableArray<RawMdnsService>.Builder builder,
        HashSet<string> seen,
        RawMdnsService service) {
        if (string.IsNullOrEmpty(service.InstanceName) && string.IsNullOrEmpty(service.ServiceType)) {
            return;
        }

        if (!seen.Add(service.DiscoveryKey)) {
            return;
        }

        builder.Add(service);
    }
}
