// Navigation shell browser bridge: session-scoped desktop preference, breakpoint and Escape notifications.
const STORAGE_KEY = 'talentmatch.navigation.desktopCollapsed'
const COMPACT_QUERY = '(max-width: 991.98px)'

let callbacks = null
let mediaQuery = null
let onMediaChange = null
let onKeyDown = null

function readDesktopCollapsed() {
  try {
    return window.sessionStorage.getItem(STORAGE_KEY) === 'true'
  } catch {
    return false
  }
}

export function initialize(dotNetCallbacks) {
  dispose()
  callbacks = dotNetCallbacks
  mediaQuery = window.matchMedia(COMPACT_QUERY)

  onMediaChange = event => {
    callbacks?.invokeMethodAsync('OnBreakpointChanged', event.matches)
  }
  if (typeof mediaQuery.addEventListener === 'function') {
    mediaQuery.addEventListener('change', onMediaChange)
  } else {
    mediaQuery.addListener(onMediaChange)
  }

  onKeyDown = event => {
    if (event.key === 'Escape' || event.key === 'Esc') {
      callbacks?.invokeMethodAsync('OnEscapePressed')
    }
  }
  window.addEventListener('keydown', onKeyDown)

  return {
    desktopCollapsed: readDesktopCollapsed(),
    isCompact: mediaQuery.matches,
  }
}

export function persistDesktopCollapsed(collapsed) {
  try {
    window.sessionStorage.setItem(STORAGE_KEY, collapsed ? 'true' : 'false')
  } catch {
    // A blocked session store degrades to per-render defaults rather than failing the shell.
  }
}

export function dispose() {
  if (mediaQuery && onMediaChange) {
    if (typeof mediaQuery.removeEventListener === 'function') {
      mediaQuery.removeEventListener('change', onMediaChange)
    } else {
      mediaQuery.removeListener(onMediaChange)
    }
  }
  if (onKeyDown) {
    window.removeEventListener('keydown', onKeyDown)
  }

  callbacks = null
  mediaQuery = null
  onMediaChange = null
  onKeyDown = null
}
