import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
// Bundled, so the client needs no CDN (MAUI, offline, no third-party requests)
import '@fontsource-variable/dm-sans/opsz.css'
import '@fontsource/dm-mono/300.css'
import '@fontsource/dm-mono/400.css'
import App from './App'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
