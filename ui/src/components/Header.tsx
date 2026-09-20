import { pin, unpin } from '../bridge/actions';

interface HeaderProps {
  isPinned: boolean;
  headerRef?: React.RefObject<HTMLElement | null>;
}

/**
 * Pinned header: entire non-interactive surface is drag (WebView2 NC).
 * Explicit drag on identity children — inheritance alone is insufficient on WebView2.
 * Only the pin control (and its no-drag wrapper) is excluded.
 */
export function Header({ isPinned, headerRef }: HeaderProps) {
  const handlePinClick = () => {
    if (isPinned) {
      unpin();
    } else {
      pin();
    }
  };

  const drag = isPinned ? 'app-region-drag' : 'app-region-no-drag';

  return (
    <header
      ref={headerRef}
      data-testid="flyout-header"
      className={`h-10 flex items-center justify-between mb-2 select-none relative ${drag}`}
    >
      {/* Full-bleed drag layer behind chrome so upper/lower empty header pixels drag. */}
      {isPinned && (
        <div
          aria-hidden="true"
          data-testid="header-drag-layer"
          className="absolute inset-0 app-region-drag"
        />
      )}

      <div className={`relative z-[1] flex items-center gap-2 ${drag}`}>
        <div
          className={`w-5 h-5 flex items-center justify-center text-win-accent ${drag}`}
          data-testid="header-icon"
        >
          <svg
            fill="none"
            height="18"
            stroke="currentColor"
            strokeLinecap="round"
            strokeLinejoin="round"
            strokeWidth="2"
            viewBox="0 0 24 24"
            width="18"
            className={drag}
          >
            <rect height="18" rx="2" width="14" x="5" y="3"></rect>
            <path d="M12 18h.01"></path>
          </svg>
        </div>
        <span
          data-testid="header-title"
          className={`text-[13.5px] font-semibold tracking-tight text-win-text-primary ${drag}`}
        >
          ADB View
        </span>
      </div>

      <div className="relative z-[2] app-region-no-drag">
        <button
          onClick={handlePinClick}
          className={`hit-area-40 w-7 h-7 rounded flex items-center justify-center transition-colors fluent-btn app-region-no-drag ${
            isPinned
              ? 'bg-blue-500/20 text-blue-400 border border-blue-500/30 hover:bg-blue-500/30'
              : 'text-win-glyph-text hover:text-win-text-primary hover:bg-win-btn-hover'
          }`}
          title={isPinned ? 'Unpin flyout' : 'Pin flyout'}
          aria-label={isPinned ? 'Unpin flyout' : 'Pin flyout'}
          data-testid="pin-button"
        >
          {isPinned ? (
            <svg
              fill="none"
              height="14"
              stroke="currentColor"
              strokeLinecap="round"
              strokeLinejoin="round"
              strokeWidth="2"
              viewBox="0 0 24 24"
              width="14"
            >
              <path d="M12 17v5"></path>
              <path d="M9 10.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H8a2 2 0 0 0 0 4 1 1 0 0 1 1 1z"></path>
            </svg>
          ) : (
            <svg
              fill="none"
              height="14"
              stroke="currentColor"
              strokeLinecap="round"
              strokeLinejoin="round"
              strokeWidth="2"
              viewBox="0 0 24 24"
              width="14"
            >
              <line x1="12" x2="12" y1="17" y2="22"></line>
              <path d="M5 17h14v-1.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V6h.5a1.5 1.5 0 0 0 0-3h-7a1.5 1.5 0 0 0 0 3h.5v4.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24z"></path>
            </svg>
          )}
        </button>
      </div>
    </header>
  );
}
