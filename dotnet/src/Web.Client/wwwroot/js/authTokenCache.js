// Drops cached MSAL access tokens so the next acquisition performs a silent network refresh.
// Refresh tokens are deliberately left in place: removing only the access token lets MSAL
// renew silently, and a genuinely expired session still falls back to an interactive redirect.
//
// MSAL does not scan storage to find credentials. It keeps an index at
// "msal.token.keys.<clientId>" listing the keys it believes are cached. Deleting a credential
// entry without pruning that index leaves a dangling reference: the cache manager looks the key
// up, gets null, and stops resolving a token for that scope, so every later request goes out
// unauthenticated for the rest of the session. The index must be rewritten in the same pass.
const TOKEN_KEY_INDEX_PREFIX = 'msal.token.keys.';

function purgeAccessTokensFrom(storage) {
    let removed = 0;

    const indexKeys = [];
    const looseAccessTokenKeys = [];
    for (let i = 0; i < storage.length; i++) {
        const key = storage.key(i);
        if (!key) {
            continue;
        }
        if (key.startsWith(TOKEN_KEY_INDEX_PREFIX)) {
            indexKeys.push(key);
        } else if (key.toLowerCase().includes('accesstoken')) {
            looseAccessTokenKeys.push(key);
        }
    }

    for (const indexKey of indexKeys) {
        let index;
        try {
            index = JSON.parse(storage.getItem(indexKey) || '{}');
        } catch {
            // A corrupt index cannot be pruned safely; leave it for MSAL to rebuild.
            continue;
        }
        if (!Array.isArray(index.accessToken) || index.accessToken.length === 0) {
            continue;
        }
        for (const credentialKey of index.accessToken) {
            storage.removeItem(credentialKey);
            removed++;
        }
        index.accessToken = [];
        storage.setItem(indexKey, JSON.stringify(index));
    }

    // Anything the index did not claim: older cache layouts, or partially written entries.
    for (const key of looseAccessTokenKeys) {
        if (storage.getItem(key) !== null) {
            storage.removeItem(key);
            removed++;
        }
    }

    return removed;
}

window.talentMatchPurgeCachedAccessTokens = function () {
    let removed = 0;
    for (const storage of [window.sessionStorage, window.localStorage]) {
        try {
            removed += purgeAccessTokensFrom(storage);
        } catch (err) {
            console.warn('talentMatchPurgeCachedAccessTokens failed', err);
        }
    }
    return removed;
};
