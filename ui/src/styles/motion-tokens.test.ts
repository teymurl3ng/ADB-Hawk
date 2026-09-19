import { describe, expect, it } from 'vitest';
import motionCss from './device-card-motion.css.txt?raw';

describe('structural motion tokens', () => {
  it('expansion CSS references frozen structural/state motion vars and easing', () => {
    expect(motionCss.length).toBeGreaterThan(40);
    expect(motionCss).toMatch(/var\(--motion-structural\)/);
    expect(motionCss).toMatch(/var\(--motion-state\)/);
    expect(motionCss).toMatch(/var\(--motion-easing\)/);
  });

  it('uses grid-template-rows 0fr→1fr without hard-coded expanded heights', () => {
    expect(motionCss).toMatch(/grid-template-rows:\s*0fr/);
    expect(motionCss).toMatch(/grid-template-rows:\s*1fr/);
    expect(motionCss).not.toMatch(/max-height:\s*999/);
    expect(motionCss).not.toMatch(/height:\s*auto\s*!important/);
  });
});
