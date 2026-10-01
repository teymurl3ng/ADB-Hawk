import { render, screen, fireEvent } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { PairingQrCard } from './PairingQrCard';

const SAMPLE_URI = 'data:image/svg+xml;base64,PHN2Zy8+';

const qr = {
  svgDataUri: SAMPLE_URI,
  instructions: [
    'On the phone, open Developer options → Wireless debugging.',
    'Tap Pair device with QR code and point the camera at this code.',
  ],
  cancelLabel: 'Cancel',
};

describe('PairingQrCard', () => {
  it('renders the code image, both instruction lines, and Cancel', () => {
    const onCancel = vi.fn();
    render(<PairingQrCard qr={qr} onCancel={onCancel} />);

    const image = screen.getByTestId('pairing-qr-image');
    expect(image.getAttribute('src')).toBe(SAMPLE_URI);
    expect(screen.getAllByTestId('pairing-qr-instruction')).toHaveLength(2);
    expect(screen.getByTestId('pairing-qr-cancel').textContent).toBe('Cancel');
  });

  it('forwards Cancel', () => {
    const onCancel = vi.fn();
    render(<PairingQrCard qr={qr} onCancel={onCancel} />);

    fireEvent.click(screen.getByTestId('pairing-qr-cancel'));
    expect(onCancel).toHaveBeenCalledTimes(1);
  });

  it('renders each instruction line the backend sent', () => {
    render(<PairingQrCard qr={{ ...qr, instructions: ['only line'] }} onCancel={vi.fn()} />);

    expect(screen.getAllByTestId('pairing-qr-instruction')).toHaveLength(1);
  });
});
