import React from 'react';
import { togglePin } from './bridge/actions';

export function App() {
  const [isPinned, setIsPinned] = React.useState(false);

  React.useEffect(() => {
    const handler = (e: MessageEvent) => {
      if (e.data === 'pinned:true') setIsPinned(true);
      if (e.data === 'pinned:false') setIsPinned(false);
    };
    // @ts-expect-error WebView2 injects chrome.webview natively
    window.chrome?.webview?.addEventListener('message', handler);
    // @ts-expect-error WebView2 injects chrome.webview natively
    return () => window.chrome?.webview?.removeEventListener('message', handler);
  }, []);

  return (
    <main
      className="flex flex-col min-h-screen text-neutral-100"
      style={{ backgroundColor: 'transparent' }}
    >
      {/* Drag Region */}
      <div
        className="h-12 w-full flex items-center justify-between px-4 border-b border-neutral-800"
        style={{ WebkitAppRegion: 'drag' } as unknown as React.CSSProperties}
      >
        <span className="text-sm font-medium">ADB View - Phase 1</span>
        <button
          onClick={togglePin}
          style={{ WebkitAppRegion: 'no-drag' } as unknown as React.CSSProperties}
          className="text-xs bg-neutral-800 hover:bg-neutral-700 px-2 py-1 rounded"
        >
          Pinned: {isPinned ? 'Yes' : 'No'}
        </button>
      </div>

      <div
        className="flex-1 flex flex-col items-center justify-center p-4"
        style={{ WebkitAppRegion: 'no-drag' } as unknown as React.CSSProperties}
      >
        <p className="text-sm opacity-70 text-center">
          Native Windows shell placeholder. <br />
          Drag region is top 48px.
        </p>
      </div>
    </main>
  );
}
