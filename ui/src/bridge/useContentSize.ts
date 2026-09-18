import { useEffect, useRef } from 'react';
import { reportContentSize } from './actions';

interface ContentSizeOptions {
  headerRef: React.RefObject<HTMLElement | null>;
  contentRef: React.RefObject<HTMLElement | null>;
}

/**
 * Observes header and content DOM elements to measure effective flyout height in CSS pixels.
 * Uses requestAnimationFrame coalescing and suppresses duplicate consecutive reports.
 */
export function useContentSize(
  { headerRef, contentRef }: ContentSizeOptions,
  deps: unknown[] = [],
): void {
  const lastReportedHeightRef = useRef<number | null>(null);
  const rafIdRef = useRef<number | null>(null);

  useEffect(() => {
    const measureAndReport = () => {
      if (rafIdRef.current !== null) {
        cancelAnimationFrame(rafIdRef.current);
      }

      rafIdRef.current = requestAnimationFrame(() => {
        rafIdRef.current = null;
        const headerEl = headerRef.current;
        const contentEl = contentRef.current;
        if (!headerEl || !contentEl) return;

        // Header height (h-10: 40px) + mb-2 (8px) = 48px
        const headerHeight = headerEl.offsetHeight + 8;
        // Unconstrained content wrapper height
        const contentHeight = contentEl.offsetHeight;
        // Outer chrome: main p-4 (32px) + scroll container pb-2 (8px) = 40px
        const chromePadding = 40;

        const effectiveHeight = Math.round(headerHeight + contentHeight + chromePadding);

        if (effectiveHeight === lastReportedHeightRef.current) {
          return; // Suppress duplicate/unchanged reports
        }

        lastReportedHeightRef.current = effectiveHeight;
        reportContentSize(effectiveHeight);
      });
    };

    measureAndReport();

    let observer: ResizeObserver | null = null;
    if (typeof ResizeObserver !== 'undefined') {
      observer = new ResizeObserver(() => {
        measureAndReport();
      });

      if (headerRef.current) observer.observe(headerRef.current);
      if (contentRef.current) observer.observe(contentRef.current);
    }

    return () => {
      if (rafIdRef.current !== null) {
        cancelAnimationFrame(rafIdRef.current);
        rafIdRef.current = null;
      }
      if (observer) {
        observer.disconnect();
      }
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);
}
