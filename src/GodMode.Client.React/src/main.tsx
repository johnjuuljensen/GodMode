import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
// Bundled, so the client needs no CDN (MAUI, offline, no third-party requests)
import '@fontsource-variable/dm-sans/opsz.css'
import '@fontsource/dm-mono/300.css'
import '@fontsource/dm-mono/400.css'
import App from './App'
// After the app's own stylesheets: a theme's decorations win a tie with the rule they restyle
import { applyStoredTheme } from './themes'

// The theme is on <html> before the first render, so the app never shows in another
applyStoredTheme()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
