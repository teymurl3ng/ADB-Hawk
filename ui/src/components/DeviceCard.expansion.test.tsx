import { describe, expect, it } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { DeviceCard } from './DeviceCard';
import type { DeviceInfo } from '../types';

const base: DeviceInfo = {
  id: 'd1',
  name: 'Phone',
  state: 'connected',
  transport: 'wifi',
  stateAgeText: 'Connected just now',
  isExpanded: false,
  model: 'SM-A065F',
  androidVersion: '16',
  address: '192.168.18.248:5555',
};

describe('DeviceCard structural expansion', () => {
  it('keeps details grid mounted and toggles is-expanded without hard-coded height', () => {
    const { rerender } = render(<DeviceCard device={base} onToggleExpand={() => undefined} />);
    const grid = screen.getByTestId('device-card-details-grid');
    expect(grid.className).not.toContain('is-expanded');
    expect(getComputedStyle(grid).gridTemplateRows).not.toMatch(/999/);

    rerender(
      <DeviceCard device={{ ...base, isExpanded: true }} onToggleExpand={() => undefined} />,
    );
    expect(screen.getByTestId('device-card-details-grid').className).toContain('is-expanded');
    expect(screen.getByTestId('device-card-details')).toBeTruthy();
  });

  it('omits Serial row when only Address is present', () => {
    render(
      <DeviceCard
        device={{ ...base, isExpanded: true, serial: undefined }}
        onToggleExpand={() => undefined}
      />,
    );
    expect(screen.getByText('Address')).toBeTruthy();
    expect(screen.queryByText('Serial')).toBeNull();
  });

  it('chevron exposes aria-expanded', () => {
    render(<DeviceCard device={base} onToggleExpand={() => undefined} />);
    expect(screen.getByTestId('chevron-collapsed').getAttribute('aria-expanded')).toBe('false');
  });

  it('fires toggle from collapsed chevron', () => {
    const toggles: string[] = [];
    render(
      <DeviceCard
        device={base}
        onToggleExpand={(id) => {
          toggles.push(id);
        }}
      />,
    );
    fireEvent.click(screen.getByTestId('chevron-collapsed'));
    expect(toggles).toEqual(['d1']);
  });
});
