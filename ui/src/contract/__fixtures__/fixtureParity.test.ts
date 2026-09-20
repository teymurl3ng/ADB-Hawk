import { describe, expect, it } from 'vitest';
import { parseSnapshot } from '../validate';
import { mapDeviceViewToDeviceInfo, derivePairingState } from '../../bridge/adapters';
import { formatAge } from '../../bridge/age';
import canonicalSnapshots from './canonicalSnapshots.json';

describe('Fixture Parity Suite (16 Canonical Fixtures)', () => {
  const fixtures = canonicalSnapshots as Record<string, unknown>;

  it('contains all 16 canonical fixtures', () => {
    const expected = [
      'empty',
      'connected-usb',
      'connected-wifi',
      'expanded',
      'unauthorized',
      'offline',
      'recovery',
      'bootloader',
      'sideload',
      'unknown',
      'multi-device',
      'pairing-offer',
      'pairing-code',
      'pairing-progress',
      'pairing-success',
      'pairing-failure',
    ];
    for (const name of expected) {
      expect(fixtures[name]).toBeDefined();
    }
    expect(Object.keys(fixtures).length).toBe(16);
  });

  it('empty fixture: 0 devices, null pairing, empty state', () => {
    const parsed = parseSnapshot({ type: 'snapshot', protocol: 1, snapshot: fixtures['empty'] });
    expect(parsed).not.toBeNull();
    expect(parsed!.devices).toHaveLength(0);
    expect(parsed!.pairingOffers).toHaveLength(0);
    expect(parsed!.pairing).toBeNull();
    expect(derivePairingState(parsed)).toBeNull();
  });

  it('connected-usb: USB transport, no address, exact age and specs', () => {
    const parsed = parseSnapshot({
      type: 'snapshot',
      protocol: 1,
      snapshot: fixtures['connected-usb'],
    });
    expect(parsed).not.toBeNull();
    expect(parsed!.devices).toHaveLength(1);

    const device = mapDeviceViewToDeviceInfo(parsed!.devices[0]!, false, parsed!.generatedAt);
    expect(device.name).toBe('Galaxy S24');
    expect(device.transport).toBe('usb');
    expect(device.address).toBeUndefined();
    expect(device.battery).toBe(82);
    expect(device.model).toBe('SM-S921W');
    expect(device.serial).toBe('R5CW••••••••');
    expect(device.stateAgeText).toBe('Connected Just now');
  });

  it('connected-wifi: Wi-Fi transport, address present, exact age', () => {
    const parsed = parseSnapshot({
      type: 'snapshot',
      protocol: 1,
      snapshot: fixtures['connected-wifi'],
    });
    expect(parsed).not.toBeNull();
    expect(parsed!.devices).toHaveLength(1);

    const device = mapDeviceViewToDeviceInfo(parsed!.devices[0]!, false, parsed!.generatedAt);
    expect(device.name).toBe('Galaxy S24');
    expect(device.transport).toBe('wifi');
    expect(device.address).toBe('192.168.1.144:39421');
    expect(device.battery).toBe(82);
    expect(device.stateAgeText).toBe('Connected 46s ago');
  });

  it('expanded: stateAgeText is 2 minutes ago', () => {
    const parsed = parseSnapshot({
      type: 'snapshot',
      protocol: 1,
      snapshot: fixtures['expanded'],
    });
    expect(parsed).not.toBeNull();
    const device = mapDeviceViewToDeviceInfo(parsed!.devices[0]!, true, parsed!.generatedAt);
    expect(device.stateAgeText).toBe('Connected 2 minutes ago');
    expect(device.isExpanded).toBe(true);
  });

  it('unauthorized: attention severity, no battery, Just now', () => {
    const parsed = parseSnapshot({
      type: 'snapshot',
      protocol: 1,
      snapshot: fixtures['unauthorized'],
    });
    expect(parsed).not.toBeNull();
    const device = mapDeviceViewToDeviceInfo(parsed!.devices[0]!, false, parsed!.generatedAt);
    expect(device.state).toBe('unauthorized');
    expect(device.battery).toBeUndefined();
    expect(device.stateAgeText).toBe('Just now');
  });

  it('offline: offline state, 4m ago', () => {
    const parsed = parseSnapshot({
      type: 'snapshot',
      protocol: 1,
      snapshot: fixtures['offline'],
    });
    expect(parsed).not.toBeNull();
    const device = mapDeviceViewToDeviceInfo(parsed!.devices[0]!, false, parsed!.generatedAt);
    expect(device.state).toBe('offline');
    expect(device.stateAgeText).toBe('4m ago');
  });

  it('recovery: recovery state, 8m ago', () => {
    const parsed = parseSnapshot({
      type: 'snapshot',
      protocol: 1,
      snapshot: fixtures['recovery'],
    });
    expect(parsed).not.toBeNull();
    const device = mapDeviceViewToDeviceInfo(parsed!.devices[0]!, false, parsed!.generatedAt);
    expect(device.state).toBe('recovery');
    expect(device.stateAgeText).toBe('8m ago');
  });

  it('bootloader, sideload, unknown: Just now', () => {
    for (const fixtureName of ['bootloader', 'sideload', 'unknown'] as const) {
      const parsed = parseSnapshot({
        type: 'snapshot',
        protocol: 1,
        snapshot: fixtures[fixtureName],
      });
      expect(parsed).not.toBeNull();
      const device = mapDeviceViewToDeviceInfo(parsed!.devices[0]!, false, parsed!.generatedAt);
      expect(device.stateAgeText).toBe('Just now');
      expect(device.state).toBe(fixtureName);
    }
  });

  it('multi-device: 6 devices, exact ordering, specs, and age strings', () => {
    const parsed = parseSnapshot({
      type: 'snapshot',
      protocol: 1,
      snapshot: fixtures['multi-device'],
    });
    expect(parsed).not.toBeNull();
    expect(parsed!.devices).toHaveLength(6);
    expect(parsed!.deviceCountLabel).toBe('6 devices');

    const devices = parsed!.devices.map((d) =>
      mapDeviceViewToDeviceInfo(d, false, parsed!.generatedAt),
    );

    // d1
    expect(devices[0]!.id).toBe('d1');
    expect(devices[0]!.name).toBe('Galaxy S24');
    expect(devices[0]!.transport).toBe('wifi');
    expect(devices[0]!.stateAgeText).toBe('1m ago');

    // d2
    expect(devices[1]!.id).toBe('d2');
    expect(devices[1]!.name).toBe('Pixel 8');
    expect(devices[1]!.transport).toBe('usb');
    expect(devices[1]!.address).toBeUndefined();
    expect(devices[1]!.battery).toBe(67);
    expect(devices[1]!.stateAgeText).toBe('3m ago');

    // d3
    expect(devices[2]!.id).toBe('d3');
    expect(devices[2]!.name).toBe('Pixel 5');
    expect(devices[2]!.state).toBe('unauthorized');
    expect(devices[2]!.transport).toBe('usb');
    expect(devices[2]!.stateAgeText).toBe('Just now');

    // d4
    expect(devices[3]!.id).toBe('d4');
    expect(devices[3]!.name).toBe('Galaxy Tab S10');
    expect(devices[3]!.state).toBe('recovery');
    expect(devices[3]!.transport).toBe('usb');
    expect(devices[3]!.stateAgeText).toBe('10m ago');

    // d5
    expect(devices[4]!.id).toBe('d5');
    expect(devices[4]!.name).toBe('OnePlus 13');
    expect(devices[4]!.state).toBe('offline');
    expect(devices[4]!.transport).toBe('wifi');
    expect(devices[4]!.address).toBe('192.168.1.189:41235');
    expect(devices[4]!.stateAgeText).toBe('22m ago');

    // d6
    expect(devices[5]!.id).toBe('d6');
    expect(devices[5]!.name).toBe('Nothing Phone');
    expect(devices[5]!.state).toBe('connected');
    expect(devices[5]!.transport).toBe('usb');
    expect(devices[5]!.battery).toBe(74);
    expect(devices[5]!.stateAgeText).toBe('Just now');
  });

  it('pairing states: offer, code, progress, success, failure', () => {
    // offer
    const offerParsed = parseSnapshot({
      type: 'snapshot',
      protocol: 1,
      snapshot: fixtures['pairing-offer'],
    });
    const offerPairing = derivePairingState(offerParsed);
    expect(offerPairing).toEqual({
      isPairing: true,
      state: 'pairing-offer',
      deviceName: 'Galaxy S24',
      offerId: 'offer-1',
    });

    // code
    const codeParsed = parseSnapshot({
      type: 'snapshot',
      protocol: 1,
      snapshot: fixtures['pairing-code'],
    });
    const codePairing = derivePairingState(codeParsed);
    expect(codePairing).toEqual({
      isPairing: true,
      state: 'pairing-code',
      deviceName: 'Galaxy S24',
      offerId: 'offer-1',
    });

    // progress
    const progParsed = parseSnapshot({
      type: 'snapshot',
      protocol: 1,
      snapshot: fixtures['pairing-progress'],
    });
    const progPairing = derivePairingState(progParsed);
    expect(progPairing).toEqual({
      isPairing: true,
      state: 'pairing-progress',
      deviceName: 'Galaxy S24',
      offerId: 'offer-1',
    });

    // success
    const succParsed = parseSnapshot({
      type: 'snapshot',
      protocol: 1,
      snapshot: fixtures['pairing-success'],
    });
    const succPairing = derivePairingState(succParsed);
    expect(succPairing).toEqual({
      isPairing: true,
      state: 'pairing-success',
      deviceName: 'Galaxy S24',
      offerId: 'offer-1',
    });

    // failure
    const failParsed = parseSnapshot({
      type: 'snapshot',
      protocol: 1,
      snapshot: fixtures['pairing-failure'],
    });
    const failPairing = derivePairingState(failParsed);
    expect(failPairing).toEqual({
      isPairing: true,
      state: 'pairing-failure',
      deviceName: 'Galaxy S24',
      offerId: 'offer-1',
    });
  });

  it('age formatting helper reproduces expected strings', () => {
    const t0 = '2026-09-17T12:00:00.000Z';
    expect(formatAge('Connected {age}', t0, t0)).toBe('Connected Just now');
    expect(formatAge('Connected {age}', '2026-09-17T11:59:14.000Z', t0)).toBe('Connected 46s ago');
    expect(formatAge('{age}', '2026-09-17T11:59:00.000Z', t0)).toBe('1m ago');
    expect(formatAge('Connected {age}', '2026-09-17T11:58:00.000Z', t0)).toBe(
      'Connected 2 minutes ago',
    );
    expect(formatAge('{age}', '2026-09-17T11:57:00.000Z', t0)).toBe('3m ago');
    expect(formatAge('{age}', '2026-09-17T11:56:00.000Z', t0)).toBe('4m ago');
    expect(formatAge('{age}', '2026-09-17T11:52:00.000Z', t0)).toBe('8m ago');
    expect(formatAge('{age}', '2026-09-17T11:50:00.000Z', t0)).toBe('10m ago');
    expect(formatAge('{age}', '2026-09-17T11:38:00.000Z', t0)).toBe('22m ago');
  });
});
