/**
 * Host bridge detection and interface.
 * When running inside a MAUI (or Electron) host, the host injects
 * `window.godmode` with native capabilities (voice, AI, secure storage).
 * When running standalone in a browser, this is absent.
 */

export interface GodModeHost {
  voice: {
    startListening(): Promise<string>;
    stopListening(): void;
    speak(text: string): Promise<void>;
    stopSpeaking(): void;
    isAvailable(): boolean;
    getLanguages(): string[];
    setLanguage(lang: string): void;
  };
  ai: {
    chat(prompt: string, tier: string): Promise<string>;
    isAvailable(): boolean;
  };
}

declare global {
  interface Window {
    godmode?: GodModeHost;
  }
}

/** Returns the host bridge if running inside a native host, null otherwise. */
export function getHost(): GodModeHost | null {
  return window.godmode ?? null;
}

/** Returns true if a native host is available. */
export function hasHost(): boolean {
  return window.godmode !== undefined;
}
