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
      <div className="rounded-lg bg-win-green-surface border border-win-green-border p-3.5 flex items-center gap-3">
        <div className="w-8 h-8 rounded-full bg-win-green-surface border border-win-green-border flex items-center justify-center text-win-green-text flex-shrink-0">
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
          <h3 className="text-[14px] font-semibold text-win-text-primary tracking-tight">
            Paired with {deviceName}
          </h3>
          <p className="text-[12px] text-win-text-secondary mt-0.5">Waiting for ADB connection…</p>
        </div>
      </div>
    );
  }

  if (state === 'pairing-progress') {
    return (
      <div className="rounded-lg bg-win-accent-surface border border-win-accent-border p-4 flex flex-col items-center text-center">
        <div className="w-8 h-8 rounded-full border-2 border-win-accent/20 border-t-win-accent animate-spin-quiet mb-3"></div>
        <h3 className="text-[14px] font-semibold text-win-text-primary tracking-tight mb-1">
          Pairing with {deviceName}…
        </h3>
        <p className="text-[12px] text-win-text-secondary max-w-[260px]">
          Keep the phone nearby while pairing completes.
        </p>
        <div className="hit-area-40 mt-3 pt-2.5 border-t border-win-border-subtle w-full flex justify-center">
          <button className="text-[11.5px] text-win-text-secondary hover:text-win-text-primary fluent-btn">
            Cancel pairing
          </button>
        </div>
      </div>
    );
  }

  if (state === 'pairing-offer') {
    return (
      <div className="rounded-lg bg-win-accent-surface border border-win-accent-border p-3.5">
        <div className="flex items-start justify-between gap-3">
          <div className="flex items-start gap-2.5">
            <div className="w-8 h-8 rounded bg-win-accent/10 border border-win-accent/20 flex items-center justify-center text-win-accent flex-shrink-0 mt-0.5">
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
                <span className="static-dot bg-win-accent"></span>
                <h3 className="text-[13.5px] font-semibold text-win-text-primary tracking-tight">
                  Wireless pairing available
                </h3>
              </div>
              <p className="text-[14px] font-medium text-win-text-primary mt-1">{deviceName}</p>
              <p className="text-[12px] text-win-text-secondary mt-0.5">
                Waiting for a pairing code
              </p>
            </div>
          </div>
          <div className="hit-area-40">
            <button className="px-3 py-1.5 rounded bg-win-accent hover:bg-win-accent-hover active:bg-blue-700 text-white text-[12.5px] font-medium shadow-sm flex items-center gap-1.5 fluent-btn">
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
        className="h-9 rounded bg-win-slot-bg border border-win-accent ring-2 ring-win-accent/25 flex items-center justify-center font-mono text-[14px] text-win-text-primary"
      >
        <span className="inline-block w-1.5 h-4 bg-win-accent animate-pulse"></span>
      </div>,
      ...Array.from({ length: 5 }).map((_, i) => (
        <div
          key={i + 1}
          className="h-9 rounded bg-win-slot-bg border border-win-slot-border flex items-center justify-center font-mono text-[14px] text-win-text-muted"
        >
          _
        </div>
      )),
    ];
  } else if (state === 'pairing-incomplete') {
    slots = [
      <div
        key="0"
        className="h-9 rounded bg-win-slot-bg border border-win-slot-border flex items-center justify-center font-mono text-[15px] font-semibold text-win-text-primary"
      >
        2
      </div>,
      <div
        key="1"
        className="h-9 rounded bg-win-slot-bg border border-win-slot-border flex items-center justify-center font-mono text-[15px] font-semibold text-win-text-primary"
      >
        4
      </div>,
      <div
        key="2"
        className="h-9 rounded bg-win-slot-bg border border-win-accent ring-2 ring-win-accent/25 flex items-center justify-center font-mono text-[14px] text-win-text-primary"
      >
        <span className="inline-block w-1.5 h-4 bg-win-accent animate-pulse"></span>
      </div>,
      ...Array.from({ length: 3 }).map((_, i) => (
        <div
          key={i + 3}
          className="h-9 rounded bg-win-slot-bg border border-win-slot-border flex items-center justify-center font-mono text-[14px] text-win-text-muted"
        >
          _
        </div>
      )),
    ];
  } else if (isReady) {
    slots = ['2', '4', '8', '1', '7', '3'].map((char, i) => (
      <div
        key={i}
        className="h-9 rounded bg-win-slot-bg border border-win-slot-border flex items-center justify-center font-mono text-[15px] font-semibold text-win-text-primary"
      >
        {char}
      </div>
    ));
  } else if (isFailure) {
    slots = ['2', '4', '8', '1', '7', '9'].map((char, i) => (
      <div
        key={i}
        className="h-9 rounded bg-win-slot-bg border border-win-red-border flex items-center justify-center font-mono text-[14.5px] font-medium text-win-red-text"
      >
        {char}
      </div>
    ));
  }

  return (
    <div
      className={`rounded-lg ${isFailure ? 'bg-win-red-surface border-win-red-border' : 'bg-win-accent-surface border-win-accent-border'} border p-3.5`}
    >
      {isFailure ? (
        <div className="flex items-start gap-2.5 mb-2.5">
          <div className="w-7 h-7 rounded bg-win-red-surface border border-win-red-border flex items-center justify-center text-win-red-text flex-shrink-0 mt-0.5">
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
            <h3 className="text-[14px] font-semibold text-win-text-primary tracking-tight">
              Pairing failed
            </h3>
            <p className="text-[12px] text-win-text-secondary mt-0.5">
              Check the code and try again.
            </p>
          </div>
        </div>
      ) : (
        <>
          <div className="flex items-center justify-between mb-1.5">
            <h3 className="text-[14.5px] font-semibold text-win-text-primary tracking-tight">
              {deviceName}
            </h3>
            <span className="text-[11px] text-win-accent font-medium">Wi-Fi Pairing</span>
          </div>
          <p className="text-[12px] text-win-text-secondary mb-3 leading-relaxed">
            Enter the six-digit code shown on the phone.
          </p>
        </>
      )}

      <div className="grid grid-cols-6 gap-2 mb-3.5">{slots}</div>

      <div className="flex items-center justify-end gap-2 pt-2 border-t border-win-border-subtle">
        <div className="hit-area-40">
          <button className="px-2.5 py-1 rounded text-[12px] font-medium text-win-text-secondary hover:text-win-text-primary hover:bg-win-btn-hover fluent-btn">
            Cancel
          </button>
        </div>
        <div className="hit-area-40">
          {isFailure ? (
            <button className="px-3.5 py-1 rounded text-[12px] font-medium bg-win-red hover:bg-red-600 text-white shadow-sm flex items-center gap-1.5 fluent-btn">
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
            <button className="px-3.5 py-1 rounded text-[12px] font-medium bg-win-accent hover:bg-win-accent-hover active:bg-blue-700 text-white shadow-sm flex items-center gap-1.5 fluent-btn">
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
              className="px-3 py-1 rounded text-[12px] font-medium bg-win-btn-hover text-win-text-muted border border-win-border-subtle cursor-not-allowed"
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
