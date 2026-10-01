import { useEffect, useRef } from 'react';
import { reportContentSize } from './actions';
import { measureFlyoutEffectiveHeight } from './measureContentSize';

interface ContentSizeOptions {
  headerRef: React.RefObject<HTMLElement | null>;
  contentRef: React.RefObject<HTMLElement | null>;
}

/**
 * Steady-state content size observer.
 * Host applies the reported height as an instant native step whenever it differs
 * from the applied height, visible or hidden (decision 0012, amending 0005).
 */
export function useContentSize(
  { headerRef, contentRef }: ContentSizeOptions,
  deps: unknown[] = [],
): void {
  const lastReportedHeightRef = useRef<number | null>(null);
  const rafIdRef = useRef<number | null>(null);
  const timeoutIdRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    const measureAndReport = () => {
      const headerEl = headerRef.current;
      const contentEl = contentRef.current;
      if (!headerEl || !contentEl) return;

      const effectiveHeight = measureFlyoutEffectiveHeight(headerEl, contentEl);
      if (effectiveHeight === lastReportedHeightRef.current) {
        return;
      }

      lastReportedHeightRef.current = effectiveHeight;
      reportContentSize(effectiveHeight, 'live');
    };

    const scheduleMeasure = () => {
      measureAndReport();

      if (rafIdRef.current !== null) {
        cancelAnimationFrame(rafIdRef.current);
      }
      rafIdRef.current = requestAnimationFrame(() => {
        rafIdRef.current = null;
        measureAndReport();
      });

      if (timeoutIdRef.current !== null) {
        clearTimeout(timeoutIdRef.current);
      }
      timeoutIdRef.current = setTimeout(() => {
        timeoutIdRef.current = null;
        measureAndReport();
      }, 0);
    };

    scheduleMeasure();

    let observer: ResizeObserver | null = null;
    if (typeof ResizeObserver !== 'undefined') {
      observer = new ResizeObserver(() => {
        scheduleMeasure();
      });

      if (headerRef.current) observer.observe(headerRef.current);
      if (contentRef.current) observer.observe(contentRef.current);
    }

    return () => {
      if (rafIdRef.current !== null) {
        cancelAnimationFrame(rafIdRef.current);
        rafIdRef.current = null;
      }
      if (timeoutIdRef.current !== null) {
        clearTimeout(timeoutIdRef.current);
        timeoutIdRef.current = null;
      }
      if (observer) {
        observer.disconnect();
      }
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);
}
