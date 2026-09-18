import React from 'react';
import { Header } from './components/Header';
import { EmptyState } from './components/EmptyState';
import { DeviceCard } from './components/DeviceCard';
import { PairingCard } from './components/PairingCard';
import { useSnapshot } from './bridge/useSnapshot';
import { useContentSize } from './bridge/useContentSize';
import { isTestMode, requestDismiss } from './bridge/actions';
import type { FixtureName, DeviceInfo } from './types';

const FIXTURES: FixtureName[] = [
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
  'pairing-incomplete',
  'pairing-ready',
  'pairing-progress',
  'pairing-success',
  'pairing-failure',
];

export function App() {
  const snapshot = useSnapshot();
  const isPinned = snapshot?.shell.pinned ?? false;

  const [fixture, setFixture] = React.useState<FixtureName>(() => {
    if (typeof window !== 'undefined') {
      const params = new URLSearchParams(window.location.search);
      const f = params.get('fixture') as FixtureName;
      if (f) return f;
    }
    return 'connected-wifi';
  });

  const [expandedIds, setExpandedIds] = React.useState<Set<string>>(() => {
    if (typeof window !== 'undefined') {
      const params = new URLSearchParams(window.location.search);
      const f = params.get('fixture') as FixtureName;
      if (f === 'expanded') return new Set(['d1']);
    }
    return new Set<string>();
  });

  const [showFixtureMenu, setShowFixtureMenu] = React.useState(false);
  const [copiedId, setCopiedId] = React.useState<'serial' | 'address' | null>(null);

  const headerRef = React.useRef<HTMLElement>(null);
  const contentWrapperRef = React.useRef<HTMLDivElement>(null);

  useContentSize({ headerRef, contentRef: contentWrapperRef }, [fixture, expandedIds]);

  React.useEffect(() => {
    setExpandedIds(fixture === 'expanded' ? new Set(['d1']) : new Set());
  }, [fixture]);

  // Handle Escape dismiss and fixture toggle hotkey Ctrl+Shift+F
  React.useEffect(() => {
    const keydown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        if (showFixtureMenu) {
          setShowFixtureMenu(false);
          return;
        }
        requestDismiss();
        return;
      }

      if (!isTestMode()) return;

      if (e.ctrlKey && e.shiftKey && (e.key.toLowerCase() === 'f' || e.code === 'KeyF')) {
        e.preventDefault();
        setShowFixtureMenu((prev) => !prev);
      }
    };
    window.addEventListener('keydown', keydown);
    return () => window.removeEventListener('keydown', keydown);
  }, [showFixtureMenu]);

  const handleCopy = (_text: string, type: 'serial' | 'address') => {
    setCopiedId(type);
    setTimeout(() => setCopiedId(null), 1200);
  };

  const handleToggleExpand = (id: string) => {
    setExpandedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
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
    };

    switch (fixture) {
      case 'empty':
        return [];
      case 'connected-usb':
        return [
          {
            ...galaxyS24,
            transport: 'usb',
            address: undefined, // USB device must NOT have Address
            stateAgeText: 'Connected Just now',
          },
        ];
      case 'connected-wifi':
        return [galaxyS24];
      case 'expanded':
        return [
          {
            ...galaxyS24,
            stateAgeText: 'Connected 2 minutes ago',
          },
        ];
      case 'unauthorized':
        return [
          {
            ...galaxyS24,
            state: 'unauthorized',
            battery: undefined,
            stateAgeText: 'Just now',
          },
        ];
      case 'offline':
        return [
          {
            ...galaxyS24,
            state: 'offline',
            battery: undefined,
            stateAgeText: '4m ago',
          },
        ];
      case 'recovery':
        return [
          {
            ...galaxyS24,
            state: 'recovery',
            battery: undefined,
            stateAgeText: '8m ago',
          },
        ];
      case 'bootloader':
        return [
          {
            ...galaxyS24,
            state: 'bootloader',
            battery: undefined,
            stateAgeText: 'Just now',
          },
        ];
      case 'sideload':
        return [
          {
            ...galaxyS24,
            state: 'sideload',
            battery: undefined,
            stateAgeText: 'Just now',
          },
        ];
      case 'unknown':
        return [
          {
            ...galaxyS24,
            state: 'unknown',
            battery: undefined,
            stateAgeText: 'Just now',
          },
        ];
      case 'multi-device':
        return [
          {
            ...galaxyS24,
            stateAgeText: '1m ago',
          },
          {
            id: 'd2',
            name: 'Pixel 8',
            state: 'connected',
            transport: 'usb',
            battery: 67,
            stateAgeText: '3m ago',
            model: 'Pixel 8',
            androidVersion: '15',
            serial: '3928••••••••',
          },
          {
            id: 'd3',
            name: 'Pixel 5',
            state: 'unauthorized',
            transport: 'usb',
            stateAgeText: 'Just now',
            model: 'Pixel 5',
            androidVersion: '14',
            serial: '1924••••••••',
          },
          {
            id: 'd4',
            name: 'Galaxy Tab S10',
            state: 'recovery',
            transport: 'usb',
            stateAgeText: '10m ago',
            model: 'SM-X920',
            androidVersion: '15',
            serial: 'R52X••••••••',
          },
          {
            id: 'd5',
            name: 'OnePlus 13',
            state: 'offline',
            transport: 'wifi',
            stateAgeText: '22m ago',
            model: 'CPH2653',
            androidVersion: '15',
            address: '192.168.1.189:41235',
            serial: 'OP13••••••••',
          },
          {
            id: 'd6',
            name: 'Nothing Phone',
            state: 'connected',
            transport: 'usb',
            battery: 74,
            stateAgeText: 'Just now',
            model: 'A063',
            androidVersion: '14',
            serial: 'NP2A••••••••',
          },
        ];
      default:
        return []; // pairing states have 0 devices
    }
  };

  const isPairingState = fixture.startsWith('pairing');
  const rawDevices = isPairingState ? [] : getFixtureDevices();
  const devices = rawDevices.map((d) => ({
    ...d,
    isExpanded: expandedIds.has(d.id),
  }));

  return (
    <main className="h-screen w-full flex flex-col p-4 box-border">
      <Header isPinned={isPinned} headerRef={headerRef} />

      <div
        className="flex-1 overflow-y-auto pb-2"
        style={{ WebkitAppRegion: 'no-drag' } as React.CSSProperties}
      >
        <div ref={contentWrapperRef} className="space-y-3">
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
              onToggleExpand={handleToggleExpand}
            />
          ))}
        </div>
      </div>

      {isTestMode() && showFixtureMenu && (
        <div
          className="fixed inset-0 bg-black/50 backdrop-blur-sm flex items-center justify-center z-50 p-4"
          onClick={() => setShowFixtureMenu(false)}
          style={{ WebkitAppRegion: 'no-drag' } as React.CSSProperties}
        >
          <div
            className="bg-win-base border border-win-card-border rounded-xl shadow-2xl p-4 w-80 max-h-[80vh] flex flex-col text-xs"
            onClick={(e) => e.stopPropagation()}
          >
            <div className="flex items-center justify-between pb-3 border-b border-win-border-subtle mb-3">
              <span className="font-semibold text-win-text-primary text-[13px]">
                Fixture Browser
              </span>
              <span className="text-win-text-muted text-[11px]">Esc to close</span>
            </div>
            <div className="overflow-y-auto space-y-1 pr-1 flex-1">
              {FIXTURES.map((f) => (
                <button
                  key={f}
                  onClick={() => {
                    setFixture(f);
                    setShowFixtureMenu(false);
                  }}
                  className={`w-full text-left px-2.5 py-1.5 rounded-md font-mono text-[11.5px] transition-colors flex items-center justify-between ${
                    fixture === f
                      ? 'bg-win-btn-hover text-win-text-primary font-medium'
                      : 'text-win-text-secondary hover:text-win-text-primary hover:bg-win-btn-hover'
                  }`}
                >
                  <span>{f}</span>
                  {fixture === f && <span className="text-win-green text-[10px]">●</span>}
                </button>
              ))}
            </div>
          </div>
        </div>
      )}
    </main>
  );
}
