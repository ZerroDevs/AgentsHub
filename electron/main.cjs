/**
 * Agents Hub - Native Linux Desktop Application Window
 */

const { app, BrowserWindow, shell } = require('electron');
const path = require('node:path');

const TARGET_URL = process.env.AGENTSHUB_URL || 'http://127.0.0.1:9125/index.html';

function createWindow() {
  const iconPath = path.join(__dirname, '..', 'assets', 'app.png');

  const win = new BrowserWindow({
    width: 1280,
    height: 820,
    minWidth: 960,
    minHeight: 640,
    title: 'Agents Hub',
    icon: iconPath,
    backgroundColor: '#080a0f',
    autoHideMenuBar: true,
    show: false,
    webPreferences: {
      preload: path.join(__dirname, 'preload.cjs'),
      contextIsolation: false,
      nodeIntegration: false,
      webSecurity: false
    }
  });

  win.once('ready-to-show', () => {
    win.show();
  });

  // Intercept all new window requests and open them in the external browser (INV-1)
  win.webContents.setWindowOpenHandler(({ url }) => {
    if (url.startsWith('http:') || url.startsWith('https:') || url.startsWith('mailto:')) {
      shell.openExternal(url);
    }
    return { action: 'deny' };
  });

  win.webContents.on('will-navigate', (event, url) => {
    if (!url.startsWith('http://127.0.0.1:9125') && !url.startsWith('http://localhost:9125')) {
      event.preventDefault();
      shell.openExternal(url);
    }
  });

  win.loadURL(TARGET_URL);

  win.on('closed', () => {
    app.quit();
  });
}

app.whenReady().then(() => {
  createWindow();

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createWindow();
  });
});

app.on('window-all-closed', () => {
  app.quit();
});
