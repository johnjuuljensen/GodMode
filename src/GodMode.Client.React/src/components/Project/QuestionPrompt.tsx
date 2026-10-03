import { useState, useEffect, useCallback, useRef } from 'react';
import type { QuestionItem } from '../../signalr/types';
import { getOpenConfirm } from '../../confirmDialog';
import './QuestionPrompt.css';

interface Props {
  /** The request's questions, every one shown in full (#454). The first one not answered takes the keys. */
  questions: QuestionItem[];
  /** The answers chosen so far, by question text: one may still be changed until the last is chosen. */
  answered?: Readonly<Record<string, string>>;
  /** One question's answer: the chosen label, or a multi-select's labels joined by ", ". */
  onAnswer: (question: string, answer: string) => void;
  onDismiss: () => void;
}

const NO_OPTIONS: QuestionItem['Options'] = [];

/** A multi-select's answer: its chosen labels, in the order offered, joined as the hub takes them. */
const joinLabels = (question: QuestionItem, chosen: ReadonlySet<string>) =>
  question.Options.filter(o => chosen.has(o.Label)).map(o => o.Label).join(', ');

/**
 * Whether a key is the prompt's: pressed inside it, or with nothing focused, and no dialog open. On any
 * other control it is that control's: a dialog's Cancel, the inbox's Send, a text field (#170, #240).
 */
function isForPrompt(prompt: HTMLElement | null, target: EventTarget | null): boolean {
  if (getOpenConfirm() || !prompt || !(target instanceof Node)) return false;
  return target === document.body || prompt.contains(target);
}

/** A button of the prompt's own other than an option (dismiss, a multi-select's Answer): Enter there is its click. */
const isOtherButton = (target: EventTarget | null) =>
  target instanceof HTMLButtonElement && !target.classList.contains('question-option');

export function QuestionPrompt({ questions, answered = {}, onAnswer, onDismiss }: Props) {
  const [activeIndex, setActiveIndex] = useState(0);
  // A multi-select's options checked so far, by question text
  const [checked, setChecked] = useState<Record<string, ReadonlySet<string>>>({});
  const promptRef = useRef<HTMLDivElement>(null);

  const openIndex = questions.findIndex(q => answered[q.Question] === undefined);
  const open = openIndex >= 0 ? questions[openIndex] : null;
  const options = open?.Options ?? NO_OPTIONS;

  // The next question: the highlight starts over. Another request: nothing stays checked either
  const [shown, setShown] = useState({ questions, open });
  if (shown.questions !== questions || shown.open !== open) {
    setShown({ questions, open });
    setActiveIndex(0);
    if (shown.questions !== questions) setChecked({});
  }

  const toggle = useCallback((question: QuestionItem, label: string) => {
    setChecked(c => {
      const next = new Set(c[question.Question] ?? []);
      if (!next.delete(label)) next.add(label);
      return { ...c, [question.Question]: next };
    });
  }, []);

  /** A click or a key on an option: a single-select answers with it, a multi-select checks or unchecks it. */
  const choose = useCallback((question: QuestionItem, label: string) => {
    if (question.MultiSelect) toggle(question, label);
    else onAnswer(question.Question, label);
  }, [toggle, onAnswer]);

  /** A multi-select's answer: what is checked, or with nothing checked, the highlighted option. */
  const answerMulti = useCallback((question: QuestionItem, fallback?: string) => {
    const chosen = checked[question.Question];
    const answer = chosen?.size ? joinLabels(question, chosen) : fallback;
    if (answer) onAnswer(question.Question, answer);
  }, [checked, onAnswer]);

  const handleKeyDown = useCallback((e: KeyboardEvent) => {
    // One key press is one action: not one another handler took, and not one meant for another control
    if (!open || options.length === 0 || e.defaultPrevented || !isForPrompt(promptRef.current, e.target)) return;

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
        if (open.MultiSelect) answerMulti(open, options[activeIndex].Label);
        else onAnswer(open.Question, options[activeIndex].Label);
        break;
      case 'Escape':
        e.preventDefault();
        onDismiss();
        break;
      default:
        // Number keys 1-9 for quick select (a multi-select's check)
        if (e.key >= '1' && e.key <= '9') {
          const idx = parseInt(e.key) - 1;
          if (idx < options.length) {
            e.preventDefault();
            choose(open, options[idx].Label);
          }
        }
        break;
    }
  }, [open, options, activeIndex, onAnswer, onDismiss, choose, answerMulti]);

  useEffect(() => {
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [handleKeyDown]);

  const several = questions.length > 1;

  return (
    <div className="question-prompt" ref={promptRef}>
      {/* Pulsing banner */}
      <div className="question-banner">
        <span className="question-pulse">?</span>
        <span className="question-label">{several ? `${questions.length} QUESTIONS` : 'WAITING FOR INPUT'}</span>
        {!several && questions[0]?.Header && <span className="question-header">{questions[0].Header}</span>}
        <button className="question-dismiss" onClick={onDismiss} title="Dismiss (Esc)">x</button>
      </div>

      <div className="question-list">
        {questions.map((q, qi) => {
          const isOpen = qi === openIndex;
          const answer = answered[q.Question];
          const chosen = checked[q.Question];
          return (
            <section key={q.Question} className={`question-item${isOpen ? ' question-item-open' : ''}`}>
              {(several || q.MultiSelect) && (
                <div className="question-item-header">
                  {several && <span className="question-item-num">{qi + 1}/{questions.length}</span>}
                  {several && q.Header && <span className="question-header">{q.Header}</span>}
                  {q.MultiSelect && <span className="question-multi">Choose any</span>}
                </div>
              )}

              {/* Question text: wrapped, never cut */}
              {q.Question && <div className="question-text">{q.Question}</div>}

              {q.Options.length > 0 && (
                <div className="question-options" role={q.MultiSelect ? 'group' : undefined}>
                  {q.Options.map((opt, i) => {
                    const active = isOpen && i === activeIndex;
                    const selected = q.MultiSelect ? chosen?.has(opt.Label) === true : answer === opt.Label;
                    return (
                      <button
                        key={opt.Label}
                        className={`question-option${active ? ' question-option-active' : ''}${selected ? ' question-option-selected' : ''}`}
                        onClick={() => choose(q, opt.Label)}
                        onMouseEnter={() => isOpen && setActiveIndex(i)}
                        onFocus={() => isOpen && setActiveIndex(i)}
                        aria-pressed={q.MultiSelect || several ? selected : undefined}
                      >
                        <span className="question-option-bar" />
                        <span className="question-option-num">{q.MultiSelect ? (selected ? '☑' : '☐') : i + 1}</span>
                        <span className="question-option-body">
                          <span className="question-option-label">{opt.Label}</span>
                          {opt.Description && <span className="question-option-desc">{opt.Description}</span>}
                        </span>
                        {active && <span className="question-option-hint">Enter</span>}
                      </button>
                    );
                  })}
                  {q.MultiSelect && (
                    <button className="btn btn-primary question-answer" onClick={() => answerMulti(q)} disabled={!chosen?.size}>
                      Answer
                    </button>
                  )}
                </div>
              )}

              {!isOpen && answer !== undefined && <div className="question-answered">Answer: {answer}</div>}
            </section>
          );
        })}
      </div>

      {options.length > 0 && (
        <div className="question-keys">
          <span>Up/Down navigate</span>
          <span>Enter select</span>
          <span>1-9 {open?.MultiSelect ? 'check' : 'quick pick'}</span>
          <span>Esc dismiss</span>
        </div>
      )}
    </div>
  );
}
