export interface DiscoveryRepairCardAction {
  id: string;
  label: string;
  isAdmin: boolean;
  isRunning: boolean;
  runningLabel: string;
}

interface DiscoveryHealthCardProps {
  title: string;
  detail: string;
  evidence: string[];
  actionLabel: string;
  canCheckAgain: boolean;
  repairs: DiscoveryRepairCardAction[];
  resultLine: string | null;
  onCheckAgain: () => void;
  onRepair: (id: string) => void;
}

/**
 * Discovery-health surface (decision 0008 + amendment, DESIGN.md §18). All copy arrives
 * rendered from the backend; the card only displays it and forwards actions. Repair ids are
 * opaque backend descriptors — the frontend never constructs a command.
 */
export function DiscoveryHealthCard({
  title,
  detail,
  evidence,
  actionLabel,
  canCheckAgain,
  repairs,
  resultLine,
  onCheckAgain,
  onRepair,
}: DiscoveryHealthCardProps) {
  const anyRepairRunning = repairs.some((repair) => repair.isRunning);
  return (
    <div
      data-testid="discovery-health-card"
      className="rounded-lg bg-win-card border border-win-card-border p-3.5 app-region-no-drag"
    >
      <div className="flex items-start gap-2.5">
        <div className="w-8 h-8 rounded-full bg-win-amber-surface border border-win-amber-border flex items-center justify-center text-win-amber-text flex-shrink-0">
          <svg
            fill="none"
            height="16"
            stroke="currentColor"
            strokeLinecap="round"
            strokeLinejoin="round"
            strokeWidth="2"
            viewBox="0 0 24 24"
            width="16"
          >
            <path d="M21.2 15.9A10 10 0 1 1 8 2.8"></path>
            <path d="M22 22 16 16"></path>
            <path d="M9 15a6 6 0 0 1 6-6"></path>
          </svg>
        </div>
        <div className="min-w-0">
          <h3
            data-testid="discovery-health-title"
            className="text-[13.5px] font-semibold text-win-text-primary tracking-tight"
          >
            {title}
          </h3>
          <p className="text-[12px] text-win-text-secondary mt-1 leading-relaxed">{detail}</p>
        </div>
      </div>

      {evidence.length > 0 && (
        <ul data-testid="discovery-health-evidence" className="mt-2.5 space-y-1">
          {evidence.map((line) => (
            <li key={line} className="flex items-start gap-1.5 text-[12px] text-win-text-secondary">
              <span aria-hidden="true" className="text-win-text-muted mt-[1px]">
                •
              </span>
              <span>{line}</span>
            </li>
          ))}
        </ul>
      )}

      {resultLine && (
        <p
          data-testid="discovery-result-line"
          className="mt-2 text-[12px] text-win-text-secondary leading-relaxed"
        >
          {resultLine}
        </p>
      )}

      {repairs.length > 0 && (
        <div data-testid="discovery-repair-row" className="mt-3 flex flex-wrap items-center gap-2">
          {repairs.map((repair) => (
            <button
              key={repair.id}
              type="button"
              data-testid="discovery-repair-button"
              data-repair-id={repair.id}
              data-repair-admin={repair.isAdmin || undefined}
              disabled={repair.isRunning}
              onClick={() => onRepair(repair.id)}
              className="px-2.5 py-1 rounded-md text-[12px] font-medium fluent-btn border border-win-border-subtle bg-win-btn text-win-text-primary disabled:opacity-40 disabled:cursor-default"
            >
              {repair.isRunning ? repair.runningLabel : repair.label}
            </button>
          ))}
        </div>
      )}

      <div className="mt-3 flex justify-end">
        <button
          type="button"
          data-testid="discovery-check-again"
          disabled={!canCheckAgain || anyRepairRunning}
          onClick={onCheckAgain}
          className="px-2.5 py-1 rounded-md text-[12px] font-medium fluent-btn border border-win-border-subtle bg-win-btn text-win-text-primary disabled:opacity-40 disabled:cursor-default"
        >
          {actionLabel}
        </button>
      </div>
    </div>
  );
}
