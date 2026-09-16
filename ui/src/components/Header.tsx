import { togglePin } from '../bridge/actions';

interface HeaderProps {
  isPinned: boolean;
}

export function Header({ isPinned }: HeaderProps) {
  return (
    <header
      className="h-10 flex items-center justify-between mb-2 shrink-0 select-none"
      style={{ WebkitAppRegion: isPinned ? 'drag' : 'no-drag' } as React.CSSProperties}
    >
      <div className="flex items-center gap-2">
        <div className="w-5 h-5 flex items-center justify-center text-blue-400">
          <svg
            fill="none"
            height="18"
            stroke="currentColor"
            strokeLinecap="round"
            strokeLinejoin="round"
            strokeWidth="2"
            viewBox="0 0 24 24"
            width="18"
          >
            <rect height="18" rx="2" width="14" x="5" y="3"></rect>
            <path d="M12 18h.01"></path>
            <path d="M9 3v1"></path>
            <path d="M15 3v1"></path>
          </svg>
        </div>
        <span className="text-[14.5px] font-semibold tracking-tight text-white">ADB View</span>
      </div>
      <button
        onClick={togglePin}
        aria-label={isPinned ? 'Unpin flyout' : 'Pin flyout'}
        title={isPinned ? 'Unpin flyout' : 'Pin flyout'}
        style={{ WebkitAppRegion: 'no-drag' } as React.CSSProperties}
        className="hit-area-40 w-7 h-7 rounded flex items-center justify-center text-neutral-400 hover:text-white hover:bg-white/[0.06] fluent-btn"
      >
        {isPinned ? (
          <svg fill="currentColor" height="14" viewBox="0 0 24 24" width="14">
            <path d="M5 17h14v-1.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V6h1a1 1 0 0 0 1-1V3a1 1 0 0 0-1-1H8a1 1 0 0 0-1 1v2a1 1 0 0 0 1 1h1v4.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24Z"></path>
            <line
              x1="12"
              x2="12"
              y1="17"
              y2="22"
              stroke="currentColor"
              strokeWidth="2"
              strokeLinecap="round"
            ></line>
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
            <path d="M5 17h14v-1.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V6h1a1 1 0 0 0 1-1V3a1 1 0 0 0-1-1H8a1 1 0 0 0-1 1v2a1 1 0 0 0 1 1h1v4.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24Z"></path>
          </svg>
        )}
      </button>
    </header>
  );
}
