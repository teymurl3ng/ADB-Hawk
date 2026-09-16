export function EmptyState() {
  return (
    <div className="flex-1 flex flex-col items-center">
      <div className="py-6 flex flex-col items-center text-center">
        <div className="w-10 h-10 rounded-full bg-white/[0.03] border border-white/[0.06] flex items-center justify-center text-neutral-400 mb-3">
          <svg
            fill="none"
            height="18"
            stroke="currentColor"
            strokeLinecap="round"
            strokeLinejoin="round"
            strokeWidth="1.75"
            viewBox="0 0 24 24"
            width="18"
          >
            <rect height="18" rx="2" width="12" x="6" y="3"></rect>
            <path d="M12 18h.01"></path>
          </svg>
        </div>
        <h3 className="text-[14.5px] font-semibold text-white tracking-tight">No ADB devices</h3>
        <p className="text-[12px] text-neutral-400 mt-1 max-w-[240px] leading-relaxed">
          Connect a device using USB or wireless debugging.
        </p>
      </div>
      <div className="pt-3 border-t border-win-borderSubtle flex items-center justify-center gap-2 w-full">
        <span className="static-dot bg-blue-400"></span>
        <span className="text-[11.5px] text-neutral-400 font-medium">
          Wireless discovery active
        </span>
      </div>
    </div>
  );
}
