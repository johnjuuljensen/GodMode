// A touch-first device, whose on-screen keyboard has no Shift+Enter (a narrow laptop window is not one)
const touchKeyboardQuery = window.matchMedia('(hover: none) and (pointer: coarse)');

export const subscribeTouchKeyboard = (onChange: () => void) => {
  touchKeyboardQuery.addEventListener('change', onChange);
  return () => touchKeyboardQuery.removeEventListener('change', onChange);
};

/** Whether the keyboard is an on-screen one (a phone): focusing a field raises it over what is shown. */
export const isTouchKeyboard = () => touchKeyboardQuery.matches;
