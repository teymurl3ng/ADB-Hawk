import type { DeviceInfo } from '../types';

interface DeviceCardProps {
  device: DeviceInfo;
  onToggleExpand?: (id: string) => void;
  onCopy?: (text: string, type: 'serial' | 'address') => void;
  copiedId?: 'serial' | 'address' | null;
}

export function DeviceCard({ device, onToggleExpand, onCopy, copiedId }: DeviceCardProps) {
  const isExpanded = device.isExpanded;

  const stateColors = {
    connected: {
      text: 'text-emerald-400',
      bg: 'bg-emerald-500/10',
      border: 'border-emerald-500/20',
      dot: 'bg-emerald-400',
      label: 'Connected',
    },
    unauthorized: {
      text: 'text-amber-400',
      bg: 'bg-amber-500/10',
      border: 'border-amber-500/20',
      dot: 'bg-amber-400',
      label: 'Unauthorized',
    },
    offline: {
      text: 'text-neutral-400',
      bg: 'bg-neutral-500/10',
      border: 'border-neutral-500/20',
      dot: 'bg-neutral-400',
      label: 'Offline',
    },
    recovery: {
      text: 'text-sky-400',
      bg: 'bg-sky-500/10',
      border: 'border-sky-500/20',
      dot: 'bg-sky-400',
      label: 'Recovery',
    },
    bootloader: {
      text: 'text-sky-400',
      bg: 'bg-sky-500/10',
      border: 'border-sky-500/20',
      dot: 'bg-sky-400',
      label: 'Bootloader',
    },
    sideload: {
      text: 'text-sky-400',
      bg: 'bg-sky-500/10',
      border: 'border-sky-500/20',
      dot: 'bg-sky-400',
      label: 'Sideload',
    },
    unknown: {
      text: 'text-sky-400',
      bg: 'bg-sky-500/10',
      border: 'border-sky-500/20',
      dot: 'bg-sky-400',
      label: 'Unknown',
    },
  };

  const style = stateColors[device.state] || stateColors.unknown;

  return (
    <div
      className={`rounded-lg bg-win-card border border-win-cardBorder p-3 transition-colors ${!isExpanded ? 'hover:bg-win-cardHover cursor-pointer group' : ''}`}
      onClick={() => {
        if (!isExpanded && onToggleExpand) {
          onToggleExpand(device.id);
        }
      }}
    >
      <div className="flex items-center justify-between mb-2.5">
        <div className="flex items-center gap-2.5">
          <div className="w-8 h-8 rounded bg-white/[0.04] border border-white/[0.06] flex items-center justify-center text-neutral-300">
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
              <rect height="18" rx="2" width="12" x="6" y="3"></rect>
              <path d="M12 18h.01"></path>
            </svg>
          </div>
          <div>
            <h3 className="text-[15px] font-semibold text-white tracking-tight leading-tight">
              {device.name}
            </h3>
            <p className="text-[12px] text-neutral-400 mt-0.5">{device.stateAgeText}</p>
          </div>
        </div>

        {isExpanded ? (
          <div
            className="hit-area-40 w-6 h-6 flex items-center justify-center text-neutral-300 hover:text-white transition-colors cursor-pointer"
            onClick={(e) => {
              e.stopPropagation();
              onToggleExpand?.(device.id);
            }}
          >
            <svg
              fill="none"
              height="14"
              stroke="currentColor"
              strokeLinecap="round"
              strokeLinejoin="round"
              strokeWidth="2"
              viewBox="0 0 24 24"
              width="14"
            >
              <path d="m18 15-6-6-6 6"></path>
            </svg>
          </div>
        ) : (
          <div className="hit-area-40 w-6 h-6 flex items-center justify-center text-neutral-500 group-hover:text-neutral-300 transition-colors">
            <svg
              fill="none"
              height="14"
              stroke="currentColor"
              strokeLinecap="round"
              strokeLinejoin="round"
              strokeWidth="2"
              viewBox="0 0 24 24"
              width="14"
            >
              <path d="m6 9 6 6 6-6"></path>
            </svg>
          </div>
        )}
      </div>

      <div className="flex items-center justify-between pt-2 border-t border-win-borderSubtle">
        <div className="flex items-center gap-2">
          <div
            className={`inline-flex items-center gap-1.5 px-2 py-0.5 rounded-full ${style.bg} border ${style.border} ${style.text} text-[12px] font-medium`}
          >
            <span className={`static-dot ${style.dot}`}></span>
            <span>{style.label}</span>
          </div>

          {device.transport !== 'unknown' && (
            <div className="flex items-center gap-1 text-[12px] text-neutral-400 px-1.5 py-0.5">
              {device.transport === 'wifi' ? (
                <svg
                  fill="none"
                  height="13"
                  stroke="currentColor"
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  strokeWidth="2"
                  viewBox="0 0 24 24"
                  width="13"
                >
                  <path d="M12 20h.01"></path>
                  <path d="M2 8.82a15 15 0 0 1 20 0"></path>
                  <path d="M5 12.859a10 10 0 0 1 14 0"></path>
                  <path d="M8.5 16.429a5 5 0 0 1 7 0"></path>
                </svg>
              ) : (
                <svg
                  fill="none"
                  height="13"
                  stroke="currentColor"
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  strokeWidth="2"
                  viewBox="0 0 24 24"
                  width="13"
                >
                  <path d="M8 7v10"></path>
                  <path d="M16 7v10"></path>
                  <path d="M8 7h8"></path>
                  <path d="M8 17h8"></path>
                  <path d="M12 2v20"></path>
                </svg>
              )}
              <span>{device.transport === 'wifi' ? 'Wi-Fi' : 'USB'}</span>
            </div>
          )}
        </div>

        {typeof device.battery === 'number' && (
          <div className="flex items-center gap-1.5 text-[12px] text-neutral-300 font-medium">
            <span>{device.battery}%</span>
            <svg
              className="text-neutral-400"
              fill="none"
              height="15"
              stroke="currentColor"
              strokeLinecap="round"
              strokeLinejoin="round"
              strokeWidth="2"
              viewBox="0 0 24 24"
              width="15"
            >
              <rect height="10" rx="2" ry="2" width="16" x="2" y="7"></rect>
              <line x1="22" x2="22" y1="11" y2="13"></line>
              <line x1="6" x2="6" y1="11" y2="13"></line>
              <line x1="10" x2="10" y1="11" y2="13"></line>
              <line x1="14" x2="14" y1="11" y2="13"></line>
            </svg>
          </div>
        )}
      </div>

      {isExpanded && (
        <div className="mt-2.5 pt-2.5 border-t border-white/[0.06] space-y-2">
          {device.model && (
            <div className="flex items-center justify-between text-[12px]">
              <span className="text-neutral-500 font-normal">Model</span>
              <span className="text-neutral-300 font-mono text-[12px]">{device.model}</span>
            </div>
          )}
          {device.androidVersion && (
            <div className="flex items-center justify-between text-[12px]">
              <span className="text-neutral-500 font-normal">Android</span>
              <span className="text-neutral-300 font-mono text-[12px]">
                {device.androidVersion}
              </span>
            </div>
          )}
          {device.address && (
            <div
              className={`flex items-center justify-between text-[12px] ${copiedId === 'address' ? 'bg-emerald-500/[0.05] -mx-1 px-1 py-0.5 rounded transition-all' : 'group/item'}`}
            >
              <span
                className={
                  copiedId === 'address'
                    ? 'text-neutral-400 font-normal'
                    : 'text-neutral-500 font-normal'
                }
              >
                Address
              </span>
              <div className="flex items-center gap-1.5">
                <span
                  className={`${copiedId === 'address' ? 'text-emerald-400' : 'text-neutral-300'} font-mono text-[11.5px]`}
                >
                  {device.address}
                </span>
                {copiedId === 'address' ? (
                  <span className="inline-flex items-center gap-1 text-[11px] text-emerald-400 font-medium bg-emerald-500/10 border border-emerald-500/20 px-1.5 py-0.5 rounded">
                    <svg
                      fill="none"
                      height="10"
                      stroke="currentColor"
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      strokeWidth="2.5"
                      viewBox="0 0 24 24"
                      width="10"
                    >
                      <polyline points="20 6 9 17 4 12"></polyline>
                    </svg>
                    Copied
                  </span>
                ) : (
                  <button
                    onClick={(e) => {
                      e.stopPropagation();
                      onCopy?.(device.address!, 'address');
                    }}
                    className="hit-area-40 w-5 h-5 rounded flex items-center justify-center text-neutral-500 hover:text-white hover:bg-white/[0.08] fluent-btn"
                    title="Copy Address"
                  >
                    <svg
                      fill="none"
                      height="12"
                      stroke="currentColor"
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      strokeWidth="2"
                      viewBox="0 0 24 24"
                      width="12"
                    >
                      <rect height="14" rx="2" ry="2" width="14" x="8" y="8"></rect>
                      <path d="M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2"></path>
                    </svg>
                  </button>
                )}
              </div>
            </div>
          )}
          {device.serial && (
            <div
              className={`flex items-center justify-between text-[12px] ${copiedId === 'serial' ? 'bg-emerald-500/[0.05] -mx-1 px-1 py-0.5 rounded transition-all' : 'group/item'}`}
            >
              <span
                className={
                  copiedId === 'serial'
                    ? 'text-neutral-400 font-normal'
                    : 'text-neutral-500 font-normal'
                }
              >
                Serial
              </span>
              <div className="flex items-center gap-1.5">
                <span
                  className={`${copiedId === 'serial' ? 'text-emerald-400' : 'text-neutral-300'} font-mono text-[11.5px]`}
                >
                  {device.serial}
                </span>
                {copiedId === 'serial' ? (
                  <span className="inline-flex items-center gap-1 text-[11px] text-emerald-400 font-medium bg-emerald-500/10 border border-emerald-500/20 px-1.5 py-0.5 rounded">
                    <svg
                      fill="none"
                      height="10"
                      stroke="currentColor"
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      strokeWidth="2.5"
                      viewBox="0 0 24 24"
                      width="10"
                    >
                      <polyline points="20 6 9 17 4 12"></polyline>
                    </svg>
                    Copied
                  </span>
                ) : (
                  <button
                    onClick={(e) => {
                      e.stopPropagation();
                      onCopy?.(device.serial!, 'serial');
                    }}
                    className="hit-area-40 w-5 h-5 rounded flex items-center justify-center text-neutral-500 hover:text-white hover:bg-white/[0.08] fluent-btn"
                    title="Copy Serial"
                  >
                    <svg
                      fill="none"
                      height="12"
                      stroke="currentColor"
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      strokeWidth="2"
                      viewBox="0 0 24 24"
                      width="12"
                    >
                      <rect height="14" rx="2" ry="2" width="14" x="8" y="8"></rect>
                      <path d="M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2"></path>
                    </svg>
                  </button>
                )}
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
