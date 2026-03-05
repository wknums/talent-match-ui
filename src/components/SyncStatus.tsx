import { useState, useEffect } from 'react';

type SyncState = 'synced' | 'syncing' | 'error' | 'offline';

export function useSyncStatus() {
  const [state, setState] = useState<SyncState>('synced');
  const [lastSynced, setLastSynced] = useState<Date>(new Date());

  const setSyncing = () => setState('syncing');
  const setSynced = () => { setState('synced'); setLastSynced(new Date()); };
  const setError = () => setState('error');

  useEffect(() => {
    const handleOnline = () => { if (state === 'offline') setState('synced'); };
    const handleOffline = () => setState('offline');
    window.addEventListener('online', handleOnline);
    window.addEventListener('offline', handleOffline);
    if (!navigator.onLine) setState('offline');
    return () => {
      window.removeEventListener('online', handleOnline);
      window.removeEventListener('offline', handleOffline);
    };
  }, [state]);

  return { state, lastSynced, setSyncing, setSynced, setError };
}

export function SyncStatusIndicator({ state, lastSynced }: { state: SyncState; lastSynced: Date }) {
  const config = {
    synced: { color: '#4caf50', label: 'Synced' },
    syncing: { color: '#ff9800', label: 'Syncing...' },
    error: { color: '#f44336', label: 'Sync Error' },
    offline: { color: '#999', label: 'Offline' },
  }[state];

  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: '0.5rem', fontSize: '0.75rem', color: '#666' }}>
      <div style={{ width: 8, height: 8, borderRadius: '50%', background: config.color }} />
      <span>{config.label}</span>
      {state === 'synced' && <span>· {lastSynced.toLocaleTimeString()}</span>}
    </div>
  );
}
