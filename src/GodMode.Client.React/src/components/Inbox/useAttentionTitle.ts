import { useEffect } from 'react';
import { useAppStore } from '../../store';

/** Puts how many projects need the user in the document title: "(3) GodMode". */
export function useAttentionTitle() {
  const count = useAppStore(s => s.attention.length);
  useEffect(() => {
    const base = document.title.replace(/^\(\d+\) /, '');
    document.title = count > 0 ? `(${count}) ${base}` : base;
  }, [count]);
}
