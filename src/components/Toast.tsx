import { useState, createContext, useContext, useCallback } from 'react';

type ToastType = 'success' | 'error' | 'info';

interface Toast {
  id: string;
  message: string;
  type: ToastType;
}

interface ToastContextType {
  showToast: (message: string, type?: ToastType) => void;
}

const ToastContext = createContext<ToastContextType>({ showToast: () => {} });

export function useToast() {
  return useContext(ToastContext);
}

export function ToastProvider({ children }: { children: React.ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([]);

  const showToast = useCallback((message: string, type: ToastType = 'info') => {
    const id = Math.random().toString(36).slice(2);
    setToasts(prev => [...prev, { id, message, type }]);
    setTimeout(() => {
      setToasts(prev => prev.filter(t => t.id !== id));
    }, 4000);
  }, []);

  return (
    <ToastContext.Provider value={{ showToast }}>
      {children}
      <div style={{
        position: 'fixed', bottom: '1rem', right: '1rem',
        display: 'flex', flexDirection: 'column', gap: '0.5rem', zIndex: 9999
      }}>
        {toasts.map(toast => (
          <div key={toast.id} style={{
            padding: '0.75rem 1rem', borderRadius: '8px', color: 'white',
            minWidth: '250px', boxShadow: '0 2px 8px rgba(0,0,0,0.2)',
            background: toast.type === 'success' ? '#4caf50' : toast.type === 'error' ? '#f44336' : '#2196f3'
          }}>
            {toast.message}
          </div>
        ))}
      </div>
    </ToastContext.Provider>
  );
}
