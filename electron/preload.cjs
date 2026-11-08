/**
 * Agents Hub - Linux Desktop Window Preload
 * Provides seamless bridge polyfill for window.chrome.webview over local WebSocket IPC.
 */

const bridgeUrl = 'ws://127.0.0.1:9125/ws';
let ws = null;
const queue = [];
const messageListeners = new Set();

function connect() {
  try {
    ws = new WebSocket(bridgeUrl);
    ws.onopen = () => {
      while (queue.length > 0) {
        const item = queue.shift();
        ws.send(item);
      }
    };
    ws.onmessage = (event) => {
      for (const listener of messageListeners) {
        try {
          listener({ data: event.data });
        } catch (e) {
          console.error('[AgentsHub Preload] Listener error:', e);
        }
      }
    };
    ws.onclose = () => {
      setTimeout(connect, 1000);
    };
    ws.onerror = () => {
      try { ws.close(); } catch (_) {}
    };
  } catch (e) {
    setTimeout(connect, 1000);
  }
}

connect();

window.chrome = window.chrome || {};
window.chrome.webview = {
  postMessage: (data) => {
    const str = typeof data === 'string' ? data : JSON.stringify(data);
    if (ws && ws.readyState === WebSocket.OPEN) {
      ws.send(str);
    } else {
      queue.push(str);
    }
  },
  addEventListener: (type, listener) => {
    if (type === 'message') {
      messageListeners.add(listener);
    }
  },
  removeEventListener: (type, listener) => {
    if (type === 'message') {
      messageListeners.delete(listener);
    }
  }
};
