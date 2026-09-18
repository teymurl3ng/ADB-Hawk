import { useEffect, useRef, useState } from 'react';
import type { AppSnapshot } from '../contract/snapshot';
import { parseSnapshot } from '../contract/validate';
import { isTestMode, sendReady } from './actions';

/**
 * Manages native AppSnapshot synchronization over the WebView2 bridge.
 * Enforces revision monotonicity (revision > latest) and cleans up listeners on unmount.
 */
export function useSnapshot(): AppSnapshot | null {
  const [snapshot, setSnapshot] = useState<AppSnapshot | null>(null);
  const latestRevisionRef = useRef<number>(0);

  useEffect(() => {
    const handleMessage = (event: MessageEvent) => {
      const parsed = parseSnapshot(event.data);
      if (!parsed) {
        // Invalid or malformed snapshot, ignore safely
        return;
      }

      // Revision monotonicity rules:
      // revision < latest  => ignore stale
      // revision == latest => ignore duplicate
      // revision > latest  => accept
      if (parsed.revision <= latestRevisionRef.current) {
        return;
      }

      latestRevisionRef.current = parsed.revision;
      if (isTestMode()) {
        const win = window as unknown as {
          __adbSnapshot?: AppSnapshot;
          __adbSnapshotCount?: number;
        };
        win.__adbSnapshot = parsed;
        win.__adbSnapshotCount = (win.__adbSnapshotCount ?? 0) + 1;
      }
      setSnapshot(parsed);
    };

    // @ts-expect-error WebView2 injects chrome.webview natively
    const webview = window.chrome?.webview;
    if (webview) {
      webview.addEventListener('message', handleMessage);
      sendReady();
    } else if (typeof window !== 'undefined') {
      // In test/browser fallback environments without native webview
      window.addEventListener('message', handleMessage);
      sendReady();
    }

    return () => {
      if (webview) {
        webview.removeEventListener('message', handleMessage);
      } else if (typeof window !== 'undefined') {
        window.removeEventListener('message', handleMessage);
      }
    };
  }, []);

  return snapshot;
}
