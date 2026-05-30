> **Procedencia:** documento del equipo de investigación, incorporado como referencia. Endpoints y
> client_id son públicos/necesarios; no contiene tokens reales. Las credenciales se toman en runtime,
> nunca se embeben. Capturas crudas (`out/`) NO se incluyen en este repo.

# Zwift account login (username + password) — documented & verified

**Date:** 2026-05-30. Lets the bridge authenticate with the **user's own Zwift account** (ethical-by-design: own account → own hardware), producing the `access_token` (Bearer) needed for the d-lock unlock POST. No token is ever embedded.

## Endpoint

Discovered from captured traffic — `GET https://us-or-rly101.zwift.com/api/auth` returns:
```json
{"realm":"zwift","launcher":"https://launcher.zwift.com/launcher","url":"https://secure.zwift.com/auth/","webUrl":"https://www.zwift.com/"}
```
So the OIDC token endpoint is:
```
POST https://secure.zwift.com/auth/realms/zwift/protocol/openid-connect/token
Content-Type: application/x-www-form-urlencoded
```

## Grant 1 — password (Resource Owner Password Credentials)

```
client_id=Zwift_Mobile_Link
grant_type=password
username=<account email>
password=<account password>
```

**Verified (2026-05-30) with a dummy login, no real creds:**
- `client_id=Zwift_Mobile_Link` → `HTTP 401 {"error":"invalid_grant","error_description":"Invalid user credentials"}` → the client **has Direct Access Grants enabled**; the grant works, it just rejected the fake password. Real credentials → 200 + token set.
- `client_id=Game_Launcher` → `HTTP 400 {"error":"unauthorized_client","error_description":"Client not allowed for direct access grants"}` → Game_Launcher does **not** support password grant.

→ Use **`Zwift_Mobile_Link`** for password login.

## Grant 2 — refresh_token (no password)

```
client_id=Game_Launcher
grant_type=refresh_token
refresh_token=<saved refresh_token>
```
**Verified working** against the live server (used this session to mint a fresh access_token from the captured refresh_token).

## Response (both grants)

JSON: `access_token` (JWT, ~2262 chars), `refresh_token`, `expires_in` (**21600 s = 6 h**), `refresh_expires_in` (**691200 s = 8 days**), `token_type=Bearer`, `id_token`, `scope`. Use `access_token` as `Authorization: Bearer <…>` for `/api/d-lock-service/...` and other Zwift APIs.

## The launcher's own flow (for reference, NOT needed)

The official Game_Launcher does an **authorization_code** flow: it opens a webview to `secure.zwift.com`, the user types credentials in the Keycloak login page, and the launcher receives `grant_type=authorization_code&code=zwift_refresh_token<JWT>&redirect_url=http://zwift&client_id=Game_Launcher`. That's why the raw username/password never appear in the API capture. For a headless tool the ROPC password grant above is the practical equivalent.

## Tooling

- `ZwiftAuth` (`src/Auth/ZwiftAuth.cs`): `LoginAsync(user,pass)` (Zwift_Mobile_Link), `RefreshAsync(rt)` (Game_Launcher), `ResolveTokenFromEnvAsync()`.
- `zwift-zap-probe --login`: reads `ZWIFT_USERNAME`+`ZWIFT_PASSWORD` (or `ZWIFT_REFRESH_TOKEN`), saves the token to `out/zwift_token.json`.
- `zwift-zap-probe --unlock`: auto-resolves the token from env (`ZWIFT_ACCESS_TOKEN` → `ZWIFT_USERNAME`/`ZWIFT_PASSWORD` → `ZWIFT_REFRESH_TOKEN`) before running the unlock chain.

PowerShell, one-shot unlock straight from credentials:
```powershell
$env:ZWIFT_USERNAME = 'you@example.com'
$env:ZWIFT_PASSWORD = 'your-password'
dotnet run -- --unlock
```

## Security / ethics
- Credentials are taken from the environment at runtime, never hardcoded or logged (only the token length + last 8 chars are shown).
- `out/zwift_token.json` and `out/mitm/*` hold real tokens — **gitignore / never publish**.
- 2FA: if the account has MFA enabled, ROPC will fail; fall back to the refresh_token grant (obtain the refresh_token once via the launcher/web login).
