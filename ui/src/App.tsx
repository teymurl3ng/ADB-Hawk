import React from 'react';
import { Header } from './components/Header';
import { EmptyState } from './components/EmptyState';
import { DeviceCard } from './components/DeviceCard';
import { PairingCard } from './components/PairingCard';
import { PairingQrCard } from './components/PairingQrCard';
import { DiscoveryHealthCard } from './components/DiscoveryHealthCard';
import { useSnapshot } from './bridge/useSnapshot';
import { useContentSize } from './bridge/useContentSize';
import {
  isTestMode,
  requestDismiss,
  pairStart,
  pairCancel,
  pairSubmit,
  discoveryRecheck,
  discoveryRepair,
  pairQrStart,
  pairQrCancel,
} from './bridge/actions';
import { mapDeviceViewToDeviceInfo } from './bridge/adapters';
import {
  createDwellController,
  resolvePairingPresentation,
  type DwellController,
  type PairingPresentationState,
  PAIRING_SUCCESS_DWELL_MS,
} from './bridge/pairingPresentation';
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
  'discovery-health',
  'pairing-qr',
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
  const [presentation, setPresentation] = React.useState<PairingPresentationState | null>(null);
  const [dwellTick, setDwellTick] = React.useState(0);

  const headerRef = React.useRef<HTMLElement>(null);
  const contentWrapperRef = React.useRef<HTMLDivElement>(null);
  const scrollRef = React.useRef<HTMLDivElement>(null);
  const mainRef = React.useRef<HTMLElement>(null);
  const dwellRef = React.useRef<DwellController>(createDwellController());

  // Resolve presentation (success dwell) from authoritative snapshot.
  React.useLayoutEffect(() => {
    const now = performance.now();
    const resolved = resolvePairingPresentation(snapshot, dwellRef.current, now);
    dwellRef.current = resolved.dwell;
    setPresentation(resolved.presentation);

    if (resolved.presentation?.dwellingSuccess && resolved.dwell.dwellUntilMs > now) {
      const remain = resolved.dwell.dwellUntilMs - now;
      const t = window.setTimeout(() => setDwellTick((n) => n + 1), remain + 1);
      return () => window.clearTimeout(t);
    }
    return undefined;
  }, [snapshot, dwellTick]);

  // Host freezes HWND while visible; reports still feed next-show height.
  useContentSize({ headerRef, contentRef: contentWrapperRef }, [
    snapshot,
    expandedIds,
    presentation,
  ]);

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
    // Latest click wins immediately — no animation queue / FLIP.
    setExpandedIds((prev) => {
      if (prev.has(id)) return new Set<string>();
      return new Set<string>([id]);
    });
  };

  React.useEffect(() => {
    if (!isTestMode() || typeof window === 'undefined') return;
    const win = window as unknown as {
      __adbToggleExpand?: (id: string) => void;
      __adbPairingDwellMs?: number;
    };
    win.__adbToggleExpand = (deviceId: string) => handleToggleExpand(deviceId);
    win.__adbPairingDwellMs = PAIRING_SUCCESS_DWELL_MS;
    return () => {
      delete win.__adbToggleExpand;
      delete win.__adbPairingDwellMs;
    };
  }, [expandedIds, snapshot, presentation]);

  React.useEffect(() => {
    if (typeof document === 'undefined') return;

    const apply = () => {
      let theme: 'light' | 'dark' = snapshot?.shell.theme === 'light' ? 'light' : 'dark';
      if (isTestMode() && typeof window.matchMedia === 'function') {
        if (window.matchMedia('(prefers-color-scheme: light)').matches) {
          theme = 'light';
        } else if (window.matchMedia('(prefers-color-scheme: dark)').matches) {
          theme = 'dark';
        }
      }
      document.documentElement.dataset.theme = theme;
      // v1: always opaque unless a fixture explicitly asserts transparency (none in prod).
      const opaque = snapshot?.shell.transparencyEnabled !== true;
      document.documentElement.dataset.opaque = opaque ? 'true' : 'false';
      document.documentElement.style.colorScheme = theme;
    };

    apply();

    if (typeof window.matchMedia !== 'function') {
      return undefined;
    }

    const mq = window.matchMedia('(prefers-color-scheme: light)');
    const onChange = () => apply();
    mq.addEventListener('change', onChange);
    return () => mq.removeEventListener('change', onChange);
  }, [snapshot?.shell]);

  const devices = snapshot?.devices ?? [];
  const pairingInfo = presentation;
  const qrActive = snapshot?.pairingQr != null;
  // Decision 0010: the QR entry is visible whenever no pairing operation is active.
  const showQrEntry = !pairingInfo && !qrActive;
  // During success dwell, defer newly arrived devices until coalesced post-dwell transition.
  const visibleDevices =
    pairingInfo?.dwellingSuccess === true
      ? devices.filter((d) => pairingInfo.layoutDeviceIds.includes(d.id))
      : devices;
  const isEmpty = visibleDevices.length === 0 && !pairingInfo && !qrActive;
  const nowIso = snapshot?.generatedAt ?? '';

  const activeFixture =
    typeof window !== 'undefined'
      ? new URLSearchParams(window.location.search).get('fixture') || 'connected-wifi'
      : 'connected-wifi';

  return (
    <main
      ref={mainRef}
      data-testid="flyout-shell"
      className={`h-screen w-full flex flex-col p-4 box-border ${
        isPinned ? 'app-region-drag' : 'app-region-no-drag'
      }`}
    >
      <Header isPinned={isPinned} headerRef={headerRef} />

      <div
        ref={scrollRef}
        data-testid="flyout-scroll"
        className="flex-1 overflow-y-auto pb-2 app-region-no-drag flyout-scroll"
        style={{ WebkitAppRegion: 'no-drag' } as React.CSSProperties}
      >
        <div ref={contentWrapperRef} className="space-y-3">
          {isEmpty && <EmptyState />}

          {/* Decision 0010: idle entry — quiet, always visible when no pairing is active
              (inside the empty state as well). */}
          {showQrEntry && (
            <button
              type="button"
              data-testid="pairing-qr-entry"
              onClick={pairQrStart}
              className="w-full rounded-lg bg-win-card border border-win-card-border p-2.5 flex items-center justify-center gap-1.5 text-[12.5px] font-medium text-win-text-secondary hover:text-win-text-primary fluent-btn app-region-no-drag"
            >
              <svg
                fill="none"
                height="13"
                stroke="currentColor"
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth="2"
                viewBox="0 0 24 24"
                width="13"
              >
                <path d="M4.9 19.1C1 15.2 1 8.8 4.9 4.9"></path>
                <path d="M7.8 16.2c-2.3-2.3-2.3-6.1 0-8.5"></path>
                <circle cx="12" cy="12" r="2"></circle>
                <path d="M16.2 7.8c2.3 2.3 2.3 6.1 0 8.5"></path>
                <path d="M19.1 4.9C23 8.8 23 15.1 19.1 19"></path>
              </svg>
              Pair a device over Wi-Fi
            </button>
          )}

          {/* Decision 0010: active QR session. */}
          {snapshot?.pairingQr && <PairingQrCard qr={snapshot.pairingQr} onCancel={pairQrCancel} />}

          {pairingInfo && (
            <PairingCard
              state={pairingInfo.cardState}
              deviceName={pairingInfo.deviceName}
              offerId={pairingInfo.offerId}
              detail={pairingInfo.detail}
              onPairStart={pairStart}
              onPairCancel={pairCancel}
              onPairSubmit={pairSubmit}
            />
          )}

          {visibleDevices.map((d) => (
            <DeviceCard
              key={d.id}
              device={mapDeviceViewToDeviceInfo(d, expandedIds.has(d.id), nowIso)}
              copiedId={copiedId}
              onCopy={handleCopy}
              onToggleExpand={handleToggleExpand}
            />
          ))}

          {/* Decision 0008 (+ amendment): after pairing/devices, before any future warning. */}
          {snapshot?.discoveryHealth && (
            <DiscoveryHealthCard
              title={snapshot.discoveryHealth.title}
              detail={snapshot.discoveryHealth.detail}
              evidence={snapshot.discoveryHealth.evidence}
              actionLabel={snapshot.discoveryHealth.actionLabel}
              canCheckAgain={snapshot.discoveryHealth.canCheckAgain}
              repairs={snapshot.discoveryHealth.repairs}
              resultLine={snapshot.discoveryHealth.resultLine}
              onCheckAgain={discoveryRecheck}
              onRepair={discoveryRepair}
            />
          )}
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
