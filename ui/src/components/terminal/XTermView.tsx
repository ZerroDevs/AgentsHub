import React, { useEffect, useRef } from 'react';
import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import { WebLinksAddon } from '@xterm/addon-web-links';
import '@xterm/xterm/css/xterm.css';
import { webviewBridge } from '../../services/webviewBridge';

interface XTermViewProps {
  cliId?: string;
  className?: string;
}

export const XTermView: React.FC<XTermViewProps> = ({ cliId = 'antigravity_cli', className = '' }) => {
  const terminalRef = useRef<HTMLDivElement>(null);
  const xtermInstance = useRef<Terminal | null>(null);
  const fitAddonRef = useRef<FitAddon | null>(null);

  useEffect(() => {
    if (!terminalRef.current) return;

    // Initialize XTerm instance
    const term = new Terminal({
      cursorBlink: true,
      fontFamily: '"JetBrains Mono", Consolas, "Courier New", monospace',
      fontSize: 13,
      theme: {
        background: '#090D16',
        foreground: '#F8FAFC',
        cursor: '#38BDF8',
        selectionBackground: 'rgba(56, 189, 248, 0.3)',
        black: '#0F172A',
        red: '#F43F5E',
        green: '#10B981',
        yellow: '#F59E0B',
        blue: '#38BDF8',
        magenta: '#A855F7',
        cyan: '#06B6D4',
        white: '#F8FAFC',
      },
    });

    const fitAddon = new FitAddon();
    const webLinksAddon = new WebLinksAddon();

    term.loadAddon(fitAddon);
    term.loadAddon(webLinksAddon);
    term.open(terminalRef.current);
    fitAddon.fit();

    xtermInstance.current = term;
    fitAddonRef.current = fitAddon;

    // Handle user input from terminal
    term.onData(data => {
      webviewBridge.invoke('send_terminal_input', {
        cli: cliId,
        data,
      }).catch(() => {
        // Fallback for direct sidecar action
        webviewBridge.invoke('terminal_input', {
          sessionId: cliId,
          data,
        }).catch(() => {});
      });
    });

    // Handle resize
    const handleResize = () => {
      try {
        fitAddon.fit();
        webviewBridge.invoke('resize_terminal', {
          cli: cliId,
          cols: term.cols,
          rows: term.rows,
        }).catch(() => {});
      } catch {}
    };

    window.addEventListener('resize', handleResize);

    // Initial Welcome message
    term.writeln('\x1b[38;2;56;189;248mAgents Hub ConPTY Terminal HUD [v' + __APP_VERSION__ + ']\x1b[0m');
    term.writeln(`\x1b[90mSession bound to: ${cliId}\x1b[0m\r\n`);

    // Listen for incoming backend terminal stream
    const unsubscribe = webviewBridge.on('terminal_data', (payload: any) => {
      if (!payload) return;
      if (!payload.cli || payload.cli.toLowerCase() === cliId.toLowerCase() || payload.cli === 'all') {
        if (payload.data) {
          term.write(payload.data);
        }
      }
    });

    return () => {
      window.removeEventListener('resize', handleResize);
      unsubscribe();
      term.dispose();
    };
  }, [cliId]);

  return (
    <div className={`w-full h-full p-2 bg-[#090D16] rounded-xl overflow-hidden ${className}`}>
      <div ref={terminalRef} className="w-full h-full" />
    </div>
  );
};
