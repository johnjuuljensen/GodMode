import { useState, useEffect, useCallback } from 'react';
import type { QuestionOptionData } from '../../signalr/types';
import './QuestionPrompt.css';

interface Props {
  text: string | null;
  header: string | null;
  options: QuestionOptionData[];
  onSelectOption: (label: string) => void;
  onDismiss: () => void;
}

export function QuestionPrompt({ text, header, options, onSelectOption, onDismiss }: Props) {
  const [activeIndex, setActiveIndex] = useState(0);

  // Reset active index when options change
  useEffect(() => {
    setActiveIndex(0);
  }, [options]);

  const handleKeyDown = useCallback((e: KeyboardEvent) => {
    if (options.length === 0) return;

    switch (e.key) {
      case 'ArrowUp':
        e.preventDefault();
        setActiveIndex(i => (i - 1 + options.length) % options.length);
        break;
      case 'ArrowDown':
        e.preventDefault();
        setActiveIndex(i => (i + 1) % options.length);
        break;
      case 'Enter':
        e.preventDefault();
        onSelectOption(options[activeIndex].label);
        break;
      case 'Escape':
        e.preventDefault();
        onDismiss();
        break;
      default:
        // Number keys 1-9 for quick select
        if (e.key >= '1' && e.key <= '9') {
          const idx = parseInt(e.key) - 1;
          if (idx < options.length) {
            e.preventDefault();
            onSelectOption(options[idx].label);
          }
        }
        break;
    }
  }, [options, activeIndex, onSelectOption, onDismiss]);

  useEffect(() => {
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [handleKeyDown]);

  return (
    <div className="question-prompt">
      {/* Pulsing banner */}
      <div className="question-banner">
        <span className="question-pulse">?</span>
        <span className="question-label">WAITING FOR INPUT</span>
        {header && <span className="question-header">{header}</span>}
        <button className="question-dismiss" onClick={onDismiss} title="Dismiss (Esc)">x</button>
      </div>

      {/* Question text */}
      {text && <div className="question-text">{text}</div>}

      {/* Option list */}
      {options.length > 0 && (
        <div className="question-options">
          {options.map((opt, i) => (
            <button
              key={i}
              className={`question-option ${i === activeIndex ? 'question-option-active' : ''}`}
              onClick={() => onSelectOption(opt.label)}
              onMouseEnter={() => setActiveIndex(i)}
            >
              <span className="question-option-bar" />
              <span className="question-option-num">{i + 1}</span>
              <span className="question-option-label">{opt.label}</span>
              {opt.description && (
                <span className="question-option-desc">{opt.description}</span>
              )}
              {i === activeIndex && (
                <span className="question-option-hint">Enter</span>
              )}
            </button>
          ))}
          <div className="question-keys">
            <span>Up/Down navigate</span>
            <span>Enter select</span>
            <span>1-9 quick pick</span>
            <span>Esc dismiss</span>
          </div>
        </div>
      )}
    </div>
  );
}
