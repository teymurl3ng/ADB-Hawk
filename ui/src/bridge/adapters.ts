import type { AppSnapshot, DeviceView } from '../contract/snapshot';
import type { DeviceInfo } from '../types';
import { formatAge } from './age';

export type PairingCardState =
  | 'pairing-offer'
  | 'pairing-code'
  | 'pairing-incomplete'
  | 'pairing-ready'
  | 'pairing-progress'
  | 'pairing-success'
  | 'pairing-failure';

/**
 * Pure presentation adapter mapping native DeviceView to DeviceCard's DeviceInfo.
 */
export function mapDeviceViewToDeviceInfo(
  device: DeviceView,
  isExpanded: boolean,
  nowIso: string,
): DeviceInfo {
  return {
    id: device.id,
    name: device.displayName,
    state: device.state as DeviceInfo['state'],
    transport: device.transport,
    battery: device.batteryPercent ?? undefined,
    stateAgeText: formatAge(device.metaTemplate, device.stateSince, nowIso),
    isExpanded,
    model: device.detail.exactModel ?? undefined,
    androidVersion: device.detail.androidVersion ?? undefined,
    address: device.transport === 'usb' ? undefined : (device.detail.address ?? undefined),
    serial: device.detail.serial ?? undefined,
  };
}

/**
 * Maps PairingOperationView phase to PairingCard visual state.
 */
export function mapPairingPhaseToCardState(phase: string): PairingCardState {
  switch (phase) {
    case 'awaitingCode':
      return 'pairing-code';
    case 'pairing':
      return 'pairing-progress';
    case 'pairedWaitingForDevice':
      return 'pairing-success';
    case 'failed':
      return 'pairing-failure';
    default:
      return 'pairing-code';
  }
}

export interface DerivedPairingState {
  isPairing: boolean;
  state: PairingCardState;
  deviceName: string;
  offerId: string;
  /** Backend-selected failure supporting line (decision 0007); failure card only. */
  detail?: string;
}

/**
 * Derives pairing presentation state from snapshot.
 */
export function derivePairingState(snapshot: AppSnapshot | null): DerivedPairingState | null {
  if (!snapshot) return null;

  if (snapshot.pairing) {
    return {
      isPairing: true,
      state: mapPairingPhaseToCardState(snapshot.pairing.phase),
      deviceName: snapshot.pairing.displayName,
      offerId: snapshot.pairing.offerId,
      detail: snapshot.pairing.detail ?? undefined,
    };
  }

  if (snapshot.pairingOffers && snapshot.pairingOffers.length > 0) {
    const offer = snapshot.pairingOffers[0];
    if (offer) {
      return {
        isPairing: true,
        state: 'pairing-offer',
        deviceName: offer.displayName,
        offerId: offer.id,
      };
    }
  }

  return null;
}
