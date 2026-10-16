namespace AdbView.Adb;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using AdbView.Adb.Model;
using AdbView.Adb.Protocol;

/// <summary>
/// Parses protobuf-text <c>Services</c> documents from
/// <c>adb.exe mdns track-services --proto-text</c> (platform-tools 37.0.1).
/// </summary>
internal static class MdnsServicesProtoTextParser {
    public static ImmutableArray<RawMdnsService> Parse(ReadOnlySpan<byte> payload) =>
        Parse(Encoding.UTF8.GetString(payload));

    public static ImmutableArray<RawMdnsService> Parse(string text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return ImmutableArray<RawMdnsService>.Empty;
        }

        var tokenizer = new Tokenizer(text);
        tokenizer.SkipNoise();

        // Optional leading "Services:" / "Services {" message name.
        if (tokenizer.TryConsumeIdent(out string? head) &&
            string.Equals(head, "Services", StringComparison.Ordinal)) {
            tokenizer.SkipNoise();
            if (tokenizer.TryConsume(':')) {
                tokenizer.SkipNoise();
            }
        }
        else {
            tokenizer.Reset();
            tokenizer.SkipNoise();
        }

        var builder = ImmutableArray.CreateBuilder<RawMdnsService>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (!tokenizer.IsEof) {
            tokenizer.SkipNoise();
            if (tokenizer.IsEof) {
                break;
            }

            if (!tokenizer.TryConsumeIdent(out string? group)) {
                throw new AdbProtocolException(
                    AdbErrorCode.MalformedLength,
                    $"Unexpected token in mDNS proto-text at position {tokenizer.Position}.");
            }

            tokenizer.SkipNoise();
            if (!tokenizer.TryConsume('{')) {
                throw new AdbProtocolException(
                    AdbErrorCode.MalformedLength,
                    $"Expected '{{' after '{group}' in mDNS proto-text.");
            }

            if (string.Equals(group, "tcp", StringComparison.Ordinal)) {
                Add(builder, seen, ParseWrappedGroup(tokenizer, knownDevice: null));
            }
            else if (string.Equals(group, "tls", StringComparison.Ordinal)) {
                Add(builder, seen, ParseTlsGroup(tokenizer));
            }
            else if (string.Equals(group, "pair", StringComparison.Ordinal)) {
                Add(builder, seen, ParseWrappedGroup(tokenizer, knownDevice: null));
            }
            else {
                // Skip unknown top-level groups safely.
                SkipBlock(tokenizer);
            }
        }

        return builder.ToImmutable();
    }

    private static RawMdnsService? ParseTlsGroup(Tokenizer tokenizer) {
        RawMdnsService? service = null;
        bool? knownDevice = null;

        while (true) {
            tokenizer.SkipNoise();
            if (tokenizer.TryConsume('}')) {
                break;
            }

            if (!tokenizer.TryConsumeIdent(out string? field)) {
                throw new AdbProtocolException(
                    AdbErrorCode.MalformedLength,
                    "Malformed tls group in mDNS proto-text.");
            }

            tokenizer.SkipNoise();
            if (string.Equals(field, "service", StringComparison.Ordinal)) {
                if (!tokenizer.TryConsume('{')) {
                    throw new AdbProtocolException(
                        AdbErrorCode.MalformedLength,
                        "Expected '{' after tls.service in mDNS proto-text.");
                }

                service = ParseServiceFields(tokenizer);
                continue;
            }

            if (string.Equals(field, "known_device", StringComparison.Ordinal)) {
                if (!tokenizer.TryConsume(':')) {
                    throw new AdbProtocolException(
                        AdbErrorCode.MalformedLength,
                        "Expected ':' after known_device in mDNS proto-text.");
                }

                tokenizer.SkipNoise();
                if (!tokenizer.TryConsumeIdent(out string? value)) {
                    throw new AdbProtocolException(
                        AdbErrorCode.MalformedLength,
                        "Expected bool after known_device in mDNS proto-text.");
                }

                knownDevice = value is "true" or "1";
                continue;
            }

            SkipFieldValue(tokenizer);
        }

        if (service is null) {
            return null;
        }

        return service with { KnownDevice = knownDevice, Format = RawMdnsFormat.ProtoText };
    }

    private static RawMdnsService? ParseWrappedGroup(Tokenizer tokenizer, bool? knownDevice) {
        RawMdnsService? service = null;

        while (true) {
            tokenizer.SkipNoise();
            if (tokenizer.TryConsume('}')) {
                break;
            }

            if (!tokenizer.TryConsumeIdent(out string? field)) {
                throw new AdbProtocolException(
                    AdbErrorCode.MalformedLength,
                    "Malformed service group in mDNS proto-text.");
            }

            tokenizer.SkipNoise();
            if (string.Equals(field, "service", StringComparison.Ordinal)) {
                if (!tokenizer.TryConsume('{')) {
                    throw new AdbProtocolException(
                        AdbErrorCode.MalformedLength,
                        "Expected '{' after service in mDNS proto-text.");
                }

                service = ParseServiceFields(tokenizer);
                continue;
            }

            SkipFieldValue(tokenizer);
        }

        if (service is null) {
            return null;
        }

        return service with { KnownDevice = knownDevice, Format = RawMdnsFormat.ProtoText };
    }

    private static RawMdnsService ParseServiceFields(Tokenizer tokenizer) {
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

        while (true) {
            tokenizer.SkipNoise();
            if (tokenizer.TryConsume('}')) {
                break;
            }

            if (!tokenizer.TryConsumeIdent(out string? field)) {
                throw new AdbProtocolException(
                    AdbErrorCode.MalformedLength,
                    "Malformed MdnsService fields in mDNS proto-text.");
            }

            tokenizer.SkipNoise();
            if (!tokenizer.TryConsume(':')) {
                throw new AdbProtocolException(
                    AdbErrorCode.MalformedLength,
                    $"Expected ':' after field '{field}' in mDNS proto-text.");
            }

            tokenizer.SkipNoise();
            switch (field) {
                case "instance":
                    instance = tokenizer.ReadStringOrToken();
                    break;
                case "service":
                    serviceType = tokenizer.ReadStringOrToken();
                    break;
                case "domain":
                    domain = tokenizer.ReadStringOrToken();
                    break;
                case "ipv4":
                    ipv4 = tokenizer.ReadStringOrToken();
                    break;
                case "ipv6":
                    ipv6.Add(tokenizer.ReadStringOrToken());
                    break;
                case "port":
                    port = int.Parse(tokenizer.ReadStringOrToken(), CultureInfo.InvariantCulture);
                    break;
                case "product_model":
                    productModel = tokenizer.ReadStringOrToken();
                    break;
                case "build_version_sdk_full":
                    buildVersionSdkFull = tokenizer.ReadStringOrToken();
                    break;
                case "given_name":
                    givenName = tokenizer.ReadStringOrToken();
                    break;
                case "serial":
                    serial = tokenizer.ReadStringOrToken();
                    break;
                case "mdns_service_version":
                    mdnsServiceVersion = tokenizer.ReadStringOrToken();
                    break;
                case "hostname":
                    hostname = tokenizer.ReadStringOrToken();
                    break;
                default:
                    SkipScalar(tokenizer);
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
            KnownDevice = null,
            Format = RawMdnsFormat.ProtoText,
        };
    }

    private static void SkipFieldValue(Tokenizer tokenizer) {
        tokenizer.SkipNoise();
        if (tokenizer.TryConsume('{')) {
            SkipBlock(tokenizer);
            return;
        }

        if (tokenizer.TryConsume(':')) {
            tokenizer.SkipNoise();
            SkipScalar(tokenizer);
            return;
        }

        SkipScalar(tokenizer);
    }

    private static void SkipScalar(Tokenizer tokenizer) {
        if (tokenizer.Peek() == '"') {
            _ = tokenizer.ReadStringOrToken();
            return;
        }

        _ = tokenizer.ReadStringOrToken();
    }

    private static void SkipBlock(Tokenizer tokenizer) {
        int depth = 1;
        while (!tokenizer.IsEof && depth > 0) {
            char c = tokenizer.Next();
            if (c == '"') {
                // consume string
                while (!tokenizer.IsEof) {
                    char s = tokenizer.Next();
                    if (s == '\\' && !tokenizer.IsEof) {
                        tokenizer.Next();
                        continue;
                    }

                    if (s == '"') {
                        break;
                    }
                }
            }
            else if (c == '{') {
                depth++;
            }
            else if (c == '}') {
                depth--;
            }
        }
    }

    private static void Add(
        ImmutableArray<RawMdnsService>.Builder builder,
        HashSet<string> seen,
        RawMdnsService? service) {
        if (service is null) {
            return;
        }

        if (string.IsNullOrEmpty(service.InstanceName) && string.IsNullOrEmpty(service.ServiceType)) {
            return;
        }

        if (!seen.Add(service.DiscoveryKey)) {
            return;
        }

        builder.Add(service);
    }

    private sealed class Tokenizer {
        private readonly string _text;
        private int _i;

        public Tokenizer(string text) {
            _text = text;
            _i = 0;
        }

        public int Position => _i;
        public bool IsEof => _i >= _text.Length;

        public void Reset() => _i = 0;

        public char Peek() => IsEof ? '\0' : _text[_i];

        public char Next() {
            if (IsEof) {
                return '\0';
            }

            return _text[_i++];
        }

        public void SkipNoise() {
            while (!IsEof) {
                char c = Peek();
                if (char.IsWhiteSpace(c)) {
                    _i++;
                    continue;
                }

                if (c == '#') {
                    while (!IsEof && Peek() is not ('\n' or '\r')) {
                        _i++;
                    }

                    continue;
                }

                break;
            }
        }

        public bool TryConsume(char expected) {
            if (IsEof || Peek() != expected) {
                return false;
            }

            _i++;
            return true;
        }

        public bool TryConsumeIdent(out string? ident) {
            SkipNoise();
            if (IsEof || !(char.IsLetter(Peek()) || Peek() == '_')) {
                ident = null;
                return false;
            }

            int start = _i;
            _i++;
            while (!IsEof && (char.IsLetterOrDigit(Peek()) || Peek() == '_')) {
                _i++;
            }

            ident = _text[start.._i];
            return true;
        }

        public string ReadStringOrToken() {
            SkipNoise();
            if (IsEof) {
                throw new AdbProtocolException(AdbErrorCode.UnexpectedEof, "Truncated mDNS proto-text value.");
            }

            if (Peek() == '"') {
                _i++; // opening quote
                var sb = new StringBuilder();
                while (!IsEof) {
                    char c = Next();
                    if (c == '\\' && !IsEof) {
                        sb.Append(Next());
                        continue;
                    }

                    if (c == '"') {
                        return sb.ToString();
                    }

                    sb.Append(c);
                }

                throw new AdbProtocolException(AdbErrorCode.UnexpectedEof, "Unterminated string in mDNS proto-text.");
            }

            int start = _i;
            while (!IsEof) {
                char c = Peek();
                if (char.IsWhiteSpace(c) || c is '{' or '}' or ':' or '#') {
                    break;
                }

                _i++;
            }

            if (start == _i) {
                throw new AdbProtocolException(
                    AdbErrorCode.MalformedLength,
                    $"Expected scalar at position {Position} in mDNS proto-text.");
            }

            return _text[start.._i];
        }
    }
}
