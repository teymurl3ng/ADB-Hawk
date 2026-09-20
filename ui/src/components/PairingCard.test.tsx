import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';
import { render, fireEvent, screen, cleanup } from '@testing-library/react';
import { PairingCard } from '../components/PairingCard';
import * as actions from '../bridge/actions';

describe('PairingCard Pair button path', () => {
  beforeEach(() => {
    vi.spyOn(actions, 'pairStart').mockImplementation(() => {});
    vi.spyOn(actions, 'pairCancel').mockImplementation(() => {});
    vi.spyOn(actions, 'pairSubmit').mockImplementation(() => {});
  });

  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
  });

  it('emits pairStart exactly once with offer id on Pair click', () => {
    const onPairStart = vi.fn();
    render(
      <PairingCard
        state="pairing-offer"
        deviceName="Android device"
        offerId="offer-live-1"
        onPairStart={onPairStart}
      />,
    );

    fireEvent.click(screen.getByTestId('pairing-pair-button'));
    expect(onPairStart).toHaveBeenCalledTimes(1);
    expect(onPairStart).toHaveBeenCalledWith('offer-live-1');
    expect(screen.getByTestId('pairing-device-name').textContent).toBe('Android device');
    expect(screen.getByTestId('pairing-device-name').textContent).not.toMatch(/^adb-/);
  });

  it('shows six-digit code field for awaitingCode without modal', () => {
    render(
      <PairingCard
        state="pairing-code"
        deviceName="Galaxy S24"
        offerId="offer-live-1"
        onPairCancel={vi.fn()}
        onPairSubmit={vi.fn()}
      />,
    );

    expect(screen.getByTestId('pairing-code-input')).toBeTruthy();
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(screen.getByTestId('pairing-device-name').textContent).toBe('Galaxy S24');
  });

  it('submits six-digit code via pairSubmit', () => {
    const onPairSubmit = vi.fn();
    render(
      <PairingCard
        state="pairing-code"
        deviceName="Galaxy S24"
        offerId="offer-live-1"
        onPairSubmit={onPairSubmit}
        onPairCancel={vi.fn()}
      />,
    );

    fireEvent.change(screen.getByTestId('pairing-code-input'), {
      target: { value: '248173' },
    });
    fireEvent.click(screen.getByTestId('pairing-pair-button'));
    expect(onPairSubmit).toHaveBeenCalledWith('offer-live-1', '248173');
  });

  it('keeps Pair button and card height stable from 5 to 6 digits', () => {
    render(
      <PairingCard
        state="pairing-code"
        deviceName="Galaxy S24"
        offerId="offer-live-1"
        onPairSubmit={vi.fn()}
        onPairCancel={vi.fn()}
      />,
    );

    const input = screen.getByTestId('pairing-code-input');
    fireEvent.change(input, { target: { value: '12345' } });
    const cardAt5 = screen.getByTestId('pairing-card').getBoundingClientRect().height;
    const btnAt5 = screen.getByTestId('pairing-pair-button');
    const rect5 = btnAt5.getBoundingClientRect();
    const classDisabled = btnAt5.className;

    fireEvent.change(input, { target: { value: '123456' } });
    const cardAt6 = screen.getByTestId('pairing-card').getBoundingClientRect().height;
    const btnAt6 = screen.getByTestId('pairing-pair-button');
    const rect6 = btnAt6.getBoundingClientRect();
    const classEnabled = btnAt6.className;

    expect(cardAt6).toBe(cardAt5);
    expect(rect6.height).toBe(rect5.height);
    expect(rect6.width).toBe(rect5.width);
    // Zero geometry delta: fixed box-model tokens on both enabled and disabled (M6 sentinel).
    for (const cls of [classDisabled, classEnabled]) {
      expect(cls, 'Pair button must lock min-width/height/box/padding').toMatch(/min-w-\[3rem]/);
      expect(cls).toMatch(/\bh-7\b/);
      expect(cls).toMatch(/\bbox-border\b/);
      expect(cls).toMatch(/\bpx-3\.5\b/);
      expect(cls).toMatch(/\bpy-0\b/);
    }
  });

  it('drives data-pair-feedback through pointer enter/down/up/leave', () => {
    render(
      <PairingCard
        state="pairing-offer"
        deviceName="Galaxy S24"
        offerId="offer-live-1"
        onPairStart={vi.fn()}
      />,
    );
    const btn = screen.getByTestId('pairing-pair-button');
    expect(btn.getAttribute('data-pair-feedback')).toBe('rest');

    fireEvent.pointerEnter(btn);
    expect(btn.getAttribute('data-pair-feedback')).toBe('hover');

    fireEvent.pointerDown(btn, { button: 0, clientX: 10, clientY: 10 });
    expect(btn.getAttribute('data-pair-feedback')).toBe('pressed');

    const rect = btn.getBoundingClientRect();
    fireEvent.pointerUp(btn, {
      button: 0,
      clientX: rect.left + 1,
      clientY: rect.top + 1,
    });
    expect(btn.getAttribute('data-pair-feedback')).toBe('hover');

    fireEvent.pointerLeave(btn);
    expect(btn.getAttribute('data-pair-feedback')).toBe('rest');
  });

  it('keeps one stable outer shell across phase morphs', () => {
    const { rerender } = render(
      <PairingCard state="pairing-offer" deviceName="Phone" offerId="o1" onPairStart={vi.fn()} />,
    );
    const shell = screen.getByTestId('pairing-card');
    expect(shell.getAttribute('data-pairing-shell')).toBe('stable');
    const shellNode = shell;

    for (const state of [
      'pairing-code',
      'pairing-progress',
      'pairing-success',
      'pairing-failure',
    ] as const) {
      rerender(
        <PairingCard
          state={state}
          deviceName="Phone"
          offerId="o1"
          onPairStart={vi.fn()}
          onPairCancel={vi.fn()}
          onPairSubmit={vi.fn()}
        />,
      );
      const next = screen.getByTestId('pairing-card');
      expect(next).toBe(shellNode);
      expect(next.getAttribute('data-pairing-shell')).toBe('stable');
      expect(next.getAttribute('data-pairing-phase')).toBe(state);
    }
  });

  it('50 digit cycles produce zero outer-height change', () => {
    render(
      <PairingCard
        state="pairing-code"
        deviceName="Phone"
        offerId="o1"
        onPairSubmit={vi.fn()}
        onPairCancel={vi.fn()}
      />,
    );
    const input = screen.getByTestId('pairing-code-input');
    const card = screen.getByTestId('pairing-card');
    const h0 = card.getBoundingClientRect().height;
    for (let i = 0; i < 50; i++) {
      fireEvent.change(input, { target: { value: String(i % 10).repeat(i % 7) } });
      fireEvent.change(input, { target: { value: '123456' } });
      fireEvent.change(input, { target: { value: '' } });
      expect(card.getBoundingClientRect().height).toBe(h0);
    }
  });
});
