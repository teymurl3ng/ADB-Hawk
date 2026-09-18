import React from 'react';
import { Header } from './components/Header';
import { EmptyState } from './components/EmptyState';
import { DeviceCard } from './components/DeviceCard';
import { PairingCard } from './components/PairingCard';
import { useSnapshot } from './bridge/useSnapshot';
import { useContentSize } from './bridge/useContentSize';
import { isTestMode, requestDismiss } from './bridge/actions';
import { mapDeviceViewToDeviceInfo, derivePairingState } from './bridge/adapters';
import type { FixtureName } from './types';

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
  'pairing-progress',
  'pairing-success',
  'pairing-failure',
];

export function App() {
  const snapshot = useSnapshot();
  const isPinned = snapshot?.shell.pinned ?? false;

  const [expandedIds, setExpandedIds] = React.useState<Set<string>>(() => {
    if (isTestMode() && typeof window !== 'undefined') {
      const params = new URLSearchParams(window.location.search);
      if (params.get('fixture') === 'expanded') return new Set(['d1']);
    }
    return new Set<string>();
  });

  const [showFixtureMenu, setShowFixtureMenu] = React.useState(false);
  const [copiedId, setCopiedId] = React.useState<'serial' | 'address' | null>(null);

  const headerRef = React.useRef<HTMLElement>(null);
  const contentWrapperRef = React.useRef<HTMLDivElement>(null);

  useContentSize({ headerRef, contentRef: contentWrapperRef }, [snapshot, expandedIds]);

  // Synchronize expansion state when snapshot updates:
  // - Retain expansion for devices that still exist
  // - Prune disappeared devices
  // - Ensure initial expansion in test mode for 'expanded' fixture
  React.useEffect(() => {
    if (!snapshot) return;
    const currentDeviceIds = new Set(snapshot.devices.map((d) => d.id));
    setExpandedIds((prev) => {
      const next = new Set<string>();
      for (const id of prev) {
        if (currentDeviceIds.has(id)) next.add(id);
      }
      if (isTestMode() && typeof window !== 'undefined') {
        const params = new URLSearchParams(window.location.search);
        if (params.get('fixture') === 'expanded' && currentDeviceIds.has('d1')) {
          next.add('d1');
        }
      }
      return next;
    });
  }, [snapshot]);

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

  const devices = snapshot?.devices ?? [];
  const pairingInfo = derivePairingState(snapshot);
  const isEmpty = devices.length === 0 && !pairingInfo;
  const nowIso = snapshot?.generatedAt ?? '';

  const activeFixture =
    typeof window !== 'undefined'
      ? new URLSearchParams(window.location.search).get('fixture') || 'connected-wifi'
      : 'connected-wifi';

  return (
    <main className="h-screen w-full flex flex-col p-4 box-border">
      <Header isPinned={isPinned} headerRef={headerRef} />

      <div
        className="flex-1 overflow-y-auto pb-2"
        style={{ WebkitAppRegion: 'no-drag' } as React.CSSProperties}
      >
        <div ref={contentWrapperRef} className="space-y-3">
          {isEmpty && <EmptyState />}

          {pairingInfo && (
            <PairingCard state={pairingInfo.state} deviceName={pairingInfo.deviceName} />
          )}

          {devices.map((d) => (
            <DeviceCard
              key={d.id}
              device={mapDeviceViewToDeviceInfo(d, expandedIds.has(d.id), nowIso)}
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
                    setShowFixtureMenu(false);
                    window.location.search = `?fixture=${f}`;
                  }}
                  className={`w-full text-left px-2.5 py-1.5 rounded-md font-mono text-[11.5px] transition-colors flex items-center justify-between ${
                    activeFixture === f
                      ? 'bg-win-btn-hover text-win-text-primary font-medium'
                      : 'text-win-text-secondary hover:text-win-text-primary hover:bg-win-btn-hover'
                  }`}
                >
                  <span>{f}</span>
                  {activeFixture === f && <span className="text-win-green text-[10px]">●</span>}
                </button>
              ))}
            </div>
          </div>
        </div>
      )}
    </main>
  );
}
