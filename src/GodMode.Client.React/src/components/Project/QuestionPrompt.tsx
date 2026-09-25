import { useState, useEffect, useCallback, useRef } from 'react';
import type { QuestionOption } from '../../signalr/types';
import { getOpenConfirm } from '../../confirmDialog';
import './QuestionPrompt.css';

interface Props {
  text: string | null;
  header: string | null;
  options: QuestionOption[];
  onSelectOption: (label: string) => void;
  onDismiss: () => void;
}

/**
 * Whether a key is the prompt's: pressed inside it, or with nothing focused, and no dialog open. On any
 * other control it is that control's: a dialog's Cancel, the inbox's Send, a text field (#170, #240).
 */
function isForPrompt(prompt: HTMLElement | null, target: EventTarget | null): boolean {
  if (getOpenConfirm() || !prompt || !(target instanceof Node)) return false;
  return target === document.body || prompt.contains(target);
}

/** A button of the prompt's own other than an option (the dismiss button): Enter there is its click. */
const isOtherButton = (target: EventTarget | null) =>
  target instanceof HTMLButtonElement && !target.classList.contains('question-option');

export function QuestionPrompt({ text, header, options, onSelectOption, onDismiss }: Props) {
  const [activeIndex, setActiveIndex] = useState(0);
  const promptRef = useRef<HTMLDivElement>(null);

  // Reset active index when options change
  useEffect(() => {
    setActiveIndex(0);
  }, [options]);

  const handleKeyDown = useCallback((e: KeyboardEvent) => {
    // One key press is one action: not one another handler took, and not one meant for another control
    if (options.length === 0 || e.defaultPrevented || !isForPrompt(promptRef.current, e.target)) return;

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
        if (isOtherButton(e.target)) return;
        e.preventDefault();
        onSelectOption(options[activeIndex].Label);
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
            onSelectOption(options[idx].Label);
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
    <div className="question-prompt" ref={promptRef}>
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
              onClick={() => onSelectOption(opt.Label)}
              onMouseEnter={() => setActiveIndex(i)}
              onFocus={() => setActiveIndex(i)}
            >
              <span className="question-option-bar" />
              <span className="question-option-num">{i + 1}</span>
              <span className="question-option-label">{opt.Label}</span>
              {opt.Description && (
                <span className="question-option-desc">{opt.Description}</span>
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
