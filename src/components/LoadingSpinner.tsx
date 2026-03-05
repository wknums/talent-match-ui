export function LoadingSpinner({ size = 24 }: { size?: number }) {
  return (
    <div style={{
      display: 'inline-block', width: size, height: size,
      border: '3px solid #e0e0e0', borderTop: '3px solid #2196f3',
      borderRadius: '50%', animation: 'spin 0.8s linear infinite'
    }}>
      <style>{`@keyframes spin { to { transform: rotate(360deg); } }`}</style>
    </div>
  );
}

export function LoadingOverlay({ message = 'Loading...' }: { message?: string }) {
  return (
    <div style={{
      display: 'flex', flexDirection: 'column', alignItems: 'center',
      justifyContent: 'center', padding: '2rem', gap: '1rem'
    }}>
      <LoadingSpinner size={32} />
      <p style={{ color: '#666' }}>{message}</p>
    </div>
  );
}
