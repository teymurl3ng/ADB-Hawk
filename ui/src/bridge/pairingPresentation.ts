/**
 * Presentation-only pairing success dwell.
 * Backend AppSnapshot remains authoritative; this only delays removing the success UI.
 */
import type { AppSnapshot } from '../contract/snapshot';
import { derivePairingState } from './adapters';

/** Minimum perceptible success confirmation — no governed DESIGN dwell found; use 750 ms. */
export const PAIRING_SUCCESS_DWELL_MS = 750;

export interface PairingPresentationState {
  cardState:
    'pairing-offer' | 'pairing-code' | 'pairing-progress' | 'pairing-success' | 'pairing-failure';
  deviceName: string;
  offerId: string;
  layoutPhase: string | null;
  dwellingSuccess: boolean;
  /** Device ids frozen into structural layout key while success dwell is active. */
  layoutDeviceIds: string[];
}

export interface DwellController {
  dwellUntilMs: number;
  lastSuccessOfferId: string | null;
  lastSuccessName: string | null;
  /** Device set at dwell start — excludes mid-dwell Wi-Fi arrival from layout key. */
  frozenLayoutDeviceIds: string[];
}

export function createDwellController(): DwellController {
  return {
    dwellUntilMs: 0,
    lastSuccessOfferId: null,
    lastSuccessName: null,
    frozenLayoutDeviceIds: [],
  };
}

/**
 * Merge authoritative snapshot with local success dwell.
 * Cancels dwell on failure / ADB unavailable/restoring.
 */
export function resolvePairingPresentation(
  snapshot: AppSnapshot | null,
  dwell: DwellController,
  nowMs: number,
): {
  presentation: PairingPresentationState | null;
  dwell: DwellController;
  dwellExpired: boolean;
} {
  if (!snapshot) {
    return { presentation: null, dwell: createDwellController(), dwellExpired: false };
  }

  const healthKind = snapshot.health.kind;
  if (healthKind === 'adbUnavailable' || healthKind === 'adbRestoring') {
    return {
      presentation: deriveToPresentation(snapshot),
      dwell: createDwellController(),
      dwellExpired: false,
    };
  }

  const derived = derivePairingState(snapshot);
  const phase = snapshot.pairing?.phase ?? null;
  const liveDeviceIds = snapshot.devices.map((d) => d.id);

  if (phase === 'failed' && derived) {
    return {
      presentation: {
        cardState: 'pairing-failure',
        deviceName: derived.deviceName,
        offerId: derived.offerId,
        layoutPhase: 'failed',
        dwellingSuccess: false,
        layoutDeviceIds: liveDeviceIds,
      },
      dwell: createDwellController(),
      dwellExpired: false,
    };
  }

  if (phase === 'pairedWaitingForDevice' && derived) {
    const alreadyDwelling =
      dwell.dwellUntilMs > nowMs && dwell.lastSuccessOfferId === derived.offerId;
    const nextDwell: DwellController = alreadyDwelling
      ? dwell
      : {
          dwellUntilMs: nowMs + PAIRING_SUCCESS_DWELL_MS,
          lastSuccessOfferId: derived.offerId,
          lastSuccessName: derived.deviceName,
          // Freeze pre-arrival set so Wi-Fi insert during dwell does not retarget motion.
          frozenLayoutDeviceIds: [...liveDeviceIds],
        };
    return {
      presentation: {
        cardState: 'pairing-success',
        deviceName: derived.deviceName,
        offerId: derived.offerId,
        layoutPhase: 'pairedWaitingForDevice',
        dwellingSuccess: true,
        layoutDeviceIds: nextDwell.frozenLayoutDeviceIds,
      },
      dwell: nextDwell,
      dwellExpired: false,
    };
  }

  // Backend cleared pairing (device arrived / session ended) but dwell still active.
  if (
    !snapshot.pairing &&
    dwell.dwellUntilMs > 0 &&
    nowMs < dwell.dwellUntilMs &&
    dwell.lastSuccessOfferId
  ) {
    return {
      presentation: {
        cardState: 'pairing-success',
        deviceName: dwell.lastSuccessName ?? 'Android device',
        offerId: dwell.lastSuccessOfferId,
        layoutPhase: 'pairedWaitingForDevice',
        dwellingSuccess: true,
        layoutDeviceIds: dwell.frozenLayoutDeviceIds,
      },
      dwell,
      dwellExpired: false,
    };
  }

  if (dwell.dwellUntilMs > 0 && nowMs >= dwell.dwellUntilMs) {
    return {
      presentation: deriveToPresentation(snapshot),
      dwell: createDwellController(),
      dwellExpired: true,
    };
  }

  return {
    presentation: deriveToPresentation(snapshot),
    dwell,
    dwellExpired: false,
  };
}

function deriveToPresentation(snapshot: AppSnapshot): PairingPresentationState | null {
  const d = derivePairingState(snapshot);
  if (!d) return null;
  const layoutPhase = snapshot.pairing?.phase ?? (d.state === 'pairing-offer' ? 'offer' : null);
  return {
    cardState: d.state as PairingPresentationState['cardState'],
    deviceName: d.deviceName,
    offerId: d.offerId,
    layoutPhase,
    dwellingSuccess: false,
    layoutDeviceIds: snapshot.devices.map((x) => x.id),
  };
}

export function presentationLayoutPhase(
  presentation: PairingPresentationState | null,
): string | null {
  if (presentation?.dwellingSuccess) return 'pairedWaitingForDevice';
  return presentation?.layoutPhase ?? null;
}
