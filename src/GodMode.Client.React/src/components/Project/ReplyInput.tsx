import { useId, useLayoutEffect, useRef, useState, useSyncExternalStore, type RefObject } from 'react';
import { offeredCommands } from './slashCommands';

// A touch-first device, whose on-screen keyboard has no Shift+Enter (a narrow laptop window is not one)
const touchKeyboardQuery = window.matchMedia('(hover: none) and (pointer: coarse)');
const subscribeTouchKeyboard = (onChange: () => void) => {
  touchKeyboardQuery.addEventListener('change', onChange);
  return () => touchKeyboardQuery.removeEventListener('change', onChange);
};
const getTouchKeyboard = () => touchKeyboardQuery.matches;

interface Props {
  value: string;
  onChange: (value: string) => void;
  /** Enter without a modifier, on a device with a hardware keyboard. */
  onSubmit: () => void;
  placeholder?: string;
  disabled?: boolean;
  className?: string;
  inputRef?: RefObject<HTMLTextAreaElement | null>;
  /**
   * The slash commands the session takes, without their `/` (its status's SlashCommands): while only a `/word`
   * is typed, the ones it starts are offered, and Tab or Enter completes the one picked
   */
  commands?: readonly string[] | null;
}

/**
 * A multiline reply that grows with its content up to the CSS max-height. Enter sends and
 * Shift/Ctrl/Cmd/Alt+Enter is a newline; on a touch keyboard every Enter is a newline and the Send
 * button sends.
 */
export function ReplyInput({ value, onChange, onSubmit, placeholder, disabled, className, inputRef, commands }: Props) {
  const ownRef = useRef<HTMLTextAreaElement>(null);
  const ref = inputRef ?? ownRef;
  const touchKeyboard = useSyncExternalStore(subscribeTouchKeyboard, getTouchKeyboard);
  const listId = useId();
  // The offered command picked, and the text Escape closed the offer for (it opens again once the text changes)
  const [picked, setPicked] = useState(0);
  const [closedFor, setClosedFor] = useState<string | null>(null);
  const offered = closedFor === value ? [] : offeredCommands(value, commands);
  const active = Math.min(picked, Math.max(0, offered.length - 1));

  const complete = (command: string) => {
    onChange(`/${command} `);
    setPicked(0);
    ref.current?.focus();
  };

  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = `${el.scrollHeight + el.offsetHeight - el.clientHeight}px`;
  }, [ref, value]);

  const handleKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (offered.length > 0 && !e.nativeEvent.isComposing) {
      const plain = !(e.shiftKey || e.ctrlKey || e.metaKey || e.altKey);
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        e.preventDefault();
        setPicked((active + (e.key === 'ArrowDown' ? 1 : offered.length - 1)) % offered.length);
        return;
      }
      if (plain && (e.key === 'Tab' || e.key === 'Enter')) {
        e.preventDefault();
        complete(offered[active]);
        return;
      }
      if (e.key === 'Escape') {
        e.preventDefault();
        setClosedFor(value);
        return;
      }
    }
    // Enter that confirms an IME composition is not a send (Safari reports it after compositionend, as keyCode 229)
    if (e.key !== 'Enter' || e.nativeEvent.isComposing || e.keyCode === 229) return;
    if (!touchKeyboard && !(e.shiftKey || e.ctrlKey || e.metaKey || e.altKey)) {
      e.preventDefault();
      onSubmit();
    } else if (e.ctrlKey || e.metaKey || e.altKey) {
      // Shift+Enter and plain Enter insert a newline natively; Ctrl/Cmd/Alt+Enter insert nothing
      e.preventDefault();
      const el = e.currentTarget;
      el.setRangeText('\n', el.selectionStart, el.selectionEnd, 'end');
      onChange(el.value);
    }
  };

  // The offer is the textarea's sibling, placed over it by the bar's CSS, so the textarea stays where its bar's layout puts it
  return (
    <>
      {offered.length > 0 && (
        <ul className="reply-commands" id={listId} role="listbox" aria-label="Commands">
          {offered.map((command, i) => (
            <li
              key={command}
              id={`${listId}-${i}`}
              role="option"
              aria-selected={i === active}
              className={i === active ? 'active' : undefined}
              // Not a blur of the input: the pick completes it, and typing goes on
              onMouseDown={e => { e.preventDefault(); complete(command); }}
            >
              /{command}
            </li>
          ))}
        </ul>
      )}
      <textarea
        ref={ref}
        rows={1}
        className={className}
        value={value}
        onChange={e => { setPicked(0); onChange(e.target.value); }}
        onKeyDown={handleKeyDown}
        enterKeyHint={touchKeyboard ? 'enter' : 'send'}
        placeholder={placeholder}
        disabled={disabled}
        aria-autocomplete={commands ? 'list' : undefined}
        aria-controls={offered.length > 0 ? listId : undefined}
        aria-expanded={commands ? offered.length > 0 : undefined}
        aria-activedescendant={offered.length > 0 ? `${listId}-${active}` : undefined}
      />
    </>
  );
}
