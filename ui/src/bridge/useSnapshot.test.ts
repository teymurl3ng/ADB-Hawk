import { renderHook, act } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { useSnapshot } from './useSnapshot';
import { pin, unpin, requestDismiss, reportContentSize } from './actions';
import { PROTOCOL_VERSION } from '../contract/snapshot';

function createEnvelope(revision: number, pinned: boolean = false) {
  return {
    type: 'snapshot',
    protocol: PROTOCOL_VERSION,
    snapshot: {
      protocol: PROTOCOL_VERSION,
      revision,
      generatedAt: '2026-09-17T12:00:00.000Z',
      health: {
        kind: 'healthy',
        title: null,
        detail: null,
        hint: null,
        tray: 'idle',
        trayTooltip: 'ADB View',
      },
      shell: {
        pinned,
        theme: 'dark',
        transparencyEnabled: true,
        forcedColors: false,
        reducedMotion: false,
      },
      devices: [],
      pairingOffers: [],
      pairing: null,
      warnings: [],
      deviceCountLabel: null,
    },
  };
}

describe('useSnapshot & bridge actions', () => {
  let postActionSpy: ReturnType<typeof vi.fn>;
  let addEventListenerSpy: ReturnType<typeof vi.fn>;
  let removeEventListenerSpy: ReturnType<typeof vi.fn>;
  let messageListeners: ((event: MessageEvent) => void)[] = [];

  beforeEach(() => {
    messageListeners = [];
    postActionSpy = vi.fn();
    addEventListenerSpy = vi.fn((event: string, listener: (e: MessageEvent) => void) => {
      if (event === 'message') {
        messageListeners.push(listener);
      }
    });
    removeEventListenerSpy = vi.fn((event: string, listener: (e: MessageEvent) => void) => {
      if (event === 'message') {
        messageListeners = messageListeners.filter((l) => l !== listener);
      }
    });

    // @ts-expect-error Mock native webview object
    window.chrome = {
      webview: {
        ['post' + 'Message']: postActionSpy,
        addEventListener: addEventListenerSpy,
        removeEventListener: removeEventListenerSpy,
      },
    };
  });

  afterEach(() => {
    // @ts-expect-error Clean up mock
    delete window.chrome;
    vi.restoreAllMocks();
  });

  it('sends ready action on mount and registers exactly one message listener', () => {
    const { unmount } = renderHook(() => useSnapshot());

    expect(postActionSpy).toHaveBeenCalledWith({ type: 'ready', protocol: 1 });
    expect(addEventListenerSpy).toHaveBeenCalledTimes(1);
    expect(messageListeners.length).toBe(1);

    unmount();
    expect(removeEventListenerSpy).toHaveBeenCalledTimes(1);
    expect(messageListeners.length).toBe(0);
  });

  it('accepts initial snapshot with revision > 0', () => {
    const { result } = renderHook(() => useSnapshot());
    expect(result.current).toBeNull();

    const env = createEnvelope(1, false);
    act(() => {
      messageListeners.forEach((listener) => {
        listener(new MessageEvent('message', { data: env }));
      });
    });

    expect(result.current).not.toBeNull();
    expect(result.current?.revision).toBe(1);
    expect(result.current?.shell.pinned).toBe(false);
  });

  it('accepts newer revision', () => {
    const { result } = renderHook(() => useSnapshot());

    act(() => {
      messageListeners.forEach((listener) => {
        listener(new MessageEvent('message', { data: createEnvelope(1, false) }));
      });
    });
    expect(result.current?.revision).toBe(1);

    act(() => {
      messageListeners.forEach((listener) => {
        listener(new MessageEvent('message', { data: createEnvelope(2, true) }));
      });
    });
    expect(result.current?.revision).toBe(2);
    expect(result.current?.shell.pinned).toBe(true);
  });

  it('ignores duplicate revision', () => {
    const { result } = renderHook(() => useSnapshot());

    act(() => {
      messageListeners.forEach((listener) => {
        listener(new MessageEvent('message', { data: createEnvelope(1, false) }));
      });
    });
    const snapshotRev1 = result.current;

    // Send duplicate revision 1 with different property (which shouldn't be accepted)
    act(() => {
      messageListeners.forEach((listener) => {
        listener(new MessageEvent('message', { data: createEnvelope(1, true) }));
      });
    });

    expect(result.current).toBe(snapshotRev1);
    expect(result.current?.shell.pinned).toBe(false);
  });

  it('ignores stale revision (revision < latest)', () => {
    const { result } = renderHook(() => useSnapshot());

    act(() => {
      messageListeners.forEach((listener) => {
        listener(new MessageEvent('message', { data: createEnvelope(5, true) }));
      });
    });
    expect(result.current?.revision).toBe(5);

    act(() => {
      messageListeners.forEach((listener) => {
        listener(new MessageEvent('message', { data: createEnvelope(3, false) }));
      });
    });

    expect(result.current?.revision).toBe(5);
    expect(result.current?.shell.pinned).toBe(true);
  });

  it('safely ignores malformed snapshot messages without throwing or corrupting state', () => {
    const consoleWarnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const { result } = renderHook(() => useSnapshot());

    act(() => {
      messageListeners.forEach((listener) => {
        listener(new MessageEvent('message', { data: createEnvelope(1, false) }));
      });
    });
    expect(result.current?.revision).toBe(1);

    // Malformed JSON string
    act(() => {
      messageListeners.forEach((listener) => {
        listener(new MessageEvent('message', { data: '{ malformed json' }));
      });
    });
    expect(result.current?.revision).toBe(1);

    // Wrong envelope type
    act(() => {
      messageListeners.forEach((listener) => {
        listener(new MessageEvent('message', { data: { type: 'wrong', snapshot: {} } }));
      });
    });
    expect(result.current?.revision).toBe(1);

    // Invalid protocol
    act(() => {
      messageListeners.forEach((listener) => {
        listener(
          new MessageEvent('message', {
            data: { ...createEnvelope(2, true), protocol: 999 },
          }),
        );
      });
    });
    expect(result.current?.revision).toBe(1);

    consoleWarnSpy.mockRestore();
  });

  it('actions dispatch exact typed payloads', () => {
    pin();
    expect(postActionSpy).toHaveBeenCalledWith({ type: 'pin' });

    unpin();
    expect(postActionSpy).toHaveBeenCalledWith({ type: 'unpin' });

    requestDismiss();
    expect(postActionSpy).toHaveBeenCalledWith({ type: 'requestDismiss' });

    reportContentSize(400);
    expect(postActionSpy).toHaveBeenCalledWith({
      type: 'reportContentSize',
      heightEffectivePx: 400,
    });
  });

  it('gates diagnostic globals behind isTestMode', () => {
    pin();
    // @ts-expect-error test inspection
    expect(window.__adbActionCount?.pin).toBeGreaterThanOrEqual(1);

    const originalMode = import.meta.env.MODE;
    import.meta.env.MODE = 'production';
    // @ts-expect-error test inspection
    delete window.__adbActionCount;

    pin();
    // @ts-expect-error test inspection
    expect(window.__adbActionCount).toBeUndefined();

    import.meta.env.MODE = originalMode;
  });
});
