import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { App } from './App';

describe('App placeholder', () => {
  it('renders the phase 0 toolchain placeholder', () => {
    render(<App />);
    expect(screen.getByText('ADB View toolchain placeholder')).toBeInTheDocument();
  });
});
