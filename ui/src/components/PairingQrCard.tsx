interface PairingQrView {
  svgDataUri: string;
  instructions: string[];
  cancelLabel: string;
}

interface PairingQrCardProps {
  qr: PairingQrView;
  onCancel: () => void;
}

/**
 * QR pairing session (decision 0010, DESIGN.md §15). The backend renders the code as an SVG
 * image; the card displays it with the instruction lines and forwards Cancel. The payload
 * behind the image never enters frontend state.
 */
export function PairingQrCard({ qr, onCancel }: PairingQrCardProps) {
  return (
    <div
      data-testid="pairing-qr-card"
      data-pairing-shell="stable"
      className="rounded-lg border p-3.5 app-region-no-drag bg-win-card border-win-card-border"
    >
      <div data-pairing-region="header" className="flex items-start gap-2.5">
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
              Pair a device over Wi-Fi
            </h3>
          </div>
        </div>
      </div>

      <div data-pairing-region="content">
        <div className="mt-3 flex justify-center">
          {/* Backend-rendered SVG (display-only, single-use credential). */}
          <img
            data-testid="pairing-qr-image"
            src={qr.svgDataUri}
            alt="Pairing QR code"
            width="220"
            height="220"
            className="rounded-md bg-white p-2"
          />
        </div>

        <div className="mt-3 space-y-1">
          {qr.instructions.map((line) => (
            <p
              key={line}
              data-testid="pairing-qr-instruction"
              className="text-[12px] text-win-text-secondary leading-relaxed"
            >
              {line}
            </p>
          ))}
        </div>

        <div className="mt-3 flex justify-end">
          <button
            type="button"
            data-testid="pairing-qr-cancel"
            onClick={onCancel}
            className="px-2.5 py-1 rounded-md text-[12px] font-medium fluent-btn border border-win-border-subtle bg-win-btn text-win-text-primary"
          >
            {qr.cancelLabel}
          </button>
        </div>
      </div>
    </div>
  );
}
