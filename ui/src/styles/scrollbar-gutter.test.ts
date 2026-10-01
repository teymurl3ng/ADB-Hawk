import { describe, expect, it } from 'vitest';
import motionCss from './device-card-motion.css.txt?raw';

describe('flyout scroll gutter', () => {
  it('reserves classic scrollbar space on both inline edges', () => {
    expect(motionCss).toMatch(/\.flyout-scroll\s*\{[^}]*scrollbar-gutter:\s*stable both-edges/s);
  });
});
