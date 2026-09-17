import { render, screen, fireEvent } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { App } from './App';

describe('App', () => {
  it('renders the ADB View header', () => {
    render(<App />);
    expect(screen.getByText(/ADB View/i)).toBeInTheDocument();
  });

  it('toggles expansion when clicking a device card chevron', () => {
    render(<App />);
    // By default connected-wifi fixture renders Galaxy S24 collapsed
    expect(screen.queryByTestId('device-card-details')).not.toBeInTheDocument();

    const chevronCollapsed = screen.getByTestId('chevron-collapsed');
    fireEvent.click(chevronCollapsed);

    // Now expanded details should appear
    expect(screen.getByTestId('device-card-details')).toBeInTheDocument();
    expect(screen.getByText('Model')).toBeInTheDocument();
    expect(screen.getByText('SM-S921W')).toBeInTheDocument();
    expect(screen.getByText('Address')).toBeInTheDocument();

    // Clicking expanded chevron collapses it
    const chevronExpanded = screen.getByTestId('chevron-expanded');
    fireEvent.click(chevronExpanded);
    expect(screen.queryByTestId('device-card-details')).not.toBeInTheDocument();
  });

  it('supports fixture switching via Ctrl+Shift+F shortcut', () => {
    render(<App />);
    expect(screen.queryByText('Fixture Browser')).not.toBeInTheDocument();

    // Press Ctrl+Shift+F
    fireEvent.keyDown(window, { key: 'F', code: 'KeyF', ctrlKey: true, shiftKey: true });
    expect(screen.getByText('Fixture Browser')).toBeInTheDocument();

    // Press Escape
    fireEvent.keyDown(window, { key: 'Escape', code: 'Escape' });
    expect(screen.queryByText('Fixture Browser')).not.toBeInTheDocument();
  });
});
