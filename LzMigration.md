# Lz Migration — CloudFront KVS Routing (Behaviors) Port

**Status:** Analysis only. Captured for a later, separate effort.
**Date:** 2026-06-06
**Author:** investigation during the `lz updateedge` work.

## TL;DR

The CloudFront **KeyValueStore (KVS) routing table** — the per-host entries that the
edge CloudFront Functions read to do dynamic origin/behavior resolution from the
**System → Tenant → Subtenant** hierarchy — is **not implemented in the Monro
deployment toolchain**. It exists and works in the **BC** (`_BC/_Dev_BC`)
deployment toolchain, which is the more complete port of the legacy PowerShell
`LzAws` module. Monro's Ecs topology instead uses **static CloudFront behaviors
baked into the Pulumi distribution** plus a minimal KVS that only carries the
`parked` flag.

This document records where each piece lives, the entry schema, and what a Monro
port would entail. **No code changes are proposed here** — this is a map for a
future effort.

## How we got here

While extending `lz updateedge` (the zero-downtime CloudFront-function publisher),
the question arose: should it also write KVS entries? Investigation showed:

- Monro's live **test** KVS (`med-monro-kvs`) contains only:
  ```
  monrotest.click  → {"parked":false}
  monrotest2.click → {"parked":false}
  ```
  i.e. the simple park-flag schema, written by `AwsParkManager` (park/unpark) — **not**
  by `deploytenant`.
- In Monro's `lz`, the only KVS writer anywhere is `AwsParkManager`; the
  `BehaviorsConfig` config model (`Apis`/`Assets`/`WebApps`/`StaticSites`) exists
  but has **zero consumers**.
- `Implementation.md` still lists the legacy behavior as an open question:
  *"CloudFront KVS chunking: the current LzAws module handles KVS entry chunking
  for the 1024-byte limit. Determine if this logic moves to Lz.Aws or becomes
  unnecessary with a different CDN routing approach."*

The legacy source we ported from is `C:\Users\TimothyMay\repos\Scratch\LzAws`
(PowerShell). Its KVS subsystem: `Get-TenantKVSEntries.ps1`,
`Get-SubtenantKVSEntry.ps1`, `Get-BehaviorsHashTable.ps1`, `Split-KVSEntry.ps1`
(chunking), `Update-KVSEntry.ps1`.

## The routing model lives across three layers

The KVS-driven routing is **not** a single framework feature — it spans three
layers. Monro currently has only the bottom one.

| Layer | What it provides | BC (`_BC/_Dev_BC`) | Monro (`_Monro/_Test_Monro`) |
|---|---|---|---|
| **lz framework** | `ConfigMerger.ResolveWebApps` (System→Tenant→Subtenant cascade) + `ILzPlugin.RefreshTenantRuntimeAsync` hook + `deploytenant` post-deploy call | ✅ present, unit-tested (`ConfigMergerTests`) | ❌ no `ResolveWebApps`; `BehaviorsConfig` stubbed; (verify) no `RefreshTenantRuntimeAsync` hook |
| **Deploy plugin** | builds + writes the per-host KVS entries; `lz updatekvs`; implements the refresh hook | ✅ `BCPlugin.UpdateKvsForTenantAsync` | ❌ `MonroPlugin` has only seed-data commands |
| **CloudFront functions** | edge functions that *read* the KVS entries (`CFRequest.js`, `CFAuthConfig.js`, `CFAuth.js`, `CFExplore.js`) | ✅ full suite (EcsExpress topology) | ❌ `CFViewerRequest.js` reads only `parked`; Ecs resolves via static Pulumi behaviors |

Key insight: **the writer is a plugin responsibility**, because the entry schema
is app/tenant-specific. That is why it is correct that it lives in `BCPlugin` and
not in shared `lz` — and why a Monro equivalent belongs in `MonroPlugin`.

## Reference implementation (BC)

### Entry points
- `Deploy/BCPlugin.cs`
  - `RegisterUpdateKvsCommand` → **`lz updatekvs --tenant <tk>`**
  - `RefreshTenantRuntimeAsync(SystemConfig, TenantConfig)` → plugin hook called by
    `lz deploytenant` post-deploy and `lz deploysubtenants`
  - `UpdateKvsForTenantAsync(...)` → the writer (shared by both)
- `lz/Lz.Core/Config/ConfigMerger.cs` → `ResolveWebApps(system, tenant, subtenantBehaviors)`
  implements the cascade (`ApplyLevel(system,0)` → `tenant,1` → `subtenant,2`).
- `lz/Lz.Aws/EcsExpress/AwsEcsExpressCloudFrontComponent.cs` → creates the KVS
  (empty) and wires the KVS-reading CF functions (`CFRequest.js` etc.).

### KVS entry schema (what `UpdateKvsForTenantAsync` writes)

Written imperatively via the CloudFront KeyValueStore API (`PutKey`, ETag-guarded —
same mechanism as `AwsParkManager`).

| Key | Value |
|---|---|
| `AuthConfigs` | per-pool auth metadata: `{ValidateAudience, ClientId, Issuer, PoolId, Region, HostedUIDomain}`, read from **foundation Pulumi stack outputs** (`auth_{type}_userPoolId`, `auth_{type}_authority`, etc.) |
| `{domain}` | tenant entry: `{env, systemKey, tenantKey, ss, ts, region, behaviors:[…]}` |
| `{domain}-auth` | `{apps:[{path, name, authConfig}]}` (auth-related functions read this) |
| `{subLabel}.{domain}` | subtenant entry: same shape + `subtenantKey`, `sts` |
| `{subLabel}.{domain}-auth` | per-subtenant `apps[]` |

**Behavior tuples** (CF functions read `behavior[4]` as the *level* uniformly):

```
assets     : [path, "assets",     suffixToken, region, level]
webapp     : [path, "webapp",     appName,     suffixToken, level, gated]   # gated = 1 if AuthConfig set
staticsite : [path, "staticsite", appName,     suffixToken, level]          # no region; CF uses config.region
api        : [path, "api",        origin,      region, env]
```

- **Cascade**: non-webapp behaviors merge by dictionary replacement
  (system → tenant → subtenant); WebApps go through `ConfigMerger.ResolveWebApps`
  so a tenant/subtenant can override only `(Path, AuthConfig)` and inherit
  `AppName`. The resolved `Level` drives the suffix token.
- **Suffix tokens** by level: `0 → {ss}`, `1 → {ts}`, `2 → {sts}` — expanded at the
  edge into bucket names (`{sk}---webapp-{appName}-{ss}`, etc.).
- **1024-byte limit**: BC avoids `Split-KVSEntry` chunking by trimming values
  (dropped duplicated `Authority`/`MetadataUrl`) to stay under the per-value limit.
  If three-plus pools or richer entries push past 1 KB again, the legacy chunking
  (`Split-KVSEntry` / `Update-KVSEntry` / `Remove-KVSChunks`) is the pattern to port.
- Also generates a `/venues/` `subtenants.json` for the static venue-listing page
  (BC-specific; not relevant to Monro).

## What a Monro port would require (cross-cutting)

Porting KVS-driven routing to Monro is **not** a one-file change. It spans the same
three layers:

1. **lz framework (Monro's copy)** — bring forward from BC:
   - `ConfigMerger.ResolveWebApps` (+ the cascade for assets/staticsites/apis).
   - `ILzPlugin.RefreshTenantRuntimeAsync` hook and the `deploytenant` post-deploy
     call site (verify Monro's `ILzPlugin` lacks it).
   - Confirm `TenantConfig`/`SubtenantEntry`/`BehaviorsConfig` parity (BC's models
     are richer: `SubtenantEntry.DisplayName`/`IncludeOnVenuesPage`,
     `WebAppBehavior.AuthConfig`).
2. **MonroPlugin** — implement `UpdateKvsForTenantAsync` (+ `lz updatekvs`, or fold
   into `lz updateedge`) and the `RefreshTenantRuntimeAsync` hook, mirroring
   `BCPlugin`. Adjust the schema to Monro's auth model (Keycloak, not Cognito — the
   `AuthConfigs` block and `Issuer`/`PoolId` shape differ).
3. **CloudFront** — adopt KVS-reading functions (port/extend `CFRequest.js` etc.) or
   extend Monro's `CFViewerRequest.js`. **Without this, written entries are unread**
   — Monro's Ecs distribution resolves origins via static Pulumi behaviors today,
   so the writer alone would be a no-op at the edge.

### Decision to make first
Does Monro want to **adopt the dynamic KVS routing model** (align with BC /
EcsExpress, enabling per-subtenant behaviors and partial auth overrides at the
edge), or **keep the static-behavior Ecs approach** and only use the KVS for the
`parked` flag (status quo)? The answer determines whether this migration is worth
doing at all.

## Caveats / things to verify when this is picked up
- Confirm whether Monro's `lz` `ILzPlugin` already defines `RefreshTenantRuntimeAsync`
  (it may have drifted from BC's `lz`).
- Confirm BC's deployed systems actually populate the KVS via this path (dump a BC
  dev KVS the way we dumped Monro's test KVS) — to be 100% sure the writer is live
  and not superseded by a manual/legacy step.
- Auth model difference: BC uses **Cognito** (pool IDs, Cognito issuer); Monro uses
  **Keycloak** (realms). The `AuthConfigs` KVS block and the auth CF functions must
  be reworked accordingly.
- The two `lz` copies (`_Monro/_Test_Monro/lz` and `_BC/_Dev_BC/lz`) have diverged;
  treat BC's `lz` as ahead on the routing/`ConfigMerger` work and reconcile
  deliberately rather than blind-merging.

## Source references

Legacy (PowerShell): `C:\Users\TimothyMay\repos\Scratch\LzAws`
- `Private/Get-TenantKVSEntries.ps1`, `Get-SubtenantKVSEntry.ps1`,
  `Get-BehaviorsHashTable.ps1`, `Split-KVSEntry.ps1`, `Update-KVSEntry.ps1`
- `Public/Get-KvsEntries.ps1`, `Deploy-TenantCDNAws.ps1`; `KVS-CHUNKING.md`

BC (C#, reference port): `C:\Users\TimothyMay\repos\_BC\_Dev_BC`
- `Deploy/BCPlugin.cs` — `UpdateKvsForTenantAsync`, `RegisterUpdateKvsCommand`,
  `RefreshTenantRuntimeAsync`
- `lz/Lz.Core/Config/ConfigMerger.cs` — `ResolveWebApps`
- `lz/Lz.Aws/EcsExpress/AwsEcsExpressCloudFrontComponent.cs` — KVS + CF functions

Monro (C#, current): `C:\Users\TimothyMay\repos\_Monro\_Test_Monro`
- `lz/Lz.Aws/Ecs/AwsParkManager.cs` — the only current KVS writer (`parked` flag)
- `lz/Lz.Aws/Ecs/AwsCloudFrontComponent.cs` — creates KVS empty; static behaviors
- `lz/Lz.Aws/Ecs/AwsEdgeUpdater.cs` — `lz updateedge` (functions only, no KVS)
- `lz/Lz.Core/Config/BehaviorsConfig.cs` — stub model, no consumers
- `Deploy/MonroPlugin.cs` — seed-data commands only (no KVS writer)
