import { describe, expect, it } from 'vitest';
import {
  PAIRING_SUCCESS_DWELL_MS,
  createDwellController,
  resolvePairingPresentation,
} from './pairingPresentation';
import { PROTOCOL_VERSION, type AppSnapshot, type DeviceView } from '../contract/snapshot';

function wifiDevice(id = 'wifi-1'): DeviceView {
  return {
    id,
    displayName: 'Phone',
    state: 'connected',
    severity: 'positive',
    stateLabel: 'Connected',
    statusLine: 'Connected',
    guidance: null,
    transport: 'wifi',
    transportLabel: 'Wi-Fi',
    batteryPercent: 50,
    metaTemplate: 'Updated {age}',
    stateSince: '2026-09-20T00:00:00.000Z',
    connectedSince: '2026-09-20T00:00:00.000Z',
    detail: {
      serial: 'x',
      exactModel: null,
      androidVersion: null,
      address: '192.168.1.2:5555',
      copyFields: [],
    },
  };
}

function baseSnapshot(over: Partial<AppSnapshot> = {}): AppSnapshot {
  return {
    protocol: PROTOCOL_VERSION,
    revision: 1,
    generatedAt: '2026-09-20T00:00:00.000Z',
    health: {
      kind: 'healthy',
      title: null,
      detail: null,
      hint: null,
      tray: 'idle',
      trayTooltip: 'ADB View',
    },
    devices: [],
    pairingOffers: [],
    pairing: null,
    warnings: [],
    shell: {
      theme: 'dark',
      pinned: false,
      reducedMotion: false,
      transparencyEnabled: true,
      forcedColors: false,
    },
    deviceCountLabel: null,
    ...over,
  };
}

describe('pairingPresentation success dwell', () => {
  it('uses 750ms minimum dwell (no DESIGN confirmation override found)', () => {
    expect(PAIRING_SUCCESS_DWELL_MS).toBe(750);
  });

  it('keeps success visible when device arrives at 5ms', () => {
    const t0 = 1000;
    let r = resolvePairingPresentation(
      baseSnapshot({
        pairing: {
          offerId: 'o1',
          phase: 'pairedWaitingForDevice',
          displayName: 'Phone',
          title: 'Paired',
          detail: null,
          canSubmitCode: false,
          canCancel: false,
          canRetry: false,
          codeLength: 6,
        },
      }),
      createDwellController(),
      t0,
    );
    expect(r.presentation?.dwellingSuccess).toBe(true);
    expect(r.presentation?.cardState).toBe('pairing-success');

    r = resolvePairingPresentation(
      baseSnapshot({ pairing: null, devices: [wifiDevice()] }),
      r.dwell,
      t0 + 5,
    );
    expect(r.presentation?.dwellingSuccess).toBe(true);
    expect(r.presentation?.cardState).toBe('pairing-success');
    expect(r.presentation?.layoutDeviceIds).toEqual([]);
  });

  it.each([5, 100, 400, 700])('device arrival at %sms still dwelling', (arrivalMs) => {
    const t0 = 5000;
    let r = resolvePairingPresentation(
      baseSnapshot({
        pairing: {
          offerId: 'o1',
          phase: 'pairedWaitingForDevice',
          displayName: 'Phone',
          title: 'Paired',
          detail: null,
          canSubmitCode: false,
          canCancel: false,
          canRetry: false,
          codeLength: 6,
        },
      }),
      createDwellController(),
      t0,
    );
    r = resolvePairingPresentation(
      baseSnapshot({ pairing: null, devices: [wifiDevice()] }),
      r.dwell,
      t0 + arrivalMs,
    );
    expect(r.presentation?.dwellingSuccess).toBe(true);
  });

  it('after dwell expires, drops success presentation for authoritative layout', () => {
    const t0 = 1000;
    let r = resolvePairingPresentation(
      baseSnapshot({
        pairing: {
          offerId: 'o1',
          phase: 'pairedWaitingForDevice',
          displayName: 'Phone',
          title: 'Paired',
          detail: null,
          canSubmitCode: false,
          canCancel: false,
          canRetry: false,
          codeLength: 6,
        },
      }),
      createDwellController(),
      t0,
    );
    r = resolvePairingPresentation(
      baseSnapshot({ pairing: null, devices: [wifiDevice()] }),
      r.dwell,
      t0 + PAIRING_SUCCESS_DWELL_MS + 1,
    );
    expect(r.dwellExpired).toBe(true);
    expect(r.presentation).toBeNull();
  });

  it('cancels dwell immediately on pairing failure', () => {
    const t0 = 1000;
    let r = resolvePairingPresentation(
      baseSnapshot({
        pairing: {
          offerId: 'o1',
          phase: 'pairedWaitingForDevice',
          displayName: 'Phone',
          title: 'Paired',
          detail: null,
          canSubmitCode: false,
          canCancel: false,
          canRetry: false,
          codeLength: 6,
        },
      }),
      createDwellController(),
      t0,
    );
    r = resolvePairingPresentation(
      baseSnapshot({
        pairing: {
          offerId: 'o1',
          phase: 'failed',
          displayName: 'Phone',
          title: 'Pairing failed',
          detail: 'Check the code and try again.',
          canSubmitCode: true,
          canCancel: true,
          canRetry: true,
          codeLength: 6,
        },
      }),
      r.dwell,
      t0 + 10,
    );
    expect(r.presentation?.dwellingSuccess).toBe(false);
    expect(r.presentation?.cardState).toBe('pairing-failure');
  });

  it('cancels dwell on ADB unavailable', () => {
    const t0 = 1000;
    let r = resolvePairingPresentation(
      baseSnapshot({
        pairing: {
          offerId: 'o1',
          phase: 'pairedWaitingForDevice',
          displayName: 'Phone',
          title: 'Paired',
          detail: null,
          canSubmitCode: false,
          canCancel: false,
          canRetry: false,
          codeLength: 6,
        },
      }),
      createDwellController(),
      t0,
    );
    r = resolvePairingPresentation(
      baseSnapshot({
        health: {
          kind: 'adbUnavailable',
          title: 'ADB is unavailable',
          detail: null,
          hint: null,
          tray: 'failure',
          trayTooltip: 'ADB View',
        },
        pairing: null,
      }),
      r.dwell,
      t0 + 10,
    );
    expect(r.presentation?.dwellingSuccess ?? false).toBe(false);
    expect(r.dwell.dwellUntilMs).toBe(0);
  });

  it('does not restart dwell clock on metadata/device updates while success', () => {
    const t0 = 1000;
    let r = resolvePairingPresentation(
      baseSnapshot({
        pairing: {
          offerId: 'o1',
          phase: 'pairedWaitingForDevice',
          displayName: 'Phone',
          title: 'Paired',
          detail: null,
          canSubmitCode: false,
          canCancel: false,
          canRetry: false,
          codeLength: 6,
        },
      }),
      createDwellController(),
      t0,
    );
    const until = r.dwell.dwellUntilMs;
    r = resolvePairingPresentation(
      baseSnapshot({
        pairing: {
          offerId: 'o1',
          phase: 'pairedWaitingForDevice',
          displayName: 'Phone',
          title: 'Paired',
          detail: null,
          canSubmitCode: false,
          canCancel: false,
          canRetry: false,
          codeLength: 6,
        },
        devices: [wifiDevice()],
      }),
      r.dwell,
      t0 + 200,
    );
    expect(r.dwell.dwellUntilMs).toBe(until);
    expect(r.presentation?.layoutDeviceIds).toEqual([]);
  });
});
