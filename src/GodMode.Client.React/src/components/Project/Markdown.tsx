import { memo } from 'react';
import { renderMarkdown } from './renderMarkdown';
import { copyWholeAsMarkdown } from './copyMarkdown';

export const Markdown = memo(function Markdown({ text, className }: { text: string; className?: string }) {
  return (
    <div
      className={`markdown ${className ?? ''}`}
      onCopy={e => copyWholeAsMarkdown(e, text)}
      dangerouslySetInnerHTML={{ __html: renderMarkdown(text) }}
    />
  );
});
