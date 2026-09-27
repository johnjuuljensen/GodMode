import { useEffect } from 'react';
import { useAppStore } from './store';
import { Shell } from './components/Shell';

/** The app's page: the app holds each server's key, so there is nothing to sign in to. */
export default function App() {
  const loadServers = useAppStore(s => s.loadServers);

  useEffect(() => {
    loadServers().catch(console.error);
  }, [loadServers]);

  return <Shell />;
}
