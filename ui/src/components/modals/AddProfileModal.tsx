import React, { useState } from 'react';
import { UserPlus, Key } from 'lucide-react';
import { actionIcons } from '../../lib/toolIcons';
import { Button } from '../common/Button';
import { webviewBridge } from '../../services/webviewBridge';
import { useProfileStore } from '../../store/useProfileStore';

interface AddProfileModalProps {
  isOpen: boolean;
  onClose: () => void;
}

export const AddProfileModal: React.FC<AddProfileModalProps> = ({ isOpen, onClose }) => {
  const [profileName, setProfileName] = useState('');
  const [email, setEmail] = useState('');
  const [provider, setProvider] = useState('Google Antigravity');
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const { loadProfiles } = useProfileStore();

  if (!isOpen) return null;

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!profileName.trim()) return;

    try {
      setIsLoading(true);
      setError(null);
      await webviewBridge.invoke('add_antigravity_profile', {
        name: profileName.trim(),
        email: email.trim() || undefined,
        provider,
      });

      await loadProfiles();
      onClose();
    } catch (err: any) {
      setError(err?.message || 'Failed to create profile');
    } finally {
      setIsLoading(false);
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/60 backdrop-blur-xs select-none">
      <div className="w-full max-w-md rounded-2xl bg-slate-900 border border-slate-700/80 shadow-2xl overflow-hidden animate-in fade-in zoom-in-95 font-sans">
        {/* Header */}
        <div className="flex items-center justify-between px-5 py-3.5 border-b border-slate-800 bg-slate-950/40">
          <div className="flex items-center space-x-2 text-content-main font-semibold text-sm">
            <UserPlus className="w-4 h-4 text-sky-400" />
            <span>Add Runner Profile</span>
          </div>
          <button
            onClick={onClose}
            className="p-1 rounded text-content-muted hover:text-content-main hover:bg-slate-800 transition"
          >
            <img src={actionIcons.close} alt="Close" className="h-4 w-4 object-contain" />
          </button>
        </div>

        {/* Body */}
        <form onSubmit={handleSubmit} className="p-5 space-y-4 text-xs">
          {error && (
            <div className="p-2.5 rounded-lg border border-rose-500/30 bg-rose-500/10 text-rose-300 text-xs">
              {error}
            </div>
          )}

          <div className="space-y-1">
            <label className="text-content-muted font-medium">Profile Name</label>
            <input
              type="text"
              required
              placeholder="e.g. Work, Personal, Dev-Account"
              value={profileName}
              onChange={e => setProfileName(e.target.value)}
              className="w-full px-3 py-2 rounded-lg bg-slate-950 border border-slate-700 text-content-main placeholder-slate-500 focus:outline-none focus:border-sky-500"
            />
          </div>

          <div className="space-y-1">
            <label className="text-content-muted font-medium">Account Email (Optional)</label>
            <input
              type="email"
              placeholder="developer@example.com"
              value={email}
              onChange={e => setEmail(e.target.value)}
              className="w-full px-3 py-2 rounded-lg bg-slate-950 border border-slate-700 text-content-main placeholder-slate-500 focus:outline-none focus:border-sky-500"
            />
          </div>

          <div className="space-y-1">
            <label className="text-content-muted font-medium">Provider</label>
            <select
              value={provider}
              onChange={e => setProvider(e.target.value)}
              className="w-full px-3 py-2 rounded-lg bg-slate-950 border border-slate-700 text-content-main focus:outline-none focus:border-sky-500"
            >
              <option value="Google Antigravity">Google Antigravity</option>
              <option value="Claude Code">Claude Code</option>
              <option value="OpenRouter">OpenRouter</option>
            </select>
          </div>

          <div className="flex justify-end space-x-2 pt-3 border-t border-slate-800">
            <Button variant="ghost" onClick={onClose} type="button">
              Cancel
            </Button>
            <Button variant="primary" type="submit" isLoading={isLoading}>
              Create Profile
            </Button>
          </div>
        </form>
      </div>
    </div>
  );
};
