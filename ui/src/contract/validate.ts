import { PROTOCOL_VERSION } from './snapshot';
import type {
  AppSnapshot,
  GlobalHealthKind,
  TrayStatusKind,
  ThemeKind,
  SeverityKind,
  TransportKind,
  DeviceStateKind,
  PairingPhase,
  SubsystemKind,
  DiscoveryHealthView,
} from './snapshot';

const VALID_GLOBAL_HEALTH = new Set<string>(['healthy', 'adbUnavailable', 'adbRestoring']);
const VALID_TRAY_STATUS = new Set<string>([
  'idle',
  'connected',
  'attention',
  'failure',
  'special',
  'pairingAvailable',
]);
const VALID_THEME = new Set<string>(['dark', 'light']);
const VALID_SEVERITY = new Set<string>([
  'positive',
  'attention',
  'failure',
  'informational',
  'neutral',
]);
const VALID_TRANSPORT = new Set<string>(['usb', 'wifi', 'unknown']);
const VALID_DEVICE_STATE = new Set<string>([
  'connected',
  'unauthorized',
  'offline',
  'recovery',
  'bootloader',
  'sideload',
  'rescue',
  'authorizing',
  'connecting',
  'detached',
  'unknown',
]);
const VALID_PAIRING_PHASE = new Set<string>([
  'awaitingCode',
  'pairing',
  'pairedWaitingForDevice',
  'failed',
]);
const VALID_SUBSYSTEM = new Set<string>(['wirelessDiscovery']);

function isObject(val: unknown): val is Record<string, unknown> {
  return typeof val === 'object' && val !== null && !Array.isArray(val);
}

function isValidIsoDate(str: unknown): boolean {
  return typeof str === 'string' && !isNaN(Date.parse(str)) && /^\d{4}-\d{2}-\d{2}T/.test(str);
}

/**
 * Validates and parses an incoming native bridge message or AppSnapshot envelope.
 * Returns the strongly-typed AppSnapshot if valid, or null if validation fails.
 * Tolerates unknown extra properties for forward compatibility.
 */
export function parseSnapshot(raw: unknown): AppSnapshot | null {
  let message: unknown = raw;

  if (typeof message === 'string') {
    try {
      message = JSON.parse(message);
    } catch (e) {
      console.warn('parseSnapshot: malformed JSON string', e);
      return null;
    }
  }

  if (!isObject(message)) {
    console.warn('parseSnapshot: message must be an object');
    return null;
  }

  // Envelope validation
  if (message.type !== 'snapshot') {
    console.warn(
      `parseSnapshot: expected envelope type 'snapshot', received '${String(message.type)}'`,
    );
    return null;
  }

  if (message.protocol !== PROTOCOL_VERSION) {
    console.warn(
      `parseSnapshot: expected envelope protocol ${PROTOCOL_VERSION}, received ${String(message.protocol)}`,
    );
    return null;
  }

  if (!isObject(message.snapshot)) {
    console.warn('parseSnapshot: envelope.snapshot must be a non-null object');
    return null;
  }

  const s = message.snapshot;

  // Protocol invariant
  if (s.protocol !== PROTOCOL_VERSION) {
    console.warn(
      `parseSnapshot: snapshot.protocol (${String(s.protocol)}) does not match envelope protocol (${PROTOCOL_VERSION})`,
    );
    return null;
  }

  // Revision validation: safe integer >= 1
  if (typeof s.revision !== 'number' || !Number.isSafeInteger(s.revision) || s.revision < 1) {
    console.warn(
      `parseSnapshot: revision must be a safe positive integer (>= 1), received ${String(s.revision)}`,
    );
    return null;
  }

  // GeneratedAt timestamp
  if (!isValidIsoDate(s.generatedAt)) {
    console.warn(
      `parseSnapshot: generatedAt must be a valid ISO-8601 UTC timestamp string, received ${String(s.generatedAt)}`,
    );
    return null;
  }

  // Health section
  if (!isObject(s.health)) {
    console.warn('parseSnapshot: health section must be an object');
    return null;
  }
  if (!VALID_GLOBAL_HEALTH.has(s.health.kind as string)) {
    console.warn(`parseSnapshot: invalid health.kind '${String(s.health.kind)}'`);
    return null;
  }
  if (s.health.title !== null && typeof s.health.title !== 'string') {
    console.warn('parseSnapshot: health.title must be string or null');
    return null;
  }
  if (s.health.detail !== null && typeof s.health.detail !== 'string') {
    console.warn('parseSnapshot: health.detail must be string or null');
    return null;
  }
  if (s.health.hint !== null && typeof s.health.hint !== 'string') {
    console.warn('parseSnapshot: health.hint must be string or null');
    return null;
  }
  if (!VALID_TRAY_STATUS.has(s.health.tray as string)) {
    console.warn(`parseSnapshot: invalid health.tray '${String(s.health.tray)}'`);
    return null;
  }
  if (typeof s.health.trayTooltip !== 'string') {
    console.warn('parseSnapshot: health.trayTooltip must be a string');
    return null;
  }

  // Shell section
  if (!isObject(s.shell)) {
    console.warn('parseSnapshot: shell section must be an object');
    return null;
  }
  if (typeof s.shell.pinned !== 'boolean') {
    console.warn('parseSnapshot: shell.pinned must be a boolean');
    return null;
  }
  if (!VALID_THEME.has(s.shell.theme as string)) {
    console.warn(`parseSnapshot: invalid shell.theme '${String(s.shell.theme)}'`);
    return null;
  }
  if (typeof s.shell.transparencyEnabled !== 'boolean') {
    console.warn('parseSnapshot: shell.transparencyEnabled must be a boolean');
    return null;
  }
  if (typeof s.shell.forcedColors !== 'boolean') {
    console.warn('parseSnapshot: shell.forcedColors must be a boolean');
    return null;
  }
  if (typeof s.shell.reducedMotion !== 'boolean') {
    console.warn('parseSnapshot: shell.reducedMotion must be a boolean');
    return null;
  }

  // Devices section
  if (!Array.isArray(s.devices)) {
    console.warn('parseSnapshot: devices must be an array');
    return null;
  }
  for (let i = 0; i < s.devices.length; i++) {
    const d = s.devices[i];
    if (!isObject(d)) {
      console.warn(`parseSnapshot: device[${i}] must be an object`);
      return null;
    }
    if (typeof d.id !== 'string') {
      console.warn(`parseSnapshot: device[${i}].id must be a string`);
      return null;
    }
    if (typeof d.displayName !== 'string') {
      console.warn(`parseSnapshot: device[${i}].displayName must be a string`);
      return null;
    }
    if (!VALID_DEVICE_STATE.has(d.state as string)) {
      console.warn(`parseSnapshot: device[${i}].state '${String(d.state)}' is invalid`);
      return null;
    }
    if (!VALID_SEVERITY.has(d.severity as string)) {
      console.warn(`parseSnapshot: device[${i}].severity '${String(d.severity)}' is invalid`);
      return null;
    }
    if (typeof d.stateLabel !== 'string') {
      console.warn(`parseSnapshot: device[${i}].stateLabel must be a string`);
      return null;
    }
    if (typeof d.statusLine !== 'string') {
      console.warn(`parseSnapshot: device[${i}].statusLine must be a string`);
      return null;
    }
    if (d.guidance !== null && typeof d.guidance !== 'string') {
      console.warn(`parseSnapshot: device[${i}].guidance must be string or null`);
      return null;
    }
    if (!VALID_TRANSPORT.has(d.transport as string)) {
      console.warn(`parseSnapshot: device[${i}].transport '${String(d.transport)}' is invalid`);
      return null;
    }
    if (typeof d.transportLabel !== 'string') {
      console.warn(`parseSnapshot: device[${i}].transportLabel must be a string`);
      return null;
    }
    if (d.batteryPercent !== null) {
      if (typeof d.batteryPercent !== 'number' || d.batteryPercent < 0 || d.batteryPercent > 100) {
        console.warn(
          `parseSnapshot: device[${i}].batteryPercent must be null or 0..100, received ${String(d.batteryPercent)}`,
        );
        return null;
      }
    }
    if (typeof d.metaTemplate !== 'string') {
      console.warn(`parseSnapshot: device[${i}].metaTemplate must be a string`);
      return null;
    }
    if (!isValidIsoDate(d.stateSince)) {
      console.warn(`parseSnapshot: device[${i}].stateSince must be a valid ISO date string`);
      return null;
    }
    if (d.connectedSince !== null && !isValidIsoDate(d.connectedSince)) {
      console.warn(
        `parseSnapshot: device[${i}].connectedSince must be a valid ISO date string or null`,
      );
      return null;
    }

    // Detail section
    if (!isObject(d.detail)) {
      console.warn(`parseSnapshot: device[${i}].detail must be an object`);
      return null;
    }
    if (!(d.detail.serial === null || typeof d.detail.serial === 'string')) {
      console.warn(`parseSnapshot: device[${i}].detail.serial must be a string or null`);
      return null;
    }
    if (d.detail.exactModel !== null && typeof d.detail.exactModel !== 'string') {
      console.warn(`parseSnapshot: device[${i}].detail.exactModel must be string or null`);
      return null;
    }
    if (d.detail.androidVersion !== null && typeof d.detail.androidVersion !== 'string') {
      console.warn(`parseSnapshot: device[${i}].detail.androidVersion must be string or null`);
      return null;
    }
    if (d.detail.address !== null && typeof d.detail.address !== 'string') {
      console.warn(`parseSnapshot: device[${i}].detail.address must be string or null`);
      return null;
    }
    if (!Array.isArray(d.detail.copyFields)) {
      console.warn(`parseSnapshot: device[${i}].detail.copyFields must be an array`);
      return null;
    }
    for (let j = 0; j < d.detail.copyFields.length; j++) {
      const f = d.detail.copyFields[j];
      if (
        !isObject(f) ||
        typeof f.key !== 'string' ||
        typeof f.label !== 'string' ||
        typeof f.value !== 'string'
      ) {
        console.warn(`parseSnapshot: device[${i}].detail.copyFields[${j}] is invalid`);
        return null;
      }
    }
  }

  // PairingOffers section
  if (!Array.isArray(s.pairingOffers)) {
    console.warn('parseSnapshot: pairingOffers must be an array');
    return null;
  }
  for (let i = 0; i < s.pairingOffers.length; i++) {
    const o = s.pairingOffers[i];
    if (
      !isObject(o) ||
      typeof o.id !== 'string' ||
      typeof o.displayName !== 'string' ||
      typeof o.heading !== 'string' ||
      typeof o.detail !== 'string' ||
      typeof o.actionLabel !== 'string'
    ) {
      console.warn(`parseSnapshot: pairingOffers[${i}] is invalid`);
      return null;
    }
  }

  // Pairing section (optional / nullable)
  if (s.pairing !== null) {
    if (!isObject(s.pairing)) {
      console.warn('parseSnapshot: pairing must be null or an object');
      return null;
    }
    if (typeof s.pairing.offerId !== 'string') {
      console.warn('parseSnapshot: pairing.offerId must be a string');
      return null;
    }
    if (typeof s.pairing.displayName !== 'string') {
      console.warn('parseSnapshot: pairing.displayName must be a string');
      return null;
    }
    if (!VALID_PAIRING_PHASE.has(s.pairing.phase as string)) {
      console.warn(`parseSnapshot: invalid pairing.phase '${String(s.pairing.phase)}'`);
      return null;
    }
    if (typeof s.pairing.title !== 'string') {
      console.warn('parseSnapshot: pairing.title must be a string');
      return null;
    }
    if (s.pairing.detail !== null && typeof s.pairing.detail !== 'string') {
      console.warn('parseSnapshot: pairing.detail must be string or null');
      return null;
    }
    if (typeof s.pairing.canSubmitCode !== 'boolean') {
      console.warn('parseSnapshot: pairing.canSubmitCode must be a boolean');
      return null;
    }
    if (typeof s.pairing.canCancel !== 'boolean') {
      console.warn('parseSnapshot: pairing.canCancel must be a boolean');
      return null;
    }
    if (typeof s.pairing.canRetry !== 'boolean') {
      console.warn('parseSnapshot: pairing.canRetry must be a boolean');
      return null;
    }
    if (s.pairing.codeLength !== 6) {
      console.warn(
        `parseSnapshot: pairing.codeLength must be 6, received ${String(s.pairing.codeLength)}`,
      );
      return null;
    }
  }

  // Warnings section
  if (!Array.isArray(s.warnings)) {
    console.warn('parseSnapshot: warnings must be an array');
    return null;
  }
  for (let i = 0; i < s.warnings.length; i++) {
    const w = s.warnings[i];
    if (
      !isObject(w) ||
      !VALID_SUBSYSTEM.has(w.subsystem as string) ||
      !VALID_SEVERITY.has(w.severity as string) ||
      typeof w.title !== 'string' ||
      typeof w.detail !== 'string'
    ) {
      console.warn(`parseSnapshot: warnings[${i}] is invalid`);
      return null;
    }
  }

  // DeviceCountLabel
  if (s.deviceCountLabel !== null && typeof s.deviceCountLabel !== 'string') {
    console.warn('parseSnapshot: deviceCountLabel must be string or null');
    return null;
  }

  // Discovery health (decision 0008 + amendment). Tolerates absence for forward compatibility.
  let discoveryHealth: DiscoveryHealthView | null = null;
  if (s.discoveryHealth !== undefined && s.discoveryHealth !== null) {
    const dh = s.discoveryHealth;
    if (
      !isObject(dh) ||
      typeof dh.title !== 'string' ||
      typeof dh.detail !== 'string' ||
      typeof dh.actionLabel !== 'string' ||
      typeof dh.canCheckAgain !== 'boolean' ||
      !Array.isArray(dh.evidence) ||
      !Array.isArray(dh.repairs) ||
      (dh.resultLine !== null && typeof dh.resultLine !== 'string')
    ) {
      console.warn('parseSnapshot: discoveryHealth is invalid');
      return null;
    }

    for (let i = 0; i < dh.evidence.length; i++) {
      if (typeof dh.evidence[i] !== 'string') {
        console.warn(`parseSnapshot: discoveryHealth.evidence[${i}] must be a string`);
        return null;
      }
    }

    const repairs: DiscoveryHealthView['repairs'] = [];
    for (let i = 0; i < dh.repairs.length; i++) {
      const r = dh.repairs[i];
      if (
        !isObject(r) ||
        typeof r.id !== 'string' ||
        typeof r.label !== 'string' ||
        typeof r.isAdmin !== 'boolean' ||
        typeof r.isRunning !== 'boolean' ||
        typeof r.runningLabel !== 'string'
      ) {
        console.warn(`parseSnapshot: discoveryHealth.repairs[${i}] is invalid`);
        return null;
      }

      repairs.push({
        id: r.id as string,
        label: r.label as string,
        isAdmin: r.isAdmin as boolean,
        isRunning: r.isRunning as boolean,
        runningLabel: r.runningLabel as string,
      });
    }

    discoveryHealth = {
      title: dh.title as string,
      detail: dh.detail as string,
      evidence: (dh.evidence as Array<Record<string, unknown>>).map(
        (line) => line as unknown as string,
      ),
      actionLabel: dh.actionLabel as string,
      canCheckAgain: dh.canCheckAgain as boolean,
      repairs,
      resultLine: (dh.resultLine as string | null) ?? null,
    };
  }

  return {
    protocol: s.protocol as number,
    revision: s.revision as number,
    generatedAt: s.generatedAt as string,
    health: {
      kind: s.health.kind as GlobalHealthKind,
      title: s.health.title as string | null,
      detail: s.health.detail as string | null,
      hint: s.health.hint as string | null,
      tray: s.health.tray as TrayStatusKind,
      trayTooltip: s.health.trayTooltip as string,
    },
    shell: {
      pinned: s.shell.pinned as boolean,
      theme: s.shell.theme as ThemeKind,
      transparencyEnabled: s.shell.transparencyEnabled as boolean,
      forcedColors: s.shell.forcedColors as boolean,
      reducedMotion: s.shell.reducedMotion as boolean,
    },
    devices: (s.devices as Array<Record<string, unknown>>).map((d) => {
      const detail = d.detail as Record<string, unknown>;
      const copyFields = detail.copyFields as Array<Record<string, unknown>>;
      return {
        id: d.id as string,
        displayName: d.displayName as string,
        state: d.state as DeviceStateKind,
        severity: d.severity as SeverityKind,
        stateLabel: d.stateLabel as string,
        statusLine: d.statusLine as string,
        guidance: d.guidance as string | null,
        transport: d.transport as TransportKind,
        transportLabel: d.transportLabel as string,
        batteryPercent: d.batteryPercent as number | null,
        metaTemplate: d.metaTemplate as string,
        stateSince: d.stateSince as string,
        connectedSince: d.connectedSince as string | null,
        detail: {
          serial: (detail.serial as string | null) ?? null,
          exactModel: detail.exactModel as string | null,
          androidVersion: detail.androidVersion as string | null,
          address: detail.address as string | null,
          copyFields: copyFields.map((f) => ({
            key: f.key as string,
            label: f.label as string,
            value: f.value as string,
          })),
        },
      };
    }),
    pairingOffers: (s.pairingOffers as Array<Record<string, unknown>>).map((o) => ({
      id: o.id as string,
      displayName: o.displayName as string,
      heading: o.heading as string,
      detail: o.detail as string,
      actionLabel: o.actionLabel as string,
    })),
    pairing: s.pairing
      ? {
          offerId: (s.pairing as Record<string, unknown>).offerId as string,
          displayName: (s.pairing as Record<string, unknown>).displayName as string,
          phase: (s.pairing as Record<string, unknown>).phase as PairingPhase,
          title: (s.pairing as Record<string, unknown>).title as string,
          detail: (s.pairing as Record<string, unknown>).detail as string | null,
          canSubmitCode: (s.pairing as Record<string, unknown>).canSubmitCode as boolean,
          canCancel: (s.pairing as Record<string, unknown>).canCancel as boolean,
          canRetry: (s.pairing as Record<string, unknown>).canRetry as boolean,
          codeLength: (s.pairing as Record<string, unknown>).codeLength as number,
        }
      : null,
    warnings: (s.warnings as Array<Record<string, unknown>>).map((w) => ({
      subsystem: w.subsystem as SubsystemKind,
      severity: w.severity as SeverityKind,
      title: w.title as string,
      detail: w.detail as string,
    })),
    discoveryHealth,
    deviceCountLabel: s.deviceCountLabel as string | null,
  };
}
