import { memo } from 'react';
import { renderMarkdown } from './renderMarkdown';

export const Markdown = memo(function Markdown({ text, className }: { text: string; className?: string }) {
  return <div className={`markdown ${className ?? ''}`} dangerouslySetInnerHTML={{ __html: renderMarkdown(text) }} />;
});
