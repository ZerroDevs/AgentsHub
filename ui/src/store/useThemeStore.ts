/**
 * Theme Manager & Syncing Store
 * Manages Dark/Light mode, the accent palette, colour intensity, wallpaper, and host syncing.
 */

import { create } from 'zustand';
import { webviewBridge } from '../services/webviewBridge';

export type ThemeMode = 'dark' | 'light';

export interface ThemeStoreState {
  theme: ThemeMode;
  accentColor: string;
  themeIntensity: number;
  /** Wallpaper URL, empty when none is set. */
  backgroundImage: string;
  /** Scrim strength over the wallpaper, 0-90 percent. */
  backgroundDim: number;

  // Actions
  setTheme: (theme: ThemeMode) => void;
  setAccentColor: (hex: string) => void;
  setThemeIntensity: (intensity: number) => void;
  setBackgroundImage: (url: string) => void;
  setBackgroundDim: (dim: number) => void;
  applyTheme: (theme?: ThemeMode, hex?: string, intensity?: number) => void;
  initTheme: () => void;
}

export const PRESET_SWATCHES = [
  { hex: '#4049B9', label: 'Discord Indigo' },
  { hex: '#5865F2', label: 'Discord Blurple' },
  { hex: '#10B981', label: 'Emerald Matrix' },
  { hex: '#06B6D4', label: 'Cyan Tech' },
  { hex: '#38BDF8', label: 'Skyline Blue' },
  { hex: '#F59E0B', label: 'Amber Cyber' },
  { hex: '#EC4899', label: 'Hot Pink' },
  { hex: '#8B5CF6', label: 'Deep Violet' },
];

function hexToHsl(hex: string): [number, number, number] {
  let c = hex.replace('#', '');
  if (c.length === 3) c = c.split('').map(x => x + x).join('');
  const num = parseInt(c, 16);
  const r = (num >> 16) / 255;
  const g = ((num >> 8) & 255) / 255;
  const b = (num & 255) / 255;

  const max = Math.max(r, g, b);
  const min = Math.min(r, g, b);
  let h = 0;
  let s = 0;
  const l = (max + min) / 2;

  if (max !== min) {
    const d = max - min;
    s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
    switch (max) {
      case r: h = ((g - b) / d + (g < b ? 6 : 0)) / 6; break;
      case g: h = ((b - r) / d + 2) / 6; break;
      case b: h = ((r - g) / d + 4) / 6; break;
    }
  }

  return [Math.round(h * 360), Math.round(s * 100), Math.round(l * 100)];
}

/** HSL as a space-separated `r g b` triplet, the form Tailwind's `<alpha-value>` expects. */
function hslToRgbParts(h: number, s: number, l: number): string {
  const sat = Math.min(Math.max(s, 0), 100) / 100;
  const light = Math.min(Math.max(l, 0), 100) / 100;
  const c = (1 - Math.abs(2 * light - 1)) * sat;
  const x = c * (1 - Math.abs(((h / 60) % 2) - 1));
  const m = light - c / 2;

  const [r, g, b] =
    h < 60 ? [c, x, 0] :
    h < 120 ? [x, c, 0] :
    h < 180 ? [0, c, x] :
    h < 240 ? [0, x, c] :
    h < 300 ? [x, 0, c] :
    [c, 0, x];

  const to255 = (v: number) => Math.round((v + m) * 255);
  return `${to255(r)} ${to255(g)} ${to255(b)}`;
}

/** `#RRGGBB` as an rgba() string, for accent tints that must stay translucent. */
function hexToRgba(hex: string, alpha: number): string {
  let c = hex.replace('#', '');
  if (c.length === 3) c = c.split('').map(x => x + x).join('');
  const num = parseInt(c, 16);
  const r = (num >> 16) & 255;
  const g = (num >> 8) & 255;
  const b = num & 255;
  return `rgba(${r}, ${g}, ${b}, ${alpha})`;
}

function readNumber(key: string, fallback: number): number {
  try {
    const val = parseInt(localStorage.getItem(key) || '', 10);
    return isNaN(val) ? fallback : val;
  } catch {
    return fallback;
  }
}

function readString(key: string, fallback: string): string {
  try {
    return localStorage.getItem(key) || fallback;
  } catch {
    return fallback;
  }
}

export const useThemeStore = create<ThemeStoreState>((set, get) => ({
  theme: (readString('agents_hub_theme', 'dark') === 'light' ? 'light' : 'dark'),
  accentColor: readString('agents_hub_accent_color', '#4049B9'),
  themeIntensity: readNumber('agents_hub_theme_intensity', 50),
  backgroundImage: readString('agents_hub_theme_bg', ''),
  backgroundDim: readNumber('agents_hub_theme_bg_dim', 45),

  applyTheme: (themeOverride, hexOverride, intensityOverride) => {
    const theme = themeOverride || get().theme;
    const hex = (hexOverride || get().accentColor).toUpperCase();
    const intensity = intensityOverride !== undefined ? intensityOverride : get().themeIntensity;
    const wallpaper = get().backgroundImage;
    const dim = get().backgroundDim;
    const hasWallpaper = wallpaper.length > 0;

    const [h, s] = hexToHsl(hex);
    const root = document.documentElement;

    root.dataset.theme = theme;
    document.body.dataset.theme = theme;
    document.body.dataset.themeBg = hasWallpaper ? 'image' : 'none';

    /**
     * Surfaces are opaque without a wallpaper. With one, how much of the photo they hide is the dim
     * slider's job: at 0% the panels are thin veils and the picture is plainly there, and at 90%
     * they are nearly solid so text stays readable over a busy image.
     *
     * The alphas used to be fixed constants, which is why 0% still looked like a faint photo behind
     * opaque cards — the scrim was the only thing the slider touched, and the panels did the hiding.
     */
    /** Keeps a tinted hue visible on surfaces and stops an extreme accent from flooding them. */
    const satFloor = (value: number) => Math.min(Math.max(Math.round(value), 4), 90);

    const veil = Math.min(Math.max(dim, 0), 95) / 100;

    /** From a see-through floor at 0% to nearly solid at the top of the slider. */
    const alphaFor = (floor: number) =>
      Math.round((floor + (0.96 - floor) * (veil / 0.95)) * 1000) / 1000;

    const surface = (name: string, lightness: number, saturation: number, alpha: number) => {
      const color = hasWallpaper
        ? `hsla(${h}, ${satFloor(saturation)}%, ${lightness}%, ${alphaFor(alpha)})`
        : `hsl(${h}, ${satFloor(saturation)}%, ${lightness}%)`;
      root.style.setProperty(name, color);
    };

    if (theme === 'dark') {
      root.classList.add('dark');
      root.classList.remove('light');

      const sTint = Math.round((s * (intensity / 100)) * 0.45);
      surface('--bg-app', 7, Math.max(sTint, 8), 0.14);
      surface('--bg-sidebar', 7.5, Math.round(sTint * 1.2), 0.3);
      surface('--bg-header', 5.5, sTint, 0.22);
      surface('--bg-card', 9, Math.round(sTint * 1.15), 0.46);
      surface('--bg-card-hover', 11.5, Math.round(sTint * 1.3), 0.56);
      // Inputs keep more body: a field you type into has to stay readable whatever is behind it.
      surface('--bg-input', 8, sTint, 0.78);
      surface('--bg-terminal', 3.5, sTint, 0.88);
      surface('--bg-code', 3.5, sTint, 0.88);
      surface('--bg-code-header', 5.5, sTint, 0.92);

      root.style.setProperty('--border-subtle', hasWallpaper
        ? `hsla(${h}, 20%, 70%, 0.18)`
        : `hsl(${h}, ${Math.max(Math.round(sTint * 1.1), 10)}%, 14%)`);
      root.style.setProperty('--border-light', hasWallpaper ? `hsla(${h}, 18%, 72%, 0.26)` : `hsl(${h}, ${Math.max(Math.round(sTint * 1.4), 12)}%, 19%)`);
      root.style.setProperty('--border-code', hasWallpaper ? `hsla(${h}, 20%, 70%, 0.18)` : `hsl(${h}, ${Math.max(sTint, 8)}%, 14%)`);

      root.style.setProperty('--text-main', '#F1F5F9');
      root.style.setProperty('--text-muted', '#B6C2D4');
      root.style.setProperty('--text-dim', '#8494A8');
      root.style.setProperty('--text-inverse', '#FFFFFF');
      root.style.setProperty('--theme-scrim', `rgba(0, 0, 0, ${dim / 100})`);
      root.style.setProperty('--syn-comment', '#8494A8');
      root.style.setProperty('--syn-punctuation', '#B6C2D4');
    } else {
      root.classList.remove('dark');
      root.classList.add('light');

      const sTintLight = Math.round((s * (intensity / 100)) * 0.35);
      surface('--bg-app', 99, sTintLight, 0.12);
      surface('--bg-sidebar', 97, Math.round(sTintLight * 1.35), 0.26);
      surface('--bg-header', 94, Math.round(sTintLight * 1.5), 0.2);
      surface('--bg-card', 100, sTintLight, 0.42);
      surface('--bg-card-hover', 98, sTintLight, 0.52);
      surface('--bg-input', 98, sTintLight, 0.78);
      surface('--bg-terminal', 97, sTintLight, 0.86);
      surface('--bg-code', 97, sTintLight, 0.86);
      surface('--bg-code-header', 94, sTintLight, 0.9);

      root.style.setProperty('--border-subtle', hasWallpaper ? `hsla(${h}, 25%, 45%, 0.2)` : `hsl(${h}, ${Math.round(sTintLight * 1.25)}%, 84%)`);
      root.style.setProperty('--border-light', hasWallpaper ? `hsla(${h}, 22%, 40%, 0.28)` : `hsl(${h}, ${Math.round(sTintLight * 1.4)}%, 76%)`);
      root.style.setProperty('--border-code', hasWallpaper ? `hsla(${h}, 25%, 45%, 0.2)` : `hsl(${h}, ${Math.round(sTintLight * 1.2)}%, 86%)`);

      root.style.setProperty('--text-main', '#0F172A');
      root.style.setProperty('--text-muted', '#3F4C60');
      root.style.setProperty('--text-dim', '#5A6678');
      root.style.setProperty('--text-inverse', '#FFFFFF');
      root.style.setProperty('--theme-scrim', `rgba(255, 255, 255, ${dim / 100})`);
      root.style.setProperty('--syn-comment', '#5A6678');
      root.style.setProperty('--syn-punctuation', '#3F4C60');
    }

    // Accent family: these are what buttons, focus rings, chips, and code chrome read.
    root.style.setProperty('--accent-blue', hex);
    root.style.setProperty('--accent-blue-hover', `hsl(${h}, ${Math.min(Math.max(s, 45), 85)}%, ${theme === 'dark' ? 38 : 42}%)`);
    root.style.setProperty('--accent-sky', hex);
    root.style.setProperty('--accent-cyan', hex);
    root.style.setProperty('--accent-sky-bg', hexToRgba(hex, theme === 'dark' ? 0.16 : 0.12));
    root.style.setProperty('--border-focus', hex);
    root.style.setProperty('--border-hover', hex);

    // The sky scale every component uses for selection and focus, derived from the same hue.
    const step = (lightness: number) => hslToRgbParts(h, Math.min(Math.max(s, 45), 90), lightness);
    const isDark = theme === 'dark';
    root.style.setProperty('--accent-300-rgb', step(isDark ? 74 : 58));
    root.style.setProperty('--accent-400-rgb', step(isDark ? 68 : 50));
    root.style.setProperty('--accent-500-rgb', step(isDark ? 58 : 42));
    root.style.setProperty('--accent-600-rgb', step(isDark ? 50 : 36));
    root.style.setProperty('--accent-700-rgb', step(isDark ? 44 : 30));

    // The accent is also used as text. The same hue that reads well on a dark surface is too light
    // on a light one, so the ink variant is derived per theme instead of being reused verbatim.
    const inkLightness = theme === 'dark' ? 68 : 34;
    const inkSaturation = Math.max(s, theme === 'dark' ? 60 : 55);
    root.style.setProperty('--accent-ink', `hsl(${h}, ${inkSaturation}%, ${inkLightness}%)`);

    root.style.setProperty('--theme-image', hasWallpaper ? `url("${wallpaper}")` : 'none');

    try {
      localStorage.setItem('agents_hub_theme', theme);
      localStorage.setItem('agents_hub_accent_color', hex);
      localStorage.setItem('agents_hub_theme_intensity', String(intensity));
      localStorage.setItem('agents_hub_theme_bg', wallpaper);
      localStorage.setItem('agents_hub_theme_bg_dim', String(dim));
    } catch {}

    set({ theme, accentColor: hex, themeIntensity: intensity });
  },

  setTheme: (newTheme: ThemeMode) => {
    get().applyTheme(newTheme, get().accentColor, get().themeIntensity);
    try {
      webviewBridge.invoke('save_settings', {
        settings: { theme: newTheme, accentColor: get().accentColor },
      });
    } catch {}
  },

  setAccentColor: (newHex: string) => {
    get().applyTheme(get().theme, newHex, get().themeIntensity);
    try {
      webviewBridge.invoke('save_settings', {
        settings: { theme: get().theme, accentColor: newHex },
      });
    } catch {}
  },

  setThemeIntensity: (newIntensity: number) => {
    get().applyTheme(get().theme, get().accentColor, newIntensity);
  },

  setBackgroundImage: (url: string) => {
    set({ backgroundImage: url });
    get().applyTheme();
  },

  setBackgroundDim: (dim: number) => {
    set({ backgroundDim: dim });
    get().applyTheme();
  },

  initTheme: () => {
    get().applyTheme(get().theme, get().accentColor, get().themeIntensity);
  },
}));
