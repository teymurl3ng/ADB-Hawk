import { useEffect, useRef, useState, type PointerEvent, type ReactNode } from 'react';
import type { FixtureName } from '../types';

export type PairingCardState = Extract<
  FixtureName,
  | 'pairing-offer'
  | 'pairing-code'
  | 'pairing-incomplete'
  | 'pairing-ready'
  | 'pairing-progress'
  | 'pairing-success'
  | 'pairing-failure'
>;

interface PairingCardProps {
  state: PairingCardState;
  deviceName: string;
  offerId?: string;
  onPairStart?: (offerId: string) => void;
  onPairCancel?: (offerId: string) => void;
  onPairSubmit?: (offerId: string, code: string) => void;
}

/** Fixed outer geometry for Pair — enabled/disabled/focus/hover/pressed must not change box. */
const PAIR_BTN_BOX =
  'hit-area-40 inline-flex items-center justify-center min-w-[3rem] h-7 box-border px-3.5 py-0 rounded text-[12px] font-medium border border-solid leading-none fluent-btn app-region-no-drag focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-win-accent';

type PairFeedback = 'rest' | 'hover' | 'pressed';

function PairActionButton({
  className,
  disabled,
  children,
  onClick,
}: {
  className: string;
  disabled?: boolean;
  children: ReactNode;
  onClick?: () => void;
}) {
  const [feedback, setFeedback] = useState<PairFeedback>('rest');
  const pressedRef = useRef(false);

  const setRest = () => {
    pressedRef.current = false;
    setFeedback('rest');
  };

  const onPointerEnter = () => {
    if (disabled) return;
    setFeedback(pressedRef.current ? 'pressed' : 'hover');
  };

  const onPointerLeave = () => {
    pressedRef.current = false;
    setFeedback('rest');
  };

  const onPointerDown = (e: PointerEvent<HTMLButtonElement>) => {
    if (disabled) return;
    if (e.button !== 0) return;
    pressedRef.current = true;
    setFeedback('pressed');
    try {
      e.currentTarget.setPointerCapture(e.pointerId);
    } catch {
      /* jsdom */
    }
  };

  const onPointerUp = (e: PointerEvent<HTMLButtonElement>) => {
    if (disabled) {
      setRest();
      return;
    }
    pressedRef.current = false;
    const rect = e.currentTarget.getBoundingClientRect();
    const hasBox = rect.width > 0 && rect.height > 0;
    const inside = !hasBox
      ? true
      : e.clientX >= rect.left &&
        e.clientX <= rect.right &&
        e.clientY >= rect.top &&
        e.clientY <= rect.bottom;
    setFeedback(inside ? 'hover' : 'rest');
    try {
      if (
        typeof e.currentTarget.hasPointerCapture === 'function' &&
        e.currentTarget.hasPointerCapture(e.pointerId)
      ) {
        e.currentTarget.releasePointerCapture(e.pointerId);
      }
    } catch {
      /* jsdom */
    }
  };

  return (
    <button
      type="button"
      data-testid="pairing-pair-button"
      data-pair-feedback={disabled ? 'rest' : feedback}
      disabled={disabled}
      className={className}
      onClick={onClick}
      onPointerEnter={onPointerEnter}
      onPointerLeave={onPointerLeave}
      onPointerDown={onPointerDown}
      onPointerUp={onPointerUp}
      onPointerCancel={setRest}
      onBlur={setRest}
    >
      {children}
    </button>
  );
}

function shellSurface(state: PairingCardState): string {
  if (state === 'pairing-success') {
    return 'bg-win-green-surface border-win-green-border';
  }
  if (state === 'pairing-failure') {
    return 'bg-win-red-surface border-win-red-border';
  }
  return 'bg-win-accent-surface border-win-accent-border';
}

/**
 * ONE stable PairingCard shell across all phases.
 * Phase changes morph content/action regions — they do not replace the outer host.
 */
export function PairingCard({
  state,
  deviceName,
  offerId,
  onPairStart,
  onPairCancel,
  onPairSubmit,
}: PairingCardProps) {
  const [code, setCode] = useState('');
  const codeInputRef = useRef<HTMLInputElement>(null);
  const shellRef = useRef<HTMLDivElement>(null);

  const isCodeEntry =
    state === 'pairing-code' ||
    state === 'pairing-incomplete' ||
    state === 'pairing-ready' ||
    state === 'pairing-failure';
  const isInteractiveCode =
    state === 'pairing-code' || state === 'pairing-failure' || !!onPairSubmit || !!onPairCancel;
  const isReady = state === 'pairing-ready' || (isInteractiveCode && code.length === 6);
  const isProgress = state === 'pairing-progress';
  const isOffer = state === 'pairing-offer';
  const isSuccess = state === 'pairing-success';
  const isFailure = state === 'pairing-failure';

  useEffect(() => {
    if (state === 'pairing-code' || state === 'pairing-failure') {
      setCode('');
      queueMicrotask(() => codeInputRef.current?.focus());
    }
  }, [state, offerId]);

  const digits = isInteractiveCode
    ? code.padEnd(6, '_').slice(0, 6).split('')
    : state === 'pairing-incomplete'
      ? ['2', '4', '_', '_', '_', '_']
      : state === 'pairing-ready'
        ? ['2', '4', '8', '1', '7', '3']
        : ['_', '_', '_', '_', '_', '_'];

  const slots: ReactNode[] = digits.map((char, i) => {
    const isCursor =
      isInteractiveCode && i === code.length && code.length < 6 && state !== 'pairing-ready';
    const filled = char !== '_';
    return (
      <div
        key={i}
        className={`h-9 rounded bg-win-slot-bg border border-win-slot-border box-border flex items-center justify-center font-mono text-[15px] leading-none ${
          isFailure
            ? 'border-win-red-border font-medium text-win-red-text'
            : isCursor
              ? 'text-win-text-primary shadow-[0_0_0_2px_rgba(0,120,212,0.25)]'
              : filled
                ? 'font-semibold text-win-text-primary'
                : 'text-win-text-muted'
        }`}
      >
        {isCursor ? (
          <span className="inline-block w-1.5 h-4 bg-win-accent animate-pulse"></span>
        ) : filled ? (
          char
        ) : (
          '_'
        )}
      </div>
    );
  });

  return (
    <div
      ref={shellRef}
      data-testid="pairing-card"
      data-pairing-phase={state}
      data-pairing-shell="stable"
      className={`rounded-lg border p-3.5 app-region-no-drag ${shellSurface(state)}`}
    >
      {/* Header / identity — always present */}
      <div data-pairing-region="header" className="min-h-[1.25rem]">
        {isOffer && (
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
                <p
                  data-testid="pairing-device-name"
                  className="text-[14px] font-medium text-win-text-primary mt-1"
                >
                  {deviceName}
                </p>
                <p className="text-[12px] text-win-text-secondary mt-0.5">
                  Waiting for a pairing code
                </p>
              </div>
            </div>
            <PairActionButton
              className={`${PAIR_BTN_BOX} shadow-sm fluent-btn-pair border-transparent`}
              onClick={() => {
                if (offerId) onPairStart?.(offerId);
              }}
            >
              <span>Pair</span>
              <svg
                className="ml-1.5"
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
            </PairActionButton>
          </div>
        )}

        {isSuccess && (
          <div className="flex items-center gap-3">
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
              <h3
                data-testid="pairing-device-name"
                className="text-[14px] font-semibold text-win-text-primary tracking-tight"
              >
                Paired with {deviceName}
              </h3>
              <p className="text-[12px] text-win-text-secondary mt-0.5">
                Waiting for ADB connection…
              </p>
            </div>
          </div>
        )}

        {isProgress && (
          <div className="flex flex-col items-center text-center">
            <div className="w-8 h-8 rounded-full border-2 border-win-accent/20 border-t-win-accent animate-spin-quiet mb-3"></div>
            <h3
              data-testid="pairing-device-name"
              className="text-[14px] font-semibold text-win-text-primary tracking-tight mb-1"
            >
              Pairing with {deviceName}…
            </h3>
            <p className="text-[12px] text-win-text-secondary max-w-[260px]">
              Keep the phone nearby while pairing completes.
            </p>
          </div>
        )}

        {isFailure && (
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
        )}

        {isCodeEntry && !isFailure && (
          <>
            <div className="flex items-center justify-between mb-1.5">
              <h3
                data-testid="pairing-device-name"
                className="text-[14.5px] font-semibold text-win-text-primary tracking-tight"
              >
                {deviceName}
              </h3>
              <span className="text-[11px] text-win-accent font-medium">Wi-Fi Pairing</span>
            </div>
            <p className="text-[12px] text-win-text-secondary mb-3 leading-relaxed">
              Enter the six-digit code shown on the phone.
            </p>
          </>
        )}
      </div>

      {/* Content region */}
      <div data-pairing-region="content">
        {isCodeEntry && (
          <div className="relative grid grid-cols-6 gap-2 mb-3.5">
            {slots}
            {isInteractiveCode && (
              <input
                ref={codeInputRef}
                data-testid="pairing-code-input"
                className="absolute inset-0 opacity-0 cursor-text"
                inputMode="numeric"
                autoComplete="one-time-code"
                maxLength={6}
                value={code}
                aria-label="Pairing code"
                disabled={isProgress}
                onChange={(e) => {
                  const next = e.target.value.replace(/\D/g, '').slice(0, 6);
                  setCode(next);
                }}
              />
            )}
          </div>
        )}
      </div>

      {/* Action region */}
      <div data-pairing-region="actions">
        {isProgress && (
          <button
            type="button"
            data-testid="pairing-cancel-button"
            className="hit-area-40 mt-3 pt-2.5 border-t border-win-border-subtle text-[11.5px] text-win-text-secondary hover:text-win-text-primary fluent-btn app-region-no-drag w-full"
            onClick={() => offerId && onPairCancel?.(offerId)}
          >
            Cancel pairing
          </button>
        )}

        {isCodeEntry && (
          <div className="flex items-center justify-end gap-2 pt-2 border-t border-win-border-subtle">
            <button
              type="button"
              data-testid="pairing-cancel-button"
              className="hit-area-40 px-2.5 h-7 box-border rounded text-[12px] font-medium text-win-text-secondary hover:text-win-text-primary hover:bg-win-btn-hover fluent-btn app-region-no-drag"
              onClick={() => offerId && onPairCancel?.(offerId)}
            >
              Cancel
            </button>
            {isFailure ? (
              <PairActionButton
                className={`${PAIR_BTN_BOX} bg-win-red hover:bg-red-600 text-white shadow-sm border-transparent`}
                onClick={() => {
                  if (offerId && code.length === 6) onPairSubmit?.(offerId, code);
                  else if (offerId) onPairStart?.(offerId);
                }}
              >
                <span>Retry</span>
              </PairActionButton>
            ) : (
              <PairActionButton
                disabled={!isReady}
                className={
                  isReady
                    ? `${PAIR_BTN_BOX} shadow-sm fluent-btn-pair border-transparent`
                    : `${PAIR_BTN_BOX} bg-win-btn-hover text-win-text-muted border-win-border-subtle`
                }
                onClick={() => {
                  if (offerId && code.length === 6) onPairSubmit?.(offerId, code);
                }}
              >
                <span>Pair</span>
              </PairActionButton>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
