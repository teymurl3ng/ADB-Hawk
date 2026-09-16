import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { App } from './App';

describe('App placeholder', () => {
  it('renders the phase 1 toolchain placeholder', () => {
    render(<App />);
    expect(screen.getByText(/Native Windows shell placeholder/i)).toBeInTheDocument();
  });
});
