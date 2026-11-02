import React, { useEffect, useState } from 'react';
import { Header } from './components/header/Header';
import { PrimaryRail } from './components/layout/PrimaryRail';
import { SecondarySidebar } from './components/layout/SecondarySidebar';
import { AgentChatView } from './views/AgentChatView';
import { DashboardView } from './views/DashboardView';
import { RepositoriesView } from './views/RepositoriesView';
import { ProfilesView } from './views/ProfilesView';
import { McpSkillsView } from './views/McpSkillsView';
import { SettingsView } from './views/SettingsView';
import { ModalRoot, ModalType } from './components/modals/ModalRoot';
import { UserGuideModal } from './components/guide/UserGuideModal';
import { useGuideStore } from './store/useGuideStore';
import { useLanStore } from './store/useLanStore';
import { useSidecarStore } from './store/useSidecarStore';
import { useProfileStore } from './store/useProfileStore';
import { useWorkspaceStore } from './store/useWorkspaceStore';
import { useThemeStore } from './store/useThemeStore';
import { useSettingsStore } from './store/useSettingsStore';
import { webviewBridge } from './services/webviewBridge';
import { installUiLogging } from './services/uiLogger';

export const App: React.FC = () => {
  const { activeView, isSidebarCollapsed, isRailCollapsed, initBridgeListeners } = useSidecarStore();
  const { loadProfiles } = useProfileStore();
  const { loadWorkspaces, loadWorkspaceFiles, loadBackendSessions } = useWorkspaceStore();
  const { initTheme } = useThemeStore();

  const [activeModal, setActiveModal] = useState<ModalType>(null);

  // F1 has to work from every view, so the listener lives on the window here rather than inside a
  // single page — that is why the old guide only opened while Settings was open.
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key !== 'F1') return;
      event.preventDefault();
      const guide = useGuideStore.getState();
      if (guide.isOpen) guide.close();
      else guide.open();
    };

    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  useEffect(() => {
    // 0. Initialize theme variables and contrast
    initTheme();

    // 0b. Live Logs. The host reports what it does; the front-end capture covers throws, rejected
    // promises and console errors that never leave the WebView. Both feed the same list.
    const detachUiLogging = installUiLogging();
    const detachHostLogs = webviewBridge.on('live_log_entry', (payload: any) => {
      if (!payload) return;
      const level = payload.level === 'error' || payload.level === 'warn' ? payload.level : 'info';
      useSettingsStore.getState().addLogEntry({
        timestamp: `[${payload.timestamp ?? new Date().toTimeString().slice(0, 8)}]`,
        level,
        source: payload.source ?? payload.tag ?? 'Host',
        message: payload.message ?? payload.text ?? '',
      });
    });

    // 1. Initialize bi-directional backend event subscriptions
    const cleanup = initBridgeListeners();

    // 2. Hydrate the stored preferences first: every other view reads them, and a save that runs
    // before this lands would write this page's defaults over them.
    void useSettingsStore.getState().loadGeneralSettings();

    // 2b. Hydrate profiles, workspaces, and past session history from C# host
    loadProfiles();
    loadWorkspaces().then(() => {
      loadWorkspaceFiles();
      loadBackendSessions();
    });

    // 3. LAN state is shared by Settings and the Dashboard, which hides its LAN bits while off.
    const detachLan = useLanStore.getState().attachListeners();
    void useLanStore.getState().load();

    // 4. A fresh install gets the guide once; it marks itself seen when closed.
    useGuideStore.getState().openOnFirstRun();

    return () => {
      cleanup();
      detachLan();
      detachHostLogs();
      detachUiLogging();
    };
  }, [initBridgeListeners, loadProfiles, loadWorkspaces, loadWorkspaceFiles, loadBackendSessions]);

  const renderActiveView = () => {
    switch (activeView) {
      case 'chat':
        return <AgentChatView onOpenGitModal={() => setActiveModal('git_diff')} />;
      case 'dashboard':
        return <DashboardView />;
      case 'repositories':
        return <RepositoriesView />;
      case 'profiles':
        return <ProfilesView onOpenAddProfile={() => setActiveModal('add_profile')} />;
      case 'mcpskills':
        return <McpSkillsView />;
      case 'settings':
        return <SettingsView />;
      default:
        return <AgentChatView onOpenGitModal={() => setActiveModal('git_diff')} />;
    }
  };

  return (
    <div className="flex flex-col h-screen w-screen overflow-hidden bg-surface-deep text-content-main font-sans antialiased select-none">
      {/* 1. TOP HEADER (Workspace context, branch badge, telemetry, sidecar status, HUD toggle) */}
      <Header
        onOpenSettings={() => setActiveModal('settings')}
        onOpenWorkspaceModal={() => setActiveModal('workspace')}
      />

      {/* 2. DUAL-SIDEBAR WORKSPACE CONTAINER */}
      <div id="app-container" className="flex-1 flex overflow-hidden relative">
        {/* Left-most Primary Rail (56px). Same collapse treatment as the panel beside it, with its
            own state: navigation and content are hidden for different reasons. */}
        <div
          className={`relative flex flex-shrink-0 overflow-hidden transition-[width] duration-200 ease-out ${
            isRailCollapsed ? 'w-0' : 'w-14'
          }`}
        >
          <PrimaryRail />
        </div>

        {/* Secondary sidebar: kept mounted and animated, so collapsing gives the chat its width
            back without losing the list's scroll position or its filters. Both panels are reopened
            from their own toggle (header for the rail, chat header for this one) rather than from a
            strip at the seam, which read as a stray line. */}
        {activeView === 'chat' ? (
          <div
            className={`relative flex flex-shrink-0 overflow-hidden transition-[width] duration-200 ease-out ${
              isSidebarCollapsed ? 'w-0' : 'w-64'
            }`}
          >
            <SecondarySidebar onOpenWorkspaceModal={() => setActiveModal('workspace')} />
          </div>
        ) : null}

        {/* Main Workspace Content Area */}
        <main id="main-area" className="flex-1 flex flex-col bg-surface-deep overflow-hidden relative min-w-0">
          {renderActiveView()}
        </main>
      </div>

      {/* 3. MODAL REGISTRY (Workspace Settings, Add Profile, Git Diff, MCP Skills) */}
      <ModalRoot activeModal={activeModal} onClose={() => setActiveModal(null)} />

      {/* 4. USER GUIDE (first run, F1, Settings → About) */}
      <UserGuideModal />
    </div>
  );
};

export default App;
