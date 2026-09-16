export function togglePin() {
  // @ts-expect-error WebView2 injects chrome.webview natively
  window.chrome?.webview?.postMessage('toggle_pin');
}
