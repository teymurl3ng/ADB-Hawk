import { reportContentSize } from './actions';

export type ContentSizePhase = 'live' | 'structural' | 'reconcile';

const HEADER_MARGIN_PX = 8;
const CHROME_PADDING_PX = 40;

/** Measure current flyout effective height from header + content wrappers. */
export function measureFlyoutEffectiveHeight(
  headerEl: HTMLElement,
  contentEl: HTMLElement,
): number {
  const headerHeight = headerEl.offsetHeight + HEADER_MARGIN_PX;
  const contentHeight = contentEl.offsetHeight;
  return Math.round(headerHeight + contentHeight + CHROME_PADDING_PX);
}

/**
 * Synchronously measure the final flyout height for a given expansion set by
 * forcing grid expanded state with transitions disabled for one layout pass.
 * No hard-coded expanded heights — uses real DOM geometry.
 */
export function measureExpandedTargetHeight(
  headerEl: HTMLElement,
  contentEl: HTMLElement,
  expandDeviceId: string | null,
): number {
  const grids = Array.from(
    contentEl.querySelectorAll<HTMLElement>('[data-testid="device-card-details-grid"]'),
  );
  const snapshots = grids.map((grid) => ({
    grid,
    className: grid.className,
    transition: grid.style.transition,
  }));

  try {
    for (const { grid } of snapshots) {
      const card = grid.closest<HTMLElement>('[data-device-id]');
      const id = card?.dataset.deviceId ?? null;
      grid.style.transition = 'none';
      if (expandDeviceId && id === expandDeviceId) {
        grid.classList.add('is-expanded');
      } else {
        grid.classList.remove('is-expanded');
      }
    }
    // Force reflow so offsetHeight reflects the final geometry.
    void contentEl.offsetHeight;
    return measureFlyoutEffectiveHeight(headerEl, contentEl);
  } finally {
    for (const snap of snapshots) {
      snap.grid.className = snap.className;
      snap.grid.style.transition = snap.transition;
    }
    void contentEl.offsetHeight;
  }
}

export function reportMeasuredContentSize(
  heightEffectivePx: number,
  phase: ContentSizePhase = 'live',
): void {
  reportContentSize(heightEffectivePx, phase);
}
