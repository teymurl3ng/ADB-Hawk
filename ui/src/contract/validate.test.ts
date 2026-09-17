import { describe, it, expect, vi } from 'vitest';
import { parseSnapshot } from './validate';
import { PROTOCOL_VERSION } from './snapshot';
import canaryFixtures from './__fixtures__/canary.json';

function suppressWarn<T>(fn: () => T): T {
  const spy = vi.spyOn(console, 'warn').mockImplementation(() => {});
  try {
    return fn();
  } finally {
    spy.mockRestore();
  }
}

function createBaseValidEnvelope() {
  return {
    type: 'snapshot',
    protocol: PROTOCOL_VERSION,
    snapshot: {
      protocol: PROTOCOL_VERSION,
      revision: 1,
      generatedAt: '2026-09-17T12:00:00.000Z',
      health: {
        kind: 'healthy',
        title: 'All good',
        detail: 'Connected',
        hint: null,
        tray: 'connected',
        trayTooltip: 'Pixel 8',
      },
      shell: {
        pinned: false,
        theme: 'dark',
        transparencyEnabled: true,
        forcedColors: false,
        reducedMotion: false,
      },
      devices: [
        {
          id: 'dev-1',
          displayName: 'Pixel 8',
          state: 'connected',
          severity: 'positive',
          stateLabel: 'Connected',
          statusLine: 'Connected · USB',
          guidance: null,
          transport: 'usb',
          transportLabel: 'USB',
          batteryPercent: 80 as number | null,
          metaTemplate: 'Connected {age} ago',
          stateSince: '2026-09-17T11:00:00.000Z',
          connectedSince: '2026-09-17T11:00:00.000Z',
          detail: {
            serial: 'SER123',
            exactModel: 'Pixel 8',
            androidVersion: '15',
            address: null,
            copyFields: [{ key: 'serial', label: 'Serial', value: 'SER123' }],
          },
        },
      ],
      pairingOffers: [
        {
          id: 'offer-1',
          displayName: 'Tablet',
          heading: 'Pairing offer',
          detail: 'Tap to pair',
          actionLabel: 'Pair',
        },
      ],
      pairing: {
        offerId: 'offer-1',
        displayName: 'Tablet',
        phase: 'awaitingCode',
        title: 'Pairing...',
        detail: 'Enter code',
        canSubmitCode: true,
        canCancel: true,
        canRetry: false,
        codeLength: 6,
      },
      warnings: [
        {
          subsystem: 'wirelessDiscovery',
          severity: 'informational',
          title: 'mDNS active',
          detail: 'Tracking services',
        },
      ],
      deviceCountLabel: null,
    },
  };
}

describe('parseSnapshot runtime validator', () => {
  it('parses valid snapshot envelope completely', () => {
    const envelope = createBaseValidEnvelope();
    const result = parseSnapshot(envelope);

    expect(result).not.toBeNull();
    expect(result?.protocol).toBe(1);
    expect(result?.revision).toBe(1);
    expect(result?.health.kind).toBe('healthy');
    expect(result?.devices.length).toBe(1);
    expect(result?.devices[0]?.transport).toBe('usb');
    expect(result?.pairing?.codeLength).toBe(6);
    expect(result?.warnings.length).toBe(1);
  });

  it('parses raw JSON string input', () => {
    const envelope = createBaseValidEnvelope();
    const json = JSON.stringify(envelope);
    const result = parseSnapshot(json);

    expect(result).not.toBeNull();
    expect(result?.revision).toBe(1);
  });

  it('rejects non-object or malformed JSON inputs', () => {
    suppressWarn(() => {
      expect(parseSnapshot(null)).toBeNull();
      expect(parseSnapshot(123)).toBeNull();
      expect(parseSnapshot('invalid-json{')).toBeNull();
      expect(parseSnapshot([])).toBeNull();
    });
  });

  it('rejects invalid envelope type or protocol', () => {
    suppressWarn(() => {
      const wrongType = { ...createBaseValidEnvelope(), type: 'wrong' };
      expect(parseSnapshot(wrongType)).toBeNull();

      const wrongEnvelopeProtocol = { ...createBaseValidEnvelope(), protocol: 2 };
      expect(parseSnapshot(wrongEnvelopeProtocol)).toBeNull();

      const mismatchedProtocol = createBaseValidEnvelope();
      (mismatchedProtocol.snapshot as Record<string, unknown>).protocol = 2;
      expect(parseSnapshot(mismatchedProtocol)).toBeNull();

      const missingSnapshot = { type: 'snapshot', protocol: 1 };
      expect(parseSnapshot(missingSnapshot)).toBeNull();
    });
  });

  it('enforces safe integer revision invariants', () => {
    suppressWarn(() => {
      const zeroRev = createBaseValidEnvelope();
      zeroRev.snapshot.revision = 0;
      expect(parseSnapshot(zeroRev)).toBeNull();

      const negRev = createBaseValidEnvelope();
      negRev.snapshot.revision = -1;
      expect(parseSnapshot(negRev)).toBeNull();

      const floatRev = createBaseValidEnvelope();
      floatRev.snapshot.revision = 1.5;
      expect(parseSnapshot(floatRev)).toBeNull();

      const unsafeRev = createBaseValidEnvelope();
      unsafeRev.snapshot.revision = Number.MAX_SAFE_INTEGER + 1;
      expect(parseSnapshot(unsafeRev)).toBeNull();
    });

    const maxRev = createBaseValidEnvelope();
    maxRev.snapshot.revision = Number.MAX_SAFE_INTEGER;
    expect(parseSnapshot(maxRev)?.revision).toBe(Number.MAX_SAFE_INTEGER);
  });

  it('enforces ISO-8601 timestamp validity', () => {
    suppressWarn(() => {
      const badTimestamp = createBaseValidEnvelope();
      badTimestamp.snapshot.generatedAt = 'not-a-timestamp';
      expect(parseSnapshot(badTimestamp)).toBeNull();

      const badDeviceTimestamp = createBaseValidEnvelope();
      badDeviceTimestamp.snapshot.devices[0]!.stateSince = 'invalid-date';
      expect(parseSnapshot(badDeviceTimestamp)).toBeNull();
    });
  });

  it('rejects invalid enum literals across all sections', () => {
    suppressWarn(() => {
      const badHealth = createBaseValidEnvelope();
      (badHealth.snapshot.health as Record<string, unknown>).kind = 'exploded';
      expect(parseSnapshot(badHealth)).toBeNull();

      const badTray = createBaseValidEnvelope();
      (badTray.snapshot.health as Record<string, unknown>).tray = 'spinning';
      expect(parseSnapshot(badTray)).toBeNull();

      const badTheme = createBaseValidEnvelope();
      (badTheme.snapshot.shell as Record<string, unknown>).theme = 'neon';
      expect(parseSnapshot(badTheme)).toBeNull();

      const badDeviceState = createBaseValidEnvelope();
      (badDeviceState.snapshot.devices[0] as Record<string, unknown>).state = 'floating';
      expect(parseSnapshot(badDeviceState)).toBeNull();

      const badTransport = createBaseValidEnvelope();
      (badTransport.snapshot.devices[0] as Record<string, unknown>).transport = 'bluetooth';
      expect(parseSnapshot(badTransport)).toBeNull();

      const badPhase = createBaseValidEnvelope();
      (badPhase.snapshot.pairing as Record<string, unknown>).phase = 'completed';
      expect(parseSnapshot(badPhase)).toBeNull();

      const badSubsystem = createBaseValidEnvelope();
      (badSubsystem.snapshot.warnings[0] as Record<string, unknown>).subsystem = 'cellular';
      expect(parseSnapshot(badSubsystem)).toBeNull();
    });
  });

  it('validates battery range [0..100] and nullability', () => {
    const nullBattery = createBaseValidEnvelope();
    nullBattery.snapshot.devices[0]!.batteryPercent = null;
    expect(parseSnapshot(nullBattery)?.devices[0]?.batteryPercent).toBeNull();

    const minBattery = createBaseValidEnvelope();
    minBattery.snapshot.devices[0]!.batteryPercent = 0;
    expect(parseSnapshot(minBattery)?.devices[0]?.batteryPercent).toBe(0);

    const maxBattery = createBaseValidEnvelope();
    maxBattery.snapshot.devices[0]!.batteryPercent = 100;
    expect(parseSnapshot(maxBattery)?.devices[0]?.batteryPercent).toBe(100);

    suppressWarn(() => {
      const underBattery = createBaseValidEnvelope();
      underBattery.snapshot.devices[0]!.batteryPercent = -1;
      expect(parseSnapshot(underBattery)).toBeNull();

      const overBattery = createBaseValidEnvelope();
      overBattery.snapshot.devices[0]!.batteryPercent = 101;
      expect(parseSnapshot(overBattery)).toBeNull();
    });
  });

  it('validates pairing codeLength must be exactly 6', () => {
    const validPairing = createBaseValidEnvelope();
    expect(parseSnapshot(validPairing)?.pairing?.codeLength).toBe(6);

    suppressWarn(() => {
      const invalidCodeLength = createBaseValidEnvelope();
      invalidCodeLength.snapshot.pairing!.codeLength = 4;
      expect(parseSnapshot(invalidCodeLength)).toBeNull();
    });
  });

  it('tolerates unknown additional properties (forward compatibility)', () => {
    const extendedEnvelope = {
      ...createBaseValidEnvelope(),
      unknownEnvelopeField: 'ignored',
      snapshot: {
        ...createBaseValidEnvelope().snapshot,
        futureTopLevelFlag: true,
        extraConfig: { alpha: 1 },
        devices: [
          {
            ...createBaseValidEnvelope().snapshot.devices[0]!,
            futureDeviceField: 'extra',
          },
        ],
      },
    };

    const parsed = parseSnapshot(extendedEnvelope);
    expect(parsed).not.toBeNull();
    expect(parsed?.revision).toBe(1);
    expect(parsed?.devices[0]?.displayName).toBe('Pixel 8');
  });

  it('cross-language canary: parses canonical C# serialized snapshots from canary.json', () => {
    const fixtures = canaryFixtures as Record<string, unknown>;

    expect(Object.keys(fixtures)).toContain('connected-usb');
    expect(Object.keys(fixtures)).toContain('connected-wifi');
    expect(Object.keys(fixtures)).toContain('multi-device');
    expect(Object.keys(fixtures)).toContain('pairing');
    expect(Object.keys(fixtures)).toContain('nullables');
    expect(Object.keys(fixtures)).toContain('max-revision');

    for (const [name, rawEnvelope] of Object.entries(fixtures)) {
      const parsed = parseSnapshot(rawEnvelope);
      expect(parsed, `Failed to parse canary fixture: ${name}`).not.toBeNull();
      expect(parsed?.protocol).toBe(1);

      if (name === 'connected-usb') {
        expect(parsed?.devices.length).toBe(1);
        expect(parsed?.devices[0]?.transport).toBe('usb');
        expect(parsed?.devices[0]?.state).toBe('connected');
        expect(parsed?.pairing).toBeNull();
      } else if (name === 'connected-wifi') {
        expect(parsed?.devices.length).toBe(1);
        expect(parsed?.devices[0]?.transport).toBe('wifi');
        expect(parsed?.devices[0]?.detail.address).toBe('192.168.1.50:5555');
        expect(parsed?.pairing).toBeNull();
      } else if (name === 'multi-device') {
        expect(parsed?.devices.length).toBe(3);
        expect(parsed?.deviceCountLabel).toBe('3 devices');
        expect(parsed?.devices[0]?.detail.exactModel).toBe('Pixel 8');
        expect(parsed?.devices[1]?.detail.exactModel).toBeNull();
        expect(parsed?.devices[1]?.detail.address).toBe('192.168.1.50:5555');
        expect(parsed?.devices[2]?.state).toBe('unauthorized');
        expect(parsed?.devices[2]?.guidance).not.toBeNull();
      } else if (name === 'pairing') {
        expect(parsed?.pairingOffers.length).toBe(2);
        expect(parsed?.pairing).not.toBeNull();
        expect(parsed?.pairing?.offerId).toBe('mdns-1');
        expect(parsed?.pairing?.phase).toBe('awaitingCode');
        expect(parsed?.pairing?.canSubmitCode).toBe(true);
        expect(parsed?.pairing?.canCancel).toBe(true);
        expect(parsed?.pairing?.canRetry).toBe(false);
        expect(parsed?.pairing?.codeLength).toBe(6);
        expect(parsed?.pairing?.detail).toBe('Enter the six-digit code shown on the phone.');
      } else if (name === 'nullables') {
        expect(parsed?.health.title).toBeNull();
        expect(parsed?.health.detail).toBeNull();
        expect(parsed?.health.hint).toBeNull();
        expect(parsed?.devices[0]?.batteryPercent).toBeNull();
        expect(parsed?.devices[0]?.guidance).toBeNull();
        expect(parsed?.devices[0]?.connectedSince).toBeNull();
        expect(parsed?.devices[0]?.detail.exactModel).toBeNull();
        expect(parsed?.devices[0]?.detail.androidVersion).toBeNull();
        expect(parsed?.devices[0]?.detail.address).toBeNull();
        expect(parsed?.pairing).toBeNull();
        expect(parsed?.deviceCountLabel).toBeNull();
      } else if (name === 'max-revision') {
        expect(parsed?.revision).toBe(Number.MAX_SAFE_INTEGER);
      }
    }
  });
});
