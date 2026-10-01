export function EmptyState() {
  return (
    <div
      data-testid="empty-state"
      className="rounded-lg bg-win-card border border-win-card-border p-6 flex flex-col items-center justify-center text-center"
    >
      <div className="w-10 h-10 rounded-full bg-win-glyph-bg border border-win-glyph-border flex items-center justify-center text-win-text-muted mb-3">
        <svg
          fill="none"
          height="20"
          stroke="currentColor"
          strokeLinecap="round"
          strokeLinejoin="round"
          strokeWidth="1.5"
          viewBox="0 0 24 24"
          width="20"
        >
          <rect height="18" rx="2" width="12" x="6" y="3"></rect>
          <path d="M12 18h.01"></path>
        </svg>
      </div>
      <h3 className="text-[14px] font-semibold text-win-text-primary tracking-tight mb-1">
        No devices connected
      </h3>
      <p className="text-[12px] text-win-text-secondary max-w-[220px] leading-relaxed">
        Attach a device via USB or connect over Wi-Fi to observe ADB state.
      </p>
    </div>
  );
}
