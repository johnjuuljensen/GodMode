import { useLayoutEffect, useRef, useSyncExternalStore, type RefObject } from 'react';

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
}

/**
 * A multiline reply that grows with its content up to the CSS max-height. Enter sends and
 * Shift/Ctrl/Cmd/Alt+Enter is a newline; on a touch keyboard every Enter is a newline and the Send
 * button sends.
 */
export function ReplyInput({ value, onChange, onSubmit, placeholder, disabled, className, inputRef }: Props) {
  const ownRef = useRef<HTMLTextAreaElement>(null);
  const ref = inputRef ?? ownRef;
  const touchKeyboard = useSyncExternalStore(subscribeTouchKeyboard, getTouchKeyboard);

  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = `${el.scrollHeight + el.offsetHeight - el.clientHeight}px`;
  }, [ref, value]);

  const handleKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
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

  return (
    <textarea
      ref={ref}
      rows={1}
      className={className}
      value={value}
      onChange={e => onChange(e.target.value)}
      onKeyDown={handleKeyDown}
      enterKeyHint={touchKeyboard ? 'enter' : 'send'}
      placeholder={placeholder}
      disabled={disabled}
    />
  );
}
