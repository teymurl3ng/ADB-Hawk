import { describe, expect, it } from 'vitest';
import motionCss from './device-card-motion.css.txt?raw';

describe('device-card disclosure motion tokens (v1 freeze)', () => {
  it('uses state motion for opacity/chevron only', () => {
    expect(motionCss.length).toBeGreaterThan(40);
    expect(motionCss).toMatch(/var\(--motion-state\)/);
    expect(motionCss).toMatch(/var\(--motion-easing\)/);
    expect(motionCss).not.toMatch(/var\(--motion-structural\)/);
  });

  it('uses instant final grid rows without transitioning layout height', () => {
    expect(motionCss).toMatch(/grid-template-rows:\s*0fr/);
    expect(motionCss).toMatch(/grid-template-rows:\s*1fr/);
    expect(motionCss).not.toMatch(
      /grid-template-rows[^;]*transition|transition:[^;]*grid-template-rows/,
    );
    expect(motionCss).not.toMatch(/max-height:\s*999/);
    expect(motionCss).not.toMatch(/scaleY|translateY|clip-path|FLIP|flip-/i);
  });
});
