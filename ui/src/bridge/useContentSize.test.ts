import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook } from '@testing-library/react';
import * as actions from './actions';
import { useContentSize } from './useContentSize';
import { measureExpandedTargetHeight, measureFlyoutEffectiveHeight } from './measureContentSize';

describe('useContentSize ResizeObserver integration', () => {
  let observedCallback: ResizeObserverCallback | null = null;
  const reports: { h: number; phase?: string }[] = [];

  beforeEach(() => {
    reports.length = 0;
    observedCallback = null;
    vi.spyOn(actions, 'reportContentSize').mockImplementation((h: number, phase?) => {
      reports.push({ h, phase });
    });

    class FakeRO {
      constructor(cb: ResizeObserverCallback) {
        observedCallback = cb;
      }
      observe() {}
      unobserve() {}
      disconnect() {}
    }
    vi.stubGlobal('ResizeObserver', FakeRO);
    vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => {
      cb(0);
      return 1;
    });
    vi.stubGlobal('cancelAnimationFrame', () => {});
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it('reports synchronously even when rAF is a no-op (hidden WebView)', () => {
    vi.stubGlobal('requestAnimationFrame', () => 1); // never invokes callback
    const header = document.createElement('header');
    Object.defineProperty(header, 'offsetHeight', { value: 40 });
    const content = document.createElement('div');
    Object.defineProperty(content, 'offsetHeight', { value: 200 });
    const headerRef = { current: header };
    const contentRef = { current: content };

    renderHook(() => useContentSize({ headerRef, contentRef }, []));

    expect(reports.length).toBeGreaterThanOrEqual(1);
    expect(reports[0]?.h).toBe(40 + 8 + 200 + 40);
  });

  it('coalesces duplicate ResizeObserver heights', () => {
    const header = document.createElement('header');
    Object.defineProperty(header, 'offsetHeight', { value: 40 });
    const content = document.createElement('div');
    Object.defineProperty(content, 'offsetHeight', { value: 100 });
    const headerRef = { current: header };
    const contentRef = { current: content };

    renderHook(() => useContentSize({ headerRef, contentRef }, []));
    const before = reports.length;
    observedCallback?.([], {} as ResizeObserver);
    observedCallback?.([], {} as ResizeObserver);
    // Same height → no additional unique reports beyond schedule noise with same value
    const unique = new Set(reports.map((r) => r.h));
    expect(unique.size).toBe(1);
    expect(reports.length).toBeGreaterThanOrEqual(before);
  });
});

describe('measureExpandedTargetHeight', () => {
  it('measures expanded geometry without hard-coded height', () => {
    const header = document.createElement('header');
    Object.defineProperty(header, 'offsetHeight', { value: 40 });

    const content = document.createElement('div');
    const card = document.createElement('div');
    card.dataset.deviceId = 'wifi-1';
    const grid = document.createElement('div');
    grid.dataset.testid = 'device-card-details-grid';
    grid.setAttribute('data-testid', 'device-card-details-grid');
    card.appendChild(grid);
    content.appendChild(card);

    Object.defineProperty(content, 'offsetHeight', {
      get() {
        return grid.classList.contains('is-expanded') ? 300 : 120;
      },
    });

    const collapsed = measureFlyoutEffectiveHeight(header, content);
    const expanded = measureExpandedTargetHeight(header, content, 'wifi-1');
    expect(expanded).toBeGreaterThan(collapsed);
    expect(grid.classList.contains('is-expanded')).toBe(false);
  });
});
