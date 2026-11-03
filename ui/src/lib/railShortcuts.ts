import { ActiveView } from '../types/agent';

/**
 * Keyboard shortcuts for the rail's views, and the one place that turns a key press into a saved
 * spec. Capturing and matching share it, so a shortcut that was recorded can always be matched.
 *
 * The feature ships switched off: these defaults apply the moment it is turned on, and nothing
 * listens while it is off.
 */
export const DEFAULT_RAIL_SHORTCUTS: Record<ActiveView, string> = {
  chat: 'Ctrl+1',
  profiles: 'Ctrl+2',
  mcpskills: 'Ctrl+3',
  dashboard: 'Ctrl+4',
  repositories: 'Ctrl+5',
  settings: 'Ctrl+6',
};

const MODIFIER_ORDER = ['Ctrl', 'Alt', 'Shift', 'Meta'];

/** A key press as "Ctrl+Alt+3", or null when it cannot be a shortcut (plain key, modifier alone). */
export function shortcutFromEvent(event: KeyboardEvent): string | null {
  if (!event.key) return null;
  if (MODIFIER_ORDER.includes(event.key)) return null;

  const parts: string[] = [];
  if (event.ctrlKey) parts.push('Ctrl');
  if (event.altKey) parts.push('Alt');
  if (event.shiftKey) parts.push('Shift');
  if (event.metaKey) parts.push('Meta');

  // A bare letter would fight the composer, so a modifier is required to bind anything at all.
  if (parts.length === 0) return null;

  parts.sort((a, b) => MODIFIER_ORDER.indexOf(a) - MODIFIER_ORDER.indexOf(b));

  return [...parts, event.key.length === 1 ? event.key.toUpperCase() : event.key].join('+');
}

export function matchesShortcut(event: KeyboardEvent, shortcut: string): boolean {
  return shortcut.length > 0 && shortcutFromEvent(event) === shortcut;
}

/** The other view holding `shortcut`, or null. Used when binding so one key never means two views. */
export function conflictingView(
  shortcuts: Record<ActiveView, string>,
  view: ActiveView,
  shortcut: string
): ActiveView | null {
  if (!shortcut) return null;

  for (const [id, spec] of Object.entries(shortcuts) as [ActiveView, string][]) {
    if (id !== view && spec === shortcut) return id;
  }

  return null;
}

/**
 * Views whose binding is shared with another view.
 *
 * Binding swaps them, so this can only come from storage edited by hand — worth showing rather than
 * letting the first match win without a word.
 */
export function duplicateShortcutViews(shortcuts: Record<ActiveView, string>): Set<ActiveView> {
  const holders = new Map<string, ActiveView[]>();

  for (const [id, spec] of Object.entries(shortcuts) as [ActiveView, string][]) {
    if (!spec) continue;
    holders.set(spec, [...(holders.get(spec) ?? []), id]);
  }

  const duplicated = new Set<ActiveView>();
  for (const ids of holders.values()) {
    if (ids.length > 1) ids.forEach(id => duplicated.add(id));
  }

  return duplicated;
}
