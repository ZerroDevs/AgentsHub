import React, { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import {
  MessageSquare,
  LayoutDashboard,
  GitFork,
  Users,
  Cpu,
  Sliders,
} from 'lucide-react';
import { ActiveView } from '../../types/agent';
import { useSidecarStore, RAIL_VIEW_LABELS } from '../../store/useSidecarStore';
import { brandLogo } from '../../lib/toolIcons';

/**
 * The icon rail.
 *
 * Order is changed from a context menu rather than by dragging. HTML5 drag-and-drop inside the
 * WebView2 host left the whole window blank and unresponsive after a drop on another icon — the
 * native drag loop and the compositor do not survive it — so the mouse path is a menu and the
 * keyboard path is Alt+Arrow.
 */
export const PrimaryRail: React.FC = () => {
  const {
    activeView,
    setActiveView,
    railOrder,
    setRailOrder,
    resetRailOrder,
    hiddenRailViews,
    toggleRailViewHidden,
    railShortcutsEnabled,
    railShortcuts,
  } = useSidecarStore();

  /**
   * The rail lives inside a width-animated, overflow-hidden wrapper, so a tooltip anchored inside it
   * would be clipped to 56px. Rendered into the body instead, positioned from the hovered button.
   */
  const [tooltip, setTooltip] = useState<{ id: ActiveView; label: string; top: number } | null>(null);

  /** Where the menu is open, and for which view. */
  const [menu, setMenu] = useState<{ id: ActiveView; left: number; top: number } | null>(null);

  // Only the glyphs live here: names come from the store, so the rail and the Settings checklist
  // cannot drift apart.
  const icons: Record<ActiveView, React.ReactNode> = {
    chat: <MessageSquare className="w-5 h-5" />,
    profiles: <Users className="w-5 h-5" />,
    mcpskills: <Cpu className="w-5 h-5" />,
    dashboard: <LayoutDashboard className="w-5 h-5" />,
    repositories: <GitFork className="w-5 h-5" />,
    settings: <Sliders className="w-5 h-5" />,
  };

  // A view added in a later version is not in the saved order yet, so it is appended rather than
  // dropped from the rail.
  const order = [...railOrder, ...(Object.keys(icons) as ActiveView[]).filter(id => !railOrder.includes(id))];
  const shown = order.filter(id => !hiddenRailViews.includes(id));

  const move = (id: ActiveView, delta: number) => {
    const from = order.indexOf(id);
    const to = from + delta;
    if (from < 0 || to < 0 || to >= order.length) return;

    const next = [...order];
    next.splice(from, 1);
    next.splice(to, 0, id);
    setRailOrder(next);
  };

  // Escape closes the menu, the same way it closes the app's dialogs.
  useEffect(() => {
    if (!menu) return;

    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setMenu(null);
    };

    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [menu]);

  return (
    <nav
      id="primary-rail"
      className="w-14 bg-surface-sidebar border-r border-surface-border flex flex-col items-center py-3 z-20 flex-shrink-0 select-none"
    >
      {/* Top Group: Brand Badge & Nav Items */}
      <div className="flex flex-col items-center space-y-3 w-full">
        {/* The app's own mark, drawn by its own artwork. A gradient tile with a generic cat
            glyph was the loudest tell that this is not a designed product. */}
        <button
          onClick={() => setActiveView('chat')}
          className="mb-1 w-9 h-9 rounded-lg transition hover:brightness-110 active:scale-95"
          title="Agents Hub"
          aria-label="Agents Hub"
        >
          <img src={brandLogo} alt="" draggable={false} className="w-full h-full rounded-lg object-contain" />
        </button>

        {/* Primary View Switcher Buttons, in the store's order, minus the ones taken off the rail. */}
        <div className="flex flex-col items-center space-y-1.5 w-full px-2">
          {shown.map(item => {
            const isActive = activeView === item;

            return (
              <button
                key={item}
                onClick={() => setActiveView(item)}
                onContextMenu={event => {
                  event.preventDefault();
                  const box = event.currentTarget.getBoundingClientRect();
                  setMenu({ id: item, left: box.right + 8, top: box.top });
                }}
                onKeyDown={event => {
                  if (!event.altKey) return;
                  if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') return;
                  event.preventDefault();
                  move(item, event.key === 'ArrowUp' ? -1 : 1);
                }}
                onMouseEnter={event =>
                  setTooltip({
                    id: item,
                    label: RAIL_VIEW_LABELS[item],
                    top: event.currentTarget.getBoundingClientRect().top,
                  })
                }
                onMouseLeave={() => setTooltip(null)}
                onFocus={event =>
                  setTooltip({
                    id: item,
                    label: RAIL_VIEW_LABELS[item],
                    top: event.currentTarget.getBoundingClientRect().top,
                  })
                }
                onBlur={() => setTooltip(null)}
                className={`w-10 h-10 rounded-xl flex items-center justify-center transition relative ${
                  isActive
                    ? 'bg-accent-soft text-accent border border-sky-500/30'
                    : 'text-content-muted hover:text-content-main hover:bg-surface-card'
                }`}
                title={RAIL_VIEW_LABELS[item]}
                aria-haspopup="menu"
                aria-label={`${RAIL_VIEW_LABELS[item]} — right-click for options, Alt+Arrow to move`}
              >
                {icons[item]}
              </button>
            );
          })}
        </div>
      </div>

      {menu
        ? createPortal(
            <>
              <div className="fixed inset-0 z-[109]" onClick={() => setMenu(null)} onContextMenu={() => setMenu(null)} />
              <div
                role="menu"
                aria-label={`${RAIL_VIEW_LABELS[menu.id]} options`}
                style={{ left: menu.left, top: menu.top }}
                className="fixed z-[110] w-44 rounded-lg border border-surface-border bg-surface-card p-1 shadow-xl"
              >
                <button
                  role="menuitem"
                  type="button"
                  disabled={order.indexOf(menu.id) === 0}
                  onClick={() => {
                    move(menu.id, -1);
                    setMenu(null);
                  }}
                  className="w-full rounded px-2.5 py-1.5 text-left text-[11px] text-content-main transition-colors hover:bg-surface-hover disabled:cursor-not-allowed disabled:opacity-40 disabled:hover:bg-transparent"
                >
                  Move up
                </button>
                <button
                  role="menuitem"
                  type="button"
                  disabled={order.indexOf(menu.id) === order.length - 1}
                  onClick={() => {
                    move(menu.id, 1);
                    setMenu(null);
                  }}
                  className="w-full rounded px-2.5 py-1.5 text-left text-[11px] text-content-main transition-colors hover:bg-surface-hover disabled:cursor-not-allowed disabled:opacity-40 disabled:hover:bg-transparent"
                >
                  Move down
                </button>

                {/* Settings stays put: it is where hidden views are brought back. */}
                {menu.id === 'settings' ? null : (
                  <button
                    role="menuitem"
                    type="button"
                    onClick={() => {
                      toggleRailViewHidden(menu.id);
                      setMenu(null);
                    }}
                    className="w-full rounded px-2.5 py-1.5 text-left text-[11px] text-content-main transition-colors hover:bg-surface-hover"
                  >
                    Hide from rail
                  </button>
                )}

                <button
                  role="menuitem"
                  type="button"
                  onClick={() => {
                    resetRailOrder();
                    setMenu(null);
                  }}
                  className="w-full rounded px-2.5 py-1.5 text-left text-[11px] text-content-dim transition-colors hover:bg-surface-hover hover:text-content-main"
                >
                  Reset order
                </button>
              </div>
            </>,
            document.body
          )
        : null}

      {tooltip && !menu
        ? createPortal(
            <div
              style={{ top: tooltip.top + 6, left: 64 }}
              className="pointer-events-none fixed z-[60] whitespace-nowrap rounded-md border border-surface-border bg-surface-card px-2.5 py-1 text-xs text-content-main shadow-xl"
            >
              <span className="block">{tooltip.label}</span>
              <span className="block text-[10px] text-content-dim">
                {railShortcutsEnabled && railShortcuts[tooltip.id] ? `${railShortcuts[tooltip.id]} · ` : ''}
                Right-click for options · Alt+↑/↓ moves
              </span>
            </div>,
            document.body
          )
        : null}
    </nav>
  );
};
