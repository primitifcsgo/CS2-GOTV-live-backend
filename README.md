# CS2 GOTV+ Live Dashboard Backend

Real-time CS2 match dashboard using Valve's built-in GOTV+ broadcast system.
No plugin needed, no ports to open — works on FakaHeda and any standard CS2 hosting.

## Architecture

```
CS2 Server (FakaHeda)           Your Backend (Render)              Dashboard
┌─────────────────────┐         ┌──────────────────────┐          ┌──────────┐
│ tv_broadcast = 1    │  POST   │ /ingest ← receives   │  GET     │ Netlify  │
│ tv_broadcast_url =  │─frag──▶│ /broadcast ← re-serve│◀─/state─│ polls    │
│ "https://xxx/ingest"│ (~3s)  │ demofile-net parses   │  600ms  │ 600ms    │
└─────────────────────┘         └──────────────────────┘          └──────────┘
   Engine does it all           .NET 8 + DemoFile.Net              Static HTML
```

## Deploy to Render (Docker)

1. Push this repo to GitHub
2. Render → New → Web Service → connect repo
3. **Environment:** Docker
4. **Environment Variable:** `PORT` = `5000` (Render may set this automatically)
5. Deploy — done. Note the URL, e.g. `https://cs2-gotv-plus.onrender.com`

```bash
# Local test first:
dotnet restore
dotnet run
# → http://localhost:5000/health
```

## Configure FakaHeda (2 steps)

**Step 1:** Upload `server-config/gotv_broadcast.cfg` to `csgo/cfg/` on the server.
Edit the URL:
```
tv_broadcast_url "https://cs2-gotv-plus.onrender.com/ingest"
```

**Step 2:** In server console:
```
exec gotv_broadcast
```

Done. CS2 starts pushing fragments immediately.

## REST API (same as CS2LivePlugin)

| Endpoint | Description |
|----------|-------------|
| `GET /state` | Full match state (dashboard polls this) |
| `GET /players` | Player list with stats |
| `GET /score` | Score summary |
| `GET /round` | Current round + bomb state |
| `GET /teams` | Team info |
| `GET /history` | Round history |
| `GET /health` | Health + broadcast status |
| `POST /reset` | Reset for new match |

Dashboard points at `https://cs2-gotv-plus.onrender.com` — same field, same behavior.

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `PORT` | `5000` | HTTP port (Render sets automatically) |
| `BROADCAST_AUTH` | (empty) | Must match `tv_broadcast_origin_auth` |

## Important Notes

**demofile-net API:** This project uses DemoFile v0.37.1. The library is pre-1.0 and
property names can shift between versions. If `dotnet build` shows type errors, check:
- Player properties: `PlayerName`, `SteamID`, `CSTeamNum`, `PlayerPawn`
- Pawn properties: `Health`, `ArmorValue`, `ActiveWeapon`
- Event properties: `Attacker`, `Player` (victim), `Weapon`, `Headshot`
- Money: `InGameMoneyServices?.Account`
- Stats: `ActionTrackingServices?.MatchStats?.Kills`

**FakaHeda checklist:**
- Verify `tv_enable 1` is allowed (most hosts support this)
- Verify `tv_broadcast_url` cvar is not blocked (it's an official Valve feature)
- If blocked, ask FakaHeda support to whitelist it

## Troubleshooting

**Health shows "waiting"** — CS2 hasn't pushed any fragments yet.
Check: is `gotv_broadcast.cfg` loaded? Run `tv_broadcast` in console, should show `1`.

**Fragments arrive but no player data** — Parser needs a few fragments to build
entity state. Wait 10-15 seconds after match starts.

**Dashboard shows connected but empty** — Same as above, or the map is still loading.
Check `/health` to verify `broadcastActive: true`.
