export type FixtureName =
  | 'empty'
  | 'connected-usb'
  | 'connected-wifi'
  | 'expanded'
  | 'unauthorized'
  | 'offline'
  | 'recovery'
  | 'bootloader'
  | 'sideload'
  | 'unknown'
  | 'pairing-offer'
  | 'pairing-code'
  | 'pairing-incomplete'
  | 'pairing-ready'
  | 'pairing-progress'
  | 'pairing-success'
  | 'pairing-failure'
  | 'multi-device';

export type DeviceState =
  'connected' | 'unauthorized' | 'offline' | 'recovery' | 'bootloader' | 'sideload' | 'unknown';
export type DeviceTransport = 'usb' | 'wifi' | 'unknown';

export interface DeviceInfo {
  id: string;
  name: string;
  state: DeviceState;
  transport: DeviceTransport;
  battery?: number; // 0-100
  stateAgeText: string;
  isExpanded?: boolean;

  // Expanded only
  model?: string;
  androidVersion?: string;
  address?: string;
  serial?: string;
}
