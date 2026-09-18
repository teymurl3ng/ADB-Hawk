import type { WebAction } from '../contract/actions';

export function isTestMode(): boolean {
  if (typeof window === 'undefined') return false;
  return (
    new URLSearchParams(window.location.search).has('fixture') || import.meta.env.MODE === 'test'
  );
}

export function postAction(action: WebAction): void {
  if (isTestMode()) {
    const win = window as unknown as { __adbActionCount?: Record<string, number> };
    win.__adbActionCount = win.__adbActionCount ?? {};
    win.__adbActionCount[action.type] = (win.__adbActionCount[action.type] ?? 0) + 1;
  }
  // @ts-expect-error WebView2 injects chrome.webview natively
  window.chrome?.webview?.postMessage(action);
}

export function sendReady(): void {
  postAction({ type: 'ready', protocol: 1 });
}

export function pin(): void {
  postAction({ type: 'pin' });
}

export function unpin(): void {
  postAction({ type: 'unpin' });
}

export function requestDismiss(): void {
  postAction({ type: 'requestDismiss' });
}

export function reportContentSize(heightEffectivePx: number): void {
  postAction({ type: 'reportContentSize', heightEffectivePx });
}
