import type { WebAction } from '../contract/actions';

export function isTestMode(): boolean {
  if (typeof window === 'undefined') return false;
  return (
    new URLSearchParams(window.location.search).has('fixture') || import.meta.env.MODE === 'test'
  );
}

export function postAction(action: WebAction): void {
  if (isTestMode()) {
    const win = window as unknown as {
      __adbActionCount?: Record<string, number>;
      __adbActions?: WebAction[];
    };
    win.__adbActionCount = win.__adbActionCount ?? {};
    win.__adbActionCount[action.type] = (win.__adbActionCount[action.type] ?? 0) + 1;
    win.__adbActions = win.__adbActions ?? [];
    win.__adbActions.push(action);
  }
  // @ts-expect-error WebView2 injects chrome.webview natively
  window.chrome?.webview?.postMessage(action);
}

export function pairStart(offerId: string): void {
  postAction({ type: 'pairStart', offerId });
}

export function pairSubmit(offerId: string, code: string): void {
  postAction({ type: 'pairSubmit', offerId, code });
}

export function pairCancel(offerId: string): void {
  postAction({ type: 'pairCancel', offerId });
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

export function reportContentSize(
  heightEffectivePx: number,
  phase: 'live' | 'structural' | 'reconcile' = 'live',
  layoutKey?: string,
): void {
  if (phase === 'live' && !layoutKey) {
    postAction({ type: 'reportContentSize', heightEffectivePx });
  } else if (phase === 'live') {
    postAction({ type: 'reportContentSize', heightEffectivePx, layoutKey });
  } else if (layoutKey) {
    postAction({ type: 'reportContentSize', heightEffectivePx, phase, layoutKey });
  } else {
    postAction({ type: 'reportContentSize', heightEffectivePx, phase });
  }
}
