namespace AdbView.Bridge;

using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AdbView.State;

internal static class BridgeSerializer {
    public static readonly JsonSerializerOptions SnapshotOptions = CreateSnapshotOptions();
    public static readonly JsonSerializerOptions ActionOptions = CreateActionOptions();

    private static JsonSerializerOptions CreateSnapshotOptions() {
        var options = new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip, // Forward-compatible
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new UtcDateTimeOffsetConverter());
        return options;
    }

    private static JsonSerializerOptions CreateActionOptions() {
        var options = new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip, // Forward-compatible: accepts and ignores unknown extra properties
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static string SerializeSnapshotEnvelope(SnapshotEnvelope envelope) {
        ArgumentNullException.ThrowIfNull(envelope);
        ValidateSnapshot(envelope.Snapshot);
        return JsonSerializer.Serialize(envelope, SnapshotOptions);
    }

    public static string SerializeSnapshot(AppSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshot(snapshot);
        return JsonSerializer.Serialize(snapshot, SnapshotOptions);
    }

    public static AppSnapshot DeserializeSnapshot(string json) {
        ArgumentNullException.ThrowIfNull(json);
        var snapshot = JsonSerializer.Deserialize<AppSnapshot>(json, SnapshotOptions)
            ?? throw new JsonException("Snapshot deserialized to null.");
        ValidateSnapshot(snapshot);
        return snapshot;
    }

    public static SnapshotEnvelope DeserializeSnapshotEnvelope(string json) {
        ArgumentNullException.ThrowIfNull(json);
        var envelope = JsonSerializer.Deserialize<SnapshotEnvelope>(json, SnapshotOptions)
            ?? throw new JsonException("Snapshot envelope deserialized to null.");

        if (!string.Equals(envelope.Type, "snapshot", StringComparison.Ordinal)) {
            throw new JsonException($"Invalid envelope type: '{envelope.Type}', expected 'snapshot'.");
        }

        if (envelope.Protocol != BridgeProtocol.Version) {
            throw new JsonException($"Protocol mismatch: envelope protocol {envelope.Protocol} != {BridgeProtocol.Version}.");
        }

        ValidateSnapshot(envelope.Snapshot);
        return envelope;
    }

    public static string SerializeAction(WebAction action) {
        ArgumentNullException.ThrowIfNull(action);
        return JsonSerializer.Serialize(action, ActionOptions);
    }

    public static WebAction DeserializeAction(string json) {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<WebAction>(json, ActionOptions)
            ?? throw new JsonException("Web action deserialized to null.");
    }

    public static void ValidateSnapshot(AppSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Protocol != BridgeProtocol.Version) {
            throw new InvalidOperationException($"Protocol mismatch: snapshot protocol {snapshot.Protocol} != {BridgeProtocol.Version}.");
        }

        if (snapshot.Revision < 1 || snapshot.Revision > BridgeProtocol.MaxSafeRevision) {
            throw new ArgumentOutOfRangeException(nameof(snapshot),
                $"Revision {snapshot.Revision} must be between 1 and {BridgeProtocol.MaxSafeRevision} (JavaScript Number.MAX_SAFE_INTEGER).");
        }

        if (snapshot.Pairing is not null && snapshot.Pairing.CodeLength != 6) {
            throw new ArgumentException("Pairing code length must be exactly 6.", nameof(snapshot));
        }

        foreach (var device in snapshot.Devices) {
            if (device.BatteryPercent is not null && (device.BatteryPercent < 0 || device.BatteryPercent > 100)) {
                throw new ArgumentOutOfRangeException(nameof(snapshot),
                    $"Battery percent {device.BatteryPercent} must be null or between 0 and 100.");
            }
        }
    }
}

internal sealed class SafeRevisionConverter : JsonConverter<long> {
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType != JsonTokenType.Number) {
            throw new JsonException($"Expected number for Revision, found {reader.TokenType}.");
        }

        var value = reader.GetInt64();
        if (value < 1 || value > BridgeProtocol.MaxSafeRevision) {
            throw new JsonException($"Revision {value} is out of safe range [1, {BridgeProtocol.MaxSafeRevision}].");
        }

        return value;
    }

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) {
        ArgumentNullException.ThrowIfNull(writer);

        if (value < 1 || value > BridgeProtocol.MaxSafeRevision) {
            throw new JsonException($"Revision {value} is out of safe range [1, {BridgeProtocol.MaxSafeRevision}].");
        }

        writer.WriteNumberValue(value);
    }
}

internal sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset> {
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType != JsonTokenType.String) {
            throw new JsonException($"Expected string for DateTimeOffset, found {reader.TokenType}.");
        }

        var stringVal = reader.GetString();
        if (stringVal is null || !DateTimeOffset.TryParse(stringVal, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)) {
            throw new JsonException($"Invalid DateTimeOffset format: '{stringVal}'.");
        }

        return dto;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
    }
}
