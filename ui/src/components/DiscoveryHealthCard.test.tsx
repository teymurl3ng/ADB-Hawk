import { render, screen, fireEvent } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { DiscoveryHealthCard } from './DiscoveryHealthCard';
import type { DiscoveryRepairCardAction } from './DiscoveryHealthCard';

const availableRepairs: DiscoveryRepairCardAction[] = [
  {
    id: 'restart-adb',
    label: 'Restart ADB',
    isAdmin: false,
    isRunning: false,
    runningLabel: 'Restarting ADB…',
  },
  {
    id: 'stop-service:Tailscale',
    label: 'Stop Tailscale service',
    isAdmin: true,
    isRunning: false,
    runningLabel: 'Stopping Tailscale…',
  },
];

function renderCard(overrides: Partial<Parameters<typeof DiscoveryHealthCard>[0]> = {}) {
  const onCheckAgain = vi.fn();
  const onRepair = vi.fn();
  const utils = render(
    <DiscoveryHealthCard
      title="Wireless discovery isn't working"
      detail="Galaxy S24 is connected over Wi-Fi, but ADB isn't receiving device announcements on this network."
      evidence={['Microsoft Edge is using the discovery port.']}
      actionLabel="Check again"
      canCheckAgain
      repairs={availableRepairs}
      resultLine={null}
      onCheckAgain={onCheckAgain}
      onRepair={onRepair}
      {...overrides}
    />,
  );
  return { onCheckAgain, onRepair, ...utils };
}

describe('DiscoveryHealthCard', () => {
  it('renders title, detail, evidence, and one button per backend repair', () => {
    renderCard();

    expect(screen.getByTestId('discovery-health-title').textContent).toBe(
      "Wireless discovery isn't working",
    );
    expect(screen.getByTestId('discovery-health-evidence').children).toHaveLength(1);
    const buttons = screen.getAllByTestId('discovery-repair-button');
    expect(buttons).toHaveLength(2);
    expect(buttons[0]!.textContent).toBe('Restart ADB');
    expect(buttons[1]!.textContent).toBe('Stop Tailscale service');
    expect(buttons[1]!.getAttribute('data-repair-admin')).toBe('true');
  });

  it('forwards the opaque repair id on click', () => {
    const { onRepair } = renderCard();

    fireEvent.click(screen.getAllByTestId('discovery-repair-button')[1]!);
    expect(onRepair).toHaveBeenCalledWith('stop-service:Tailscale');
  });

  it('shows the running label and disables the running repair', () => {
    renderCard({
      repairs: [{ ...availableRepairs[0]!, isRunning: true }, availableRepairs[1]!],
    });

    const running = screen.getAllByTestId('discovery-repair-button')[0]!;
    expect(running.textContent).toBe('Restarting ADB…');
    expect(running).toBeDisabled();

    // Other repairs stay clickable while one runs.
    const idle = screen.getAllByTestId('discovery-repair-button')[1]!;
    expect(idle).toBeEnabled();
  });

  it('shows the momentary result line when provided', () => {
    renderCard({ resultLine: 'Still not working after this action.' });

    expect(screen.getByTestId('discovery-result-line').textContent).toBe(
      'Still not working after this action.',
    );
  });

  it('omits the repair row and result line when empty', () => {
    renderCard({ repairs: [], resultLine: null });

    expect(screen.queryByTestId('discovery-repair-row')).toBeNull();
    expect(screen.queryByTestId('discovery-result-line')).toBeNull();
  });

  it('disables Check again while a repair is running', () => {
    const { onCheckAgain } = renderCard({
      repairs: [
        availableRepairs[0]!.isRunning
          ? availableRepairs[0]!
          : { ...availableRepairs[0]!, isRunning: true },
      ],
    });

    const check = screen.getByTestId('discovery-check-again');
    expect(check).toBeDisabled();
    fireEvent.click(check);
    expect(onCheckAgain).not.toHaveBeenCalled();
  });
});
