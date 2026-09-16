import React from 'react';
import { Header } from './components/Header';
import { EmptyState } from './components/EmptyState';
import { DeviceCard } from './components/DeviceCard';
import { PairingCard } from './components/PairingCard';
import type { FixtureName, DeviceInfo } from './types';

export function App() {
  const [isPinned, setIsPinned] = React.useState(false);

  const [fixture, setFixture] = React.useState<FixtureName>(() => {
    if (typeof window !== 'undefined') {
      const params = new URLSearchParams(window.location.search);
      const f = params.get('fixture') as FixtureName;
      if (f) return f;
    }
    return 'connected-wifi';
  });

  const [showFixtureMenu, setShowFixtureMenu] = React.useState(false);
  const [copiedId, setCopiedId] = React.useState<'serial' | 'address' | null>(null);

  React.useEffect(() => {
    const handler = (e: MessageEvent) => {
      if (e.data === 'pinned:true') setIsPinned(true);
      if (e.data === 'pinned:false') setIsPinned(false);
    };
    // @ts-expect-error WebView2 injects chrome.webview natively
    window.chrome?.webview?.addEventListener('message', handler);
    // @ts-expect-error WebView2 injects chrome.webview natively
    return () => window.chrome?.webview?.removeEventListener('message', handler);
  }, []);

  // Fixture toggle hotkey Ctrl+Shift+F
  React.useEffect(() => {
    const keydown = (e: KeyboardEvent) => {
      if (e.ctrlKey && e.shiftKey && e.key.toLowerCase() === 'f') {
        setShowFixtureMenu((prev) => !prev);
      }
    };
    window.addEventListener('keydown', keydown);
    return () => window.removeEventListener('keydown', keydown);
  }, []);

  const handleCopy = (_text: string, type: 'serial' | 'address') => {
    // In production we'd use native clipboard APIs via Bridge
    setCopiedId(type);
    setTimeout(() => setCopiedId(null), 1200);
  };

  const getFixtureDevices = (): DeviceInfo[] => {
    const galaxyS24: DeviceInfo = {
      id: 'd1',
      name: 'Galaxy S24',
      state: 'connected',
      transport: 'wifi',
      battery: 82,
      stateAgeText: 'Connected 46s ago',
      model: 'SM-S921W',
      androidVersion: '16',
      address: '192.168.1.144:39421',
      serial: 'R5CW••••••••',
      isExpanded: fixture === 'expanded',
    };

    switch (fixture) {
      case 'empty':
        return [];
      case 'connected-usb':
        return [{ ...galaxyS24, transport: 'usb', stateAgeText: 'Connected Just now' }];
      case 'connected-wifi':
        return [galaxyS24];
      case 'expanded':
        return [{ ...galaxyS24, stateAgeText: 'Connected 2 minutes ago' }];
      case 'unauthorized':
        return [
          { ...galaxyS24, state: 'unauthorized', battery: undefined, stateAgeText: 'Just now' },
        ];
      case 'offline':
        return [{ ...galaxyS24, state: 'offline', battery: undefined, stateAgeText: '4m ago' }];
      case 'recovery':
        return [{ ...galaxyS24, state: 'recovery', battery: undefined, stateAgeText: '8m ago' }];
      case 'bootloader':
        return [
          { ...galaxyS24, state: 'bootloader', battery: undefined, stateAgeText: 'Just now' },
        ];
      case 'sideload':
        return [{ ...galaxyS24, state: 'sideload', battery: undefined, stateAgeText: 'Just now' }];
      case 'unknown':
        return [{ ...galaxyS24, state: 'unknown', battery: undefined, stateAgeText: 'Just now' }];
      case 'multi-device':
        return [
          { ...galaxyS24, stateAgeText: '1m ago' },
          {
            id: 'd2',
            name: 'Pixel 8',
            state: 'connected',
            transport: 'usb',
            battery: 67,
            stateAgeText: '3m ago',
          },
          {
            id: 'd3',
            name: 'Pixel 5',
            state: 'unauthorized',
            transport: 'usb',
            stateAgeText: 'Just now',
          },
          {
            id: 'd4',
            name: 'Galaxy Tab S10',
            state: 'recovery',
            transport: 'usb',
            stateAgeText: '10m ago',
          },
          {
            id: 'd5',
            name: 'OnePlus 13',
            state: 'offline',
            transport: 'wifi',
            stateAgeText: '22m ago',
          },
          {
            id: 'd6',
            name: 'Nothing Phone',
            state: 'connected',
            transport: 'usb',
            battery: 74,
            stateAgeText: 'Just now',
          },
        ];
      default:
        return []; // pairing states
    }
  };

  const isPairingState = fixture.startsWith('pairing');
  const devices = getFixtureDevices();

  return (
    <main className="h-screen w-full flex flex-col p-4 box-border">
      <Header isPinned={isPinned} />

      <div
        className="flex-1 overflow-y-auto space-y-3 pb-2"
        style={{ WebkitAppRegion: 'no-drag' } as React.CSSProperties}
      >
        {fixture === 'empty' && <EmptyState />}

        {isPairingState && (
          <PairingCard
            state={fixture as React.ComponentProps<typeof PairingCard>['state']}
            deviceName="Galaxy S24"
          />
        )}

        {devices.map((d) => (
          <DeviceCard
            key={d.id}
            device={d}
            copiedId={copiedId}
            onCopy={handleCopy}
            onToggleExpand={() => {
              if (fixture === 'expanded') {
                setFixture('connected-wifi');
              } else if (fixture === 'connected-wifi') {
                setFixture('expanded');
              }
            }}
          />
        ))}
      </div>

      {showFixtureMenu && (
        <div
          className="absolute top-14 right-4 bg-win-base border border-win-borderSubtle rounded shadow-xl p-2 z-50 text-xs w-48 max-h-64 overflow-y-auto"
          style={{ WebkitAppRegion: 'no-drag' } as React.CSSProperties}
        >
          <div className="font-semibold text-neutral-400 mb-2 px-1">Fixtures (Ctrl+Shift+F)</div>
          {(
            [
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
              'pairing-offer',
              'pairing-code',
              'pairing-incomplete',
              'pairing-ready',
              'pairing-progress',
              'pairing-success',
              'pairing-failure',
              'multi-device',
            ] as FixtureName[]
          ).map((f) => (
            <button
              key={f}
              onClick={() => setFixture(f)}
              className={`block w-full text-left px-2 py-1 rounded transition-colors ${fixture === f ? 'bg-blue-500/20 text-blue-400' : 'hover:bg-white/10 text-neutral-300'}`}
            >
              {f}
            </button>
          ))}
        </div>
      )}
    </main>
  );
}
