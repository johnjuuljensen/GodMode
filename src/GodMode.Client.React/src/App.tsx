import { useEffect } from 'react';
import { useAppStore } from './store';
import { Shell } from './components/Shell';

export default function App() {
  const loadServers = useAppStore(s => s.loadServers);

  useEffect(() => {
    loadServers();
  }, [loadServers]);

  return <Shell />;
}
