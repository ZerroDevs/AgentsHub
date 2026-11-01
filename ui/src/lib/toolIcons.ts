// Icons cut from the sticker sheets in assets/AgAssets by tools/extract-icons.py,
// tools/convert-new.py and tools/convert-new2.py.
//
// Imported rather than referenced by URL: the UI is served from a file-backed virtual host
// with base './', so an absolute /icons/... path does not resolve there.
import bolt from '../assets/icons/tools/bolt.png';
import braces from '../assets/icons/tools/braces.png';
import document from '../assets/icons/tools/document.png';
import folder from '../assets/icons/tools/folder.png';
import gear from '../assets/icons/tools/gear.png';
import genericTool from '../assets/icons/tools/generic.png';
import terminal from '../assets/icons/tools/terminal.png';
import wifi from '../assets/icons/tools/wifi.png';
import wifiOff from '../assets/icons/tools/wifi-off.png';
import brain from '../assets/icons/ai/brain.png';
import chat from '../assets/icons/ai/chat.png';
import chip from '../assets/icons/ai/chip.png';
import openaiKnot from '../assets/icons/ai/openai-knot.png';
import robot from '../assets/icons/ai/robot.png';
import sparkle from '../assets/icons/ai/sparkle.png';
import antigravity from '../assets/icons/engines/antigravity.png';
import claude from '../assets/icons/engines/claude.png';
import cline from '../assets/icons/engines/cline.png';
import deepseek from '../assets/icons/engines/deepseek.png';
import freebuff from '../assets/icons/engines/freebuff.png';
import opencode from '../assets/icons/engines/opencode.png';
import qwen from '../assets/icons/engines/qwen.png';
import vyce from '../assets/icons/engines/vyce.png';
import about from '../assets/icons/sections/about.png';
import appearance from '../assets/icons/sections/appearance.png';
import devtools from '../assets/icons/sections/devtools.png';
import general from '../assets/icons/sections/general.png';
import integrations from '../assets/icons/sections/integrations.png';
import lan from '../assets/icons/sections/lan.png';
import logs from '../assets/icons/sections/logs.png';
import security from '../assets/icons/sections/security.png';
import actionAdd from '../assets/icons/actions/add.png';
import actionClose from '../assets/icons/actions/close.png';
import actionCopy from '../assets/icons/actions/copy.png';
import actionDownload from '../assets/icons/actions/download.png';
import actionExternal from '../assets/icons/actions/external.png';
import actionKey from '../assets/icons/actions/key.png';
import actionLock from '../assets/icons/actions/lock.png';
import actionPencil from '../assets/icons/actions/pencil.png';
import actionRefresh from '../assets/icons/actions/refresh.png';
import actionSearch from '../assets/icons/actions/search.png';
import actionTrash from '../assets/icons/actions/trash.png';
import stateOffline from '../assets/icons/states/offline.png';
import stateOk from '../assets/icons/states/ok.png';
import stateRunning from '../assets/icons/states/running.png';
import stateWarn from '../assets/icons/states/warn.png';
import objectBrowser from '../assets/icons/objects/browser.png';
import objectDiscord from '../assets/icons/objects/discord.png';
import objectFileCode from '../assets/icons/objects/file-code.png';
import objectFileJson from '../assets/icons/objects/file-json.png';
import objectFileText from '../assets/icons/objects/file-text.png';
import objectMcp from '../assets/icons/objects/mcp.png';
import objectModel from '../assets/icons/objects/model.png';
import objectPackage from '../assets/icons/objects/package.png';
import objectPort from '../assets/icons/objects/port.png';
import objectProxy from '../assets/icons/objects/proxy.png';
import objectQuota from '../assets/icons/objects/quota.png';
import objectScreenshot from '../assets/icons/objects/screenshot.png';
import objectSession from '../assets/icons/objects/session.png';
import objectWebhook from '../assets/icons/objects/webhook.png';
import objectWorkspace from '../assets/icons/objects/workspace.png';
import catConfused from '../assets/icons/mascot/cat-confused.png';
import catTerminal from '../assets/icons/mascot/cat-terminal.png';
import logo from '../assets/icons/brand/logo.png';
import logoWhite from '../assets/icons/brand/logo-white.png';

/** Tools the host seeds itself. */
const BY_ID: Record<string, string> = {
  freebuff,
  cline,
  opencode,
  antigravity_ide: antigravity,
  antigravity_app: antigravity,
  claude,
  chatgpt: openaiKnot,
};

/**
 * Matched on the words people actually put in a tool name, so a tool added by hand
 * ("Cursor", "Warp", "GitHub Desktop") still gets something better than the fallback.
 */
const BY_WORD: Array<[RegExp, string]> = [
  [/claude|anthropic/i, claude],
  [/chatgpt|openai|gpt/i, openaiKnot],
  [/freebuff/i, freebuff],
  [/opencode/i, opencode],
  [/cline/i, cline],
  [/antigravity/i, antigravity],
  [/code|cursor|vscode|studio|zed|warp/i, braces],
  [/git/i, chip],
  [/term|shell|console|cmd|powershell/i, terminal],
  [/folder|explorer|files/i, folder],
  [/doc|note|readme/i, document],
];

export function devToolIcon(id?: string, name?: string): string {
  const key = (id ?? '').toLowerCase();
  if (BY_ID[key]) return BY_ID[key];

  const haystack = `${key} ${name ?? ''}`;
  for (const [pattern, icon] of BY_WORD) {
    if (pattern.test(haystack)) return icon;
  }
  return genericTool;
}

/** Engines offered in the new-conversation picker. */
export const engineIcons = {
  agy: antigravity,
  deepseek,
  qwen,
  /** GLM has no sticker of its own in the sheets; the chip artwork is the closest honest match. */
  zai: chip,
  vyce,
  opencode,
} as const;

/** Settings sections, keyed by the category id used in SettingsView. */
export const sectionIcons: Record<string, string> = {
  appearance,
  general,
  engines: chip,
  integrations,
  devtools,
  lan,
  security,
  logs,
  about,
};

export const actionIcons = {
  add: actionAdd,
  close: actionClose,
  copy: actionCopy,
  download: actionDownload,
  external: actionExternal,
  key: actionKey,
  lock: actionLock,
  pencil: actionPencil,
  refresh: actionRefresh,
  search: actionSearch,
  trash: actionTrash,
};

export const stateIcons = {
  offline: stateOffline,
  ok: stateOk,
  running: stateRunning,
  warn: stateWarn,
};

export const objectIcons = {
  browser: objectBrowser,
  discord: objectDiscord,
  fileCode: objectFileCode,
  fileJson: objectFileJson,
  fileText: objectFileText,
  mcp: objectMcp,
  model: objectModel,
  package: objectPackage,
  port: objectPort,
  proxy: objectProxy,
  quota: objectQuota,
  screenshot: objectScreenshot,
  session: objectSession,
  webhook: objectWebhook,
  workspace: objectWorkspace,
};

export const mascot = {
  confused: catConfused,
  terminal: catTerminal,
};

/** Wired and unwired marks for the Remote Access header. */
export const lanIcons = { on: wifi, off: wifiOff };

/** The app mark, cut from assets/AgAssets/logo.png. */
export const brandLogo = logo;

/**
 * The same mark as a flat white silhouette. The blue tile needs a light surface, so anything
 * that draws the logo 16 px high on a dark title bar (tray, installer header) uses this one.
 */
export const brandLogoWhite = logoWhite;

/** Sticker glyphs for headers and tiles, where there is room for a drawn icon. */
export const sticker = {
  terminal,
  braces,
  document,
  folder,
  gear,
  bolt,
  brain,
  chat,
  chip,
  openaiKnot,
  robot,
  sparkle,
  genericTool,
};
