import type { ReactNode } from 'react';
import type { FixtureName } from '../types';

interface PairingCardProps {
  state: Extract<
    FixtureName,
    | 'pairing-offer'
    | 'pairing-code'
    | 'pairing-incomplete'
    | 'pairing-ready'
    | 'pairing-progress'
    | 'pairing-success'
    | 'pairing-failure'
  >;
  deviceName: string;
}

export function PairingCard({ state, deviceName }: PairingCardProps) {
  if (state === 'pairing-success') {
    return (
      <div className="rounded-lg bg-win-greenSurface border border-emerald-500/25 p-3.5 flex items-center gap-3">
        <div className="w-8 h-8 rounded-full bg-emerald-500/15 border border-emerald-500/30 flex items-center justify-center text-emerald-400 flex-shrink-0">
          <svg
            fill="none"
            height="16"
            stroke="currentColor"
            strokeLinecap="round"
            strokeLinejoin="round"
            strokeWidth="2.5"
            viewBox="0 0 24 24"
            width="16"
          >
            <polyline points="20 6 9 17 4 12"></polyline>
          </svg>
        </div>
        <div>
          <h3 className="text-[14px] font-semibold text-white tracking-tight">
            Paired with {deviceName}
          </h3>
          <p className="text-[12px] text-emerald-300/80 mt-0.5">Waiting for ADB connection…</p>
        </div>
      </div>
    );
  }

  if (state === 'pairing-progress') {
    return (
      <div className="rounded-lg bg-win-accentSurface border border-win-accentBorder p-4 flex flex-col items-center text-center">
        <div className="w-8 h-8 rounded-full border-2 border-blue-500/20 border-t-blue-400 animate-spin-quiet mb-3"></div>
        <h3 className="text-[14px] font-semibold text-white tracking-tight mb-1">
          Pairing with {deviceName}…
        </h3>
        <p className="text-[12px] text-neutral-400 max-w-[260px]">
          Keep the phone nearby while pairing completes.
        </p>
        <div className="hit-area-40 mt-3 pt-2.5 border-t border-white/[0.06] w-full flex justify-center">
          <button className="text-[11.5px] text-neutral-400 hover:text-neutral-200 fluent-btn">
            Cancel pairing
          </button>
        </div>
      </div>
    );
  }

  if (state === 'pairing-offer') {
    return (
      <div className="rounded-lg bg-win-accentSurface border border-win-accentBorder p-3.5">
        <div className="flex items-start justify-between gap-3">
          <div className="flex items-start gap-2.5">
            <div className="w-8 h-8 rounded bg-blue-500/10 border border-blue-500/20 flex items-center justify-center text-blue-400 flex-shrink-0 mt-0.5">
              <svg
                fill="none"
                height="16"
                stroke="currentColor"
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth="2"
                viewBox="0 0 24 24"
                width="16"
              >
                <path d="M4.9 19.1C1 15.2 1 8.8 4.9 4.9"></path>
                <path d="M7.8 16.2c-2.3-2.3-2.3-6.1 0-8.5"></path>
                <circle cx="12" cy="12" r="2"></circle>
                <path d="M16.2 7.8c2.3 2.3 2.3 6.1 0 8.5"></path>
                <path d="M19.1 4.9C23 8.8 23 15.1 19.1 19"></path>
              </svg>
            </div>
            <div>
              <div className="flex items-center gap-1.5">
                <span className="static-dot bg-blue-400"></span>
                <h3 className="text-[13.5px] font-semibold text-white tracking-tight">
                  Wireless pairing available
                </h3>
              </div>
              <p className="text-[14px] font-medium text-neutral-200 mt-1">{deviceName}</p>
              <p className="text-[12px] text-neutral-400 mt-0.5">Waiting for a pairing code</p>
            </div>
          </div>
          <div className="hit-area-40">
            <button className="px-3 py-1.5 rounded bg-blue-500 hover:bg-blue-600 active:bg-blue-700 text-white text-[12.5px] font-medium shadow-sm flex items-center gap-1.5 fluent-btn">
              <span>Pair</span>
              <svg
                fill="none"
                height="12"
                stroke="currentColor"
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth="2.5"
                viewBox="0 0 24 24"
                width="12"
              >
                <path d="M5 12h14"></path>
                <path d="m12 5 7 7-7 7"></path>
              </svg>
            </button>
          </div>
        </div>
      </div>
    );
  }

  // Code entry states (empty, incomplete, ready, failure)
  const isFailure = state === 'pairing-failure';
  const isReady = state === 'pairing-ready';

  let slots: ReactNode[] = [];
  if (state === 'pairing-code') {
    slots = [
      <div
        key="0"
        className="h-9 rounded bg-black/40 border border-blue-400 ring-2 ring-blue-500/25 flex items-center justify-center font-mono text-[14px] text-white"
      >
        <span className="inline-block w-1.5 h-4 bg-blue-400 animate-pulse"></span>
      </div>,
      ...Array.from({ length: 5 }).map((_, i) => (
        <div
          key={i + 1}
          className="h-9 rounded bg-black/30 border border-white/10 flex items-center justify-center font-mono text-[14px] text-neutral-500"
        >
          _
        </div>
      )),
    ];
  } else if (state === 'pairing-incomplete') {
    slots = [
      <div
        key="0"
        className="h-9 rounded bg-black/40 border border-white/20 flex items-center justify-center font-mono text-[15px] font-semibold text-white"
      >
        2
      </div>,
      <div
        key="1"
        className="h-9 rounded bg-black/40 border border-white/20 flex items-center justify-center font-mono text-[15px] font-semibold text-white"
      >
        4
      </div>,
      <div
        key="2"
        className="h-9 rounded bg-black/40 border border-blue-400 ring-2 ring-blue-500/25 flex items-center justify-center font-mono text-[14px] text-white"
      >
        <span className="inline-block w-1.5 h-4 bg-blue-400 animate-pulse"></span>
      </div>,
      ...Array.from({ length: 3 }).map((_, i) => (
        <div
          key={i + 3}
          className="h-9 rounded bg-black/30 border border-white/10 flex items-center justify-center font-mono text-[14px] text-neutral-500"
        >
          _
        </div>
      )),
    ];
  } else if (isReady) {
    slots = ['2', '4', '8', '1', '7', '3'].map((char, i) => (
      <div
        key={i}
        className="h-9 rounded bg-black/40 border border-white/20 flex items-center justify-center font-mono text-[15px] font-semibold text-white"
      >
        {char}
      </div>
    ));
  } else if (isFailure) {
    slots = ['2', '4', '8', '1', '7', '9'].map((char, i) => (
      <div
        key={i}
        className="h-9 rounded bg-black/40 border border-red-400/40 flex items-center justify-center font-mono text-[14.5px] font-medium text-neutral-300"
      >
        {char}
      </div>
    ));
  }

  return (
    <div
      className={`rounded-lg ${isFailure ? 'bg-win-redSurface border-win-redBorder' : 'bg-win-accentSurface border-win-accentBorder'} border p-3.5`}
    >
      {isFailure ? (
        <div className="flex items-start gap-2.5 mb-2.5">
          <div className="w-7 h-7 rounded bg-red-500/15 border border-red-500/30 flex items-center justify-center text-red-400 flex-shrink-0 mt-0.5">
            <svg
              fill="none"
              height="14"
              stroke="currentColor"
              strokeLinecap="round"
              strokeLinejoin="round"
              strokeWidth="2.5"
              viewBox="0 0 24 24"
              width="14"
            >
              <circle cx="12" cy="12" r="10"></circle>
              <line x1="12" x2="12" y1="8" y2="12"></line>
              <line x1="12" x2="12.01" y1="16" y2="16"></line>
            </svg>
          </div>
          <div>
            <h3 className="text-[14px] font-semibold text-white tracking-tight">Pairing failed</h3>
            <p className="text-[12px] text-neutral-300 mt-0.5">Check the code and try again.</p>
          </div>
        </div>
      ) : (
        <>
          <div className="flex items-center justify-between mb-1.5">
            <h3 className="text-[14.5px] font-semibold text-white tracking-tight">{deviceName}</h3>
            <span className="text-[11px] text-blue-400 font-medium">Wi-Fi Pairing</span>
          </div>
          <p className="text-[12px] text-neutral-400 mb-3 leading-relaxed">
            Enter the six-digit code shown on the phone.
          </p>
        </>
      )}

      <div className="grid grid-cols-6 gap-2 mb-3.5">{slots}</div>

      <div className="flex items-center justify-end gap-2 pt-2 border-t border-white/[0.06]">
        <div className="hit-area-40">
          <button className="px-2.5 py-1 rounded text-[12px] font-medium text-neutral-400 hover:text-white hover:bg-white/[0.06] fluent-btn">
            Cancel
          </button>
        </div>
        <div className="hit-area-40">
          {isFailure ? (
            <button className="px-3.5 py-1 rounded text-[12px] font-medium bg-red-500/90 hover:bg-red-600 text-white shadow-sm flex items-center gap-1.5 fluent-btn">
              <span>Retry</span>
              <svg
                fill="none"
                height="12"
                stroke="currentColor"
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth="2"
                viewBox="0 0 24 24"
                width="12"
              >
                <path d="M21 12a9 9 0 0 0-9-9 9.75 9.75 0 0 0-6.74 2.74L3 8"></path>
                <path d="M3 3v5h5"></path>
                <path d="M3 12a9 9 0 0 0 9 9 9.75 9.75 0 0 0 6.74-2.74L21 16"></path>
                <path d="M16 21v-5h5"></path>
              </svg>
            </button>
          ) : isReady ? (
            <button className="px-3.5 py-1 rounded text-[12px] font-medium bg-blue-500 hover:bg-blue-600 active:bg-blue-700 text-white shadow-sm flex items-center gap-1.5 fluent-btn">
              <span>Pair</span>
              <svg
                fill="none"
                height="12"
                stroke="currentColor"
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth="2.5"
                viewBox="0 0 24 24"
                width="12"
              >
                <polyline points="20 6 9 17 4 12"></polyline>
              </svg>
            </button>
          ) : (
            <button
              className="px-3 py-1 rounded text-[12px] font-medium bg-white/[0.06] text-neutral-500 border border-white/[0.04] cursor-not-allowed"
              disabled
            >
              Pair
            </button>
          )}
        </div>
      </div>
    </div>
  );
}
