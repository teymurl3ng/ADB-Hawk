import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useContentSize } from './useContentSize';
import * as actions from './actions';

describe('useContentSize ResizeObserver integration', () => {
  let observedCallback: ResizeObserverCallback | null = null;
  const heights: number[] = [];

  beforeEach(() => {
    heights.length = 0;
    observedCallback = null;
    vi.spyOn(actions, 'reportContentSize').mockImplementation((h: number) => {
      heights.push(h);
    });

    class FakeRO {
      constructor(cb: ResizeObserverCallback) {
        observedCallback = cb;
      }
      observe() {
        /* no-op */
      }
      unobserve() {
        /* no-op */
      }
      disconnect() {
        /* no-op */
      }
    }
    vi.stubGlobal('ResizeObserver', FakeRO);
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it('reports intermediate heights when observed content grows (normal motion path)', async () => {
    const header = document.createElement('header');
    Object.defineProperty(header, 'offsetHeight', { value: 40, configurable: true });
    const content = document.createElement('div');
    let contentHeight = 100;
    Object.defineProperty(content, 'offsetHeight', {
      get: () => contentHeight,
      configurable: true,
    });

    const headerRef = { current: header };
    const contentRef = { current: content };

    renderHook(() => useContentSize({ headerRef, contentRef }, []));

    await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
    expect(heights.length).toBeGreaterThanOrEqual(1);
    const first = heights[0]!;

    contentHeight = 180;
    observedCallback?.([], {} as ResizeObserver);
    await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));

    contentHeight = 260;
    observedCallback?.([], {} as ResizeObserver);
    await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));

    expect(heights.length).toBeGreaterThanOrEqual(3);
    expect(heights[heights.length - 1]).toBeGreaterThan(first);
    // Distinct intermediate values — not only initial+final jump.
    const unique = new Set(heights);
    expect(unique.size).toBeGreaterThanOrEqual(3);
  });

  it('suppresses duplicate consecutive reports', async () => {
    const header = document.createElement('header');
    Object.defineProperty(header, 'offsetHeight', { value: 40, configurable: true });
    const content = document.createElement('div');
    Object.defineProperty(content, 'offsetHeight', { value: 120, configurable: true });

    renderHook(() =>
      useContentSize({ headerRef: { current: header }, contentRef: { current: content } }, []),
    );

    await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
    const n = heights.length;
    observedCallback?.([], {} as ResizeObserver);
    await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
    expect(heights.length).toBe(n);
  });
});
