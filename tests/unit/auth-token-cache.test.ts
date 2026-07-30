import { describe, it, expect, beforeEach } from 'vitest';
import { readFileSync } from 'fs';
import path from 'path';
import vm from 'vm';

/**
 * Covers dotnet/src/Web.Client/wwwroot/js/authTokenCache.js, which the Blazor stale-token
 * retry handler calls through JS interop. The C# tests fake the interop boundary, so this
 * storage-mutation logic is only reachable from here.
 */

const SCRIPT_PATH = path.resolve(
  __dirname,
  '../../dotnet/src/Web.Client/wwwroot/js/authTokenCache.js',
);

const CLIENT_ID = '6080d7c6-478c-49f0-ab62-dc23654d67a7';
const INDEX_KEY = `msal.token.keys.${CLIENT_ID}`;
const ACCESS_TOKEN_KEY =
  'uid.tid-login.microsoftonline.com-accesstoken-' + CLIENT_ID + '-tid-api://example/access_as_user--';
const REFRESH_TOKEN_KEY = 'uid.tid-login.microsoftonline.com-refreshtoken-' + CLIENT_ID + '----';
const ID_TOKEN_KEY = 'uid.tid-login.microsoftonline.com-idtoken-' + CLIENT_ID + '-tid---';

class FakeStorage {
  private map = new Map<string, string>();

  get length(): number {
    return this.map.size;
  }

  key(i: number): string | null {
    return [...this.map.keys()][i] ?? null;
  }

  getItem(key: string): string | null {
    return this.map.has(key) ? (this.map.get(key) as string) : null;
  }

  setItem(key: string, value: string): void {
    this.map.set(key, value);
  }

  removeItem(key: string): void {
    this.map.delete(key);
  }
}

function seedSignedInSession(storage: FakeStorage): void {
  storage.setItem(ACCESS_TOKEN_KEY, JSON.stringify({ secret: 'access-token' }));
  storage.setItem(REFRESH_TOKEN_KEY, JSON.stringify({ secret: 'refresh-token' }));
  storage.setItem(ID_TOKEN_KEY, JSON.stringify({ secret: 'id-token' }));
  storage.setItem(
    INDEX_KEY,
    JSON.stringify({
      idToken: [ID_TOKEN_KEY],
      accessToken: [ACCESS_TOKEN_KEY],
      refreshToken: [REFRESH_TOKEN_KEY],
    }),
  );
}

function purge(sessionStorage: FakeStorage, localStorage: FakeStorage): number {
  const context: Record<string, unknown> = { console };
  context.window = context;
  (context as { sessionStorage: FakeStorage }).sessionStorage = sessionStorage;
  (context as { localStorage: FakeStorage }).localStorage = localStorage;
  vm.createContext(context);
  vm.runInContext(readFileSync(SCRIPT_PATH, 'utf8'), context);
  const fn = (context as { talentMatchPurgeCachedAccessTokens: () => number })
    .talentMatchPurgeCachedAccessTokens;
  return fn();
}

describe('talentMatchPurgeCachedAccessTokens', () => {
  let sessionStorage: FakeStorage;
  let localStorage: FakeStorage;

  beforeEach(() => {
    sessionStorage = new FakeStorage();
    localStorage = new FakeStorage();
  });

  it('removes the cached access token', () => {
    seedSignedInSession(sessionStorage);

    const removed = purge(sessionStorage, localStorage);

    expect(removed).toBe(1);
    expect(sessionStorage.getItem(ACCESS_TOKEN_KEY)).toBeNull();
  });

  it('prunes the MSAL token key index so no dangling reference is left behind', () => {
    // Regression: deleting the credential while leaving it listed in the index made MSAL stop
    // resolving a token entirely, so every subsequent request went out unauthenticated.
    seedSignedInSession(sessionStorage);

    purge(sessionStorage, localStorage);

    const index = JSON.parse(sessionStorage.getItem(INDEX_KEY) as string);
    expect(index.accessToken).toEqual([]);
    const dangling = (index.accessToken as string[]).filter(
      (key) => sessionStorage.getItem(key) === null,
    );
    expect(dangling).toEqual([]);
  });

  it('keeps the refresh and id tokens so MSAL can renew silently', () => {
    seedSignedInSession(sessionStorage);

    purge(sessionStorage, localStorage);

    expect(sessionStorage.getItem(REFRESH_TOKEN_KEY)).not.toBeNull();
    expect(sessionStorage.getItem(ID_TOKEN_KEY)).not.toBeNull();
    const index = JSON.parse(sessionStorage.getItem(INDEX_KEY) as string);
    expect(index.refreshToken).toEqual([REFRESH_TOKEN_KEY]);
    expect(index.idToken).toEqual([ID_TOKEN_KEY]);
  });

  it('removes access tokens that the index does not claim', () => {
    sessionStorage.setItem('legacy-accesstoken-entry', JSON.stringify({ secret: 'orphan' }));

    const removed = purge(sessionStorage, localStorage);

    expect(removed).toBe(1);
    expect(sessionStorage.getItem('legacy-accesstoken-entry')).toBeNull();
  });

  it('does not double count a credential listed in the index', () => {
    seedSignedInSession(sessionStorage);

    const removed = purge(sessionStorage, localStorage);

    expect(removed).toBe(1);
  });

  it('leaves a corrupt index for MSAL to rebuild instead of throwing', () => {
    sessionStorage.setItem(INDEX_KEY, 'not-json');
    sessionStorage.setItem(ACCESS_TOKEN_KEY, JSON.stringify({ secret: 'access-token' }));

    const removed = purge(sessionStorage, localStorage);

    expect(removed).toBe(1);
    expect(sessionStorage.getItem(INDEX_KEY)).toBe('not-json');
  });

  it('purges both storages', () => {
    seedSignedInSession(sessionStorage);
    seedSignedInSession(localStorage);

    const removed = purge(sessionStorage, localStorage);

    expect(removed).toBe(2);
    expect(sessionStorage.getItem(ACCESS_TOKEN_KEY)).toBeNull();
    expect(localStorage.getItem(ACCESS_TOKEN_KEY)).toBeNull();
  });
});
