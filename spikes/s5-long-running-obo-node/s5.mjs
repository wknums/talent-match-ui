// Spike S5 — long-running on-behalf-of with MSAL Node (T008A, Node half).
//
//   node s5.mjs obo <user>            take the incoming API token written by the .NET harness
//                                     (`login <user> --interactive --dump-assertion`) and exchange it OBO;
//                                     persist the MSAL cache to a file (production: encrypted, in the database)
//   node s5.mjs probe <user> [--cached]   a NEW process, no user, no request: try to get a Graph token again
//                                     (silent via the cached account, forced refresh unless --cached)
//   node s5.mjs inspect <user>        what the cache holds (counts only, never values)
//   node s5.mjs show                  print every recorded probe
//
// Configuration: the same S5_* environment variables the .NET harness uses. No token value is printed.

import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import crypto from "node:crypto";
import * as msal from "@azure/msal-node";

const env = (n) => process.env[n] ?? (() => { throw new Error(`${n} is not set`); })();
const stateDir = process.env.S5_STATE_DIR ?? path.join(os.homedir(), ".talent-match", "s5");
const tenant = env("S5_TENANT_ID");
const apiClientId = env("S5_API_CLIENT_ID");
const pfx = env("S5_API_CERT_PATH").replace(/\.pfx$/i, "");
const resultsFile = path.join(stateDir, "results-node.jsonl");

const GRAPH_SCOPES = [
  "https://graph.microsoft.com/User.Read",
  "https://graph.microsoft.com/Files.Read.All",
  "https://graph.microsoft.com/Sites.Read.All",
];

const cachePath = (user) => path.join(stateDir, `${user}.node-cache.json`);

function build(user) {
  const certPem = fs.readFileSync(`${pfx}.cert.pem`, "utf8");
  const der = Buffer.from(certPem.replace(/-----[^-]+-----|\s/g, ""), "base64");
  const thumbprint = crypto.createHash("sha1").update(der).digest("hex");
  const cachePlugin = {
    async beforeCacheAccess(ctx) {
      if (fs.existsSync(cachePath(user))) ctx.tokenCache.deserialize(fs.readFileSync(cachePath(user), "utf8"));
    },
    async afterCacheAccess(ctx) {
      if (ctx.cacheHasChanged) fs.writeFileSync(cachePath(user), ctx.tokenCache.serialize());
    },
  };
  return new msal.ConfidentialClientApplication({
    auth: {
      clientId: apiClientId,
      authority: `https://login.microsoftonline.com/${tenant}`,
      clientCertificate: { thumbprintSha256: crypto.createHash("sha256").update(der).digest("hex"), thumbprint, privateKey: fs.readFileSync(`${pfx}.key.pem`, "utf8") },
    },
    cache: { cachePlugin },
  });
}

async function callGraph(accessToken) {
  const r = await fetch("https://graph.microsoft.com/v1.0/me?$select=id", { headers: { Authorization: `Bearer ${accessToken}` } });
  return r.status;
}

const record = (r) => fs.appendFileSync(resultsFile, JSON.stringify(r) + "\n");
const aadsts = (m) => /AADSTS\d+/.exec(m ?? "")?.[0] ?? null;

function classify(e) {
  const code = e.errorCode ?? e.name;
  return {
    outcome: e instanceof msal.InteractionRequiredAuthError ? "interaction_required" : "error",
    errorCode: code,
    subError: e.subError ?? null,
    aadsts: aadsts(e.errorMessage ?? e.message),
    detail: (e.errorMessage ?? e.message ?? "").replace(/\s+/g, " ").slice(0, 240),
  };
}

const [, , command, user, flag] = process.argv;
const started = (u) => {
  try { return JSON.parse(fs.readFileSync(path.join(stateDir, `${u}.node-state.json`), "utf8")).startedUtc; } catch { return null; }
};

if (command === "obo") {
  const assertion = fs.readFileSync(path.join(stateDir, `${user}.assertion`), "utf8").trim();
  fs.rmSync(cachePath(user), { force: true });
  const app = build(user);
  // offline_access asks Entra for a refresh token; MSAL Node does not add it for OBO on its own.
  const result = await app.acquireTokenOnBehalfOf({ oboAssertion: assertion, scopes: [...GRAPH_SCOPES, "offline_access"] });
  const accounts = await app.getTokenCache().getAllAccounts();
  fs.writeFileSync(path.join(stateDir, `${user}.node-state.json`), JSON.stringify({ startedUtc: new Date().toISOString(), accountHome: result.account?.homeAccountId ?? accounts[0]?.homeAccountId ?? null }));
  console.log(`[${user}] OBO ok (token source ${result.fromCache ? "cache" : "identity provider"}); account in result: ${result.account ? "yes" : "no"}; accounts in cache: ${accounts.length}`);
  console.log(`[${user}] Graph /me with the OBO token: HTTP ${await callGraph(result.accessToken)}`);
  record({ utc: new Date().toISOString(), user, minutes: 0, outcome: "ok", detail: "obo" });
} else if (command === "inspect") {
  const raw = JSON.parse(fs.readFileSync(cachePath(user), "utf8"));
  console.log(Object.fromEntries(Object.entries(raw).map(([k, v]) => [k, Object.keys(v).length])));
} else if (command === "probe") {
  const state = JSON.parse(fs.readFileSync(path.join(stateDir, `${user}.node-state.json`), "utf8"));
  const minutes = Math.round((Date.now() - Date.parse(state.startedUtc)) / 6000) / 10;
  const force = flag !== "--cached";
  const app = build(user);
  let rec;
  try {
    const accounts = await app.getTokenCache().getAllAccounts();
    const account = accounts.find((a) => a.homeAccountId === state.accountHome) ?? accounts[0];
    if (!account) {
      rec = { outcome: "no_account_in_cache", errorCode: "no_account", detail: "MSAL Node has no account for an OBO-acquired token; silent acquisition is not possible" };
    } else {
      const r = await app.acquireTokenSilent({ account, scopes: GRAPH_SCOPES, forceRefresh: force });
      rec = { outcome: "ok", tokenSource: r.fromCache ? "Cache" : "IdentityProvider", graph: await callGraph(r.accessToken), detail: `expires ${r.expiresOn?.toISOString()}` };
    }
  } catch (e) {
    rec = classify(e);
  }
  const full = { utc: new Date().toISOString(), user, minutes, forced: force, ...rec };
  record(full);
  console.log(`${full.utc} [${user}] +${minutes} min  ${full.outcome}  ${full.errorCode ?? ""} ${full.aadsts ?? ""} ${full.tokenSource ?? ""} ${full.graph ? "graph=" + full.graph : ""} ${full.outcome === "no_account_in_cache" ? full.detail : ""}`.trimEnd());
  process.exitCode = full.outcome === "ok" ? 0 : 2;
} else if (command === "show") {
  if (!fs.existsSync(resultsFile)) console.log("No probes recorded yet.");
  else for (const l of fs.readFileSync(resultsFile, "utf8").trim().split("\n")) {
    const r = JSON.parse(l);
    console.log(`${r.utc}  ${String(r.user).padEnd(10)} +${String(r.minutes).padStart(6)} min  ${String(r.outcome).padEnd(22)} ${r.errorCode ?? ""} ${r.aadsts ?? ""} ${r.detail ?? ""}`);
  }
} else {
  console.log("s5.mjs: obo <user> | probe <user> [--cached] | inspect <user> | show");
  process.exitCode = 1;
}
