/**
 * Bridge message types shared between MAUI host and React app.
 * Mirrors GodMode.Maui.Bridge.BridgeMessage (PascalCase, matching JsonDefaults).
 */

export interface BridgeMessage {
  Type: string;
  Id?: string;
  Payload?: unknown;
}

/**
 * Augment Window with HybridWebView proxy (injected by MAUI HybridWebView).
 */
declare global {
  interface Window {
    HybridWebView?: {
      SendRawMessage(message: string): void;
    };
  }
}
