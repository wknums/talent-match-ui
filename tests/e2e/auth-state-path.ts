// storageState only carries cookies and localStorage. Blazor's MSAL cache lives in
// sessionStorage, so the capture helper records it in this sibling file.
export function sessionStatePathFor(authStatePath: string): string {
  return `${authStatePath.replace(/\.json$/i, '')}.session.json`
}
