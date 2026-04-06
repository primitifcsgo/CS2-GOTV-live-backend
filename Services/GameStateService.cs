using DemoFile;
using DemoFile.Game.Cs;
using GotvPlusServer.Models;

namespace GotvPlusServer.Services;

public class GameStateService : BackgroundService
{
    private readonly FragmentStore _store;
    private readonly ILogger<GameStateService> _log;
    private readonly string _broadcastUrl;
    
    private MatchState _state = new();
    private readonly object _stateLock = new();
    private volatile CsDemoParser? _activeDemo;

    public GameStateService(FragmentStore store, ILogger<GameStateService> log, IConfiguration config)
    {
        _store = store;
        _log = log;
        var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
        _broadcastUrl = $"http://localhost:{port}/broadcast";
    }

    public MatchState GetState()
    {
        lock (_stateLock)
        {
            _state.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return _state;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("GameStateService starting, waiting for broadcast...");
        
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(500, ct); } catch { break; }
                if (_activeDemo != null) SnapshotPlayers(_activeDemo);
            }
        }, ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                while (!_store.IsActive && !ct.IsCancellationRequested)
                    await Task.Delay(1000, ct);
                if (ct.IsCancellationRequested) break;

                _log.LogInformation("Broadcast detected, starting parser...");
                lock (_stateLock) { _state = new MatchState(); }
                await ParseBroadcast(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Parser error, will retry in 3s...");
                _activeDemo = null;
                try { await Task.Delay(3000, ct); } catch { break; }
            }
        }
    }

    private async Task ParseBroadcast(CancellationToken ct)
    {
        var demo = new CsDemoParser();
        _activeDemo = demo;

        // ── Bug #3 fix: Map name from ServerSpawn ───────────
        demo.Source1GameEvents.ServerSpawn += e =>
        {
            lock (_stateLock) { _state.MapName = e.Mapname ?? _state.MapName; }
        };

        demo.Source1GameEvents.PlayerDeath += e =>
        {
            lock (_stateLock)
            {
                var kill = new KillEvent
                {
                    Attacker = e.Attacker?.PlayerName ?? "World",
                    Victim = e.Player?.PlayerName ?? "?",
                    Weapon = e.Weapon ?? "",
                    Headshot = e.Headshot,
                    ThroughSmoke = e.Thrusmoke,
                    NoScope = e.Noscope,
                    Wallbang = e.Penetrated > 0,
                    AttackerFlashed = e.Attackerblind ? 1 : 0,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                _state.CurrentRound ??= new RoundState { Number = _state.RoundNumber };
                _state.CurrentRound.Kills.Add(kill);
                var victim = _state.Players.FirstOrDefault(p => p.Name == kill.Victim);
                if (victim != null) victim.IsAlive = false;
            }
        };

        demo.Source1GameEvents.RoundStart += e =>
        {
            lock (_stateLock)
            {
                _state.RoundNumber++;
                _state.CurrentRound = new RoundState { Number = _state.RoundNumber, Phase = "live" };
                foreach (var p in _state.Players) p.IsAlive = true;
            }
        };

        // ── Bug #2 fix: Correct CS2 reason codes ────────────
        demo.Source1GameEvents.RoundEnd += e =>
        {
            lock (_stateLock)
            {
                var winner = e.Winner switch { 2 => "T", 3 => "CT", _ => "?" };
                var reason = e.Reason switch
                {
                    1 => WinReason.TWinBombExplode,
                    7 => WinReason.CTWinDefuse,         // BOMB_DEFUSED
                    8 => WinReason.CTWinElimination,    // CTS_WIN
                    9 => WinReason.TWinElimination,     // TERRORISTS_WIN
                    12 => WinReason.CTWinTime,
                    _ => winner == "CT" ? WinReason.CTWinElimination : WinReason.TWinElimination
                };
                if (winner == "CT") _state.CT.Score++; else if (winner == "T") _state.T.Score++;
                _state.RoundHistory.Add(new RoundHistoryEntry
                {
                    Round = _state.RoundNumber, Winner = winner, Reason = reason,
                    CTScore = _state.CT.Score, TScore = _state.T.Score
                });
                if (_state.CurrentRound != null) _state.CurrentRound.Phase = "ended";
            }
        };

        demo.Source1GameEvents.RoundFreezeEnd += e =>
        { lock (_stateLock) { if (_state.CurrentRound != null) _state.CurrentRound.Phase = "live"; } };

        demo.Source1GameEvents.BombPlanted += e =>
        { lock (_stateLock) { if (_state.CurrentRound != null) _state.CurrentRound.Bomb = new BombInfo { Status = BombStatus.Planted, Planter = e.Player?.PlayerName, Site = e.Site.ToString(), PlantedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }; } };

        demo.Source1GameEvents.BombDefused += e =>
        { lock (_stateLock) { if (_state.CurrentRound?.Bomb != null) { _state.CurrentRound.Bomb.Status = BombStatus.Defused; _state.CurrentRound.Bomb.Defuser = e.Player?.PlayerName; } } };

        demo.Source1GameEvents.BombExploded += e =>
        { lock (_stateLock) { if (_state.CurrentRound?.Bomb != null) _state.CurrentRound.Bomb.Status = BombStatus.Exploded; } };

        demo.Source1GameEvents.BombBegindefuse += e =>
        { lock (_stateLock) { if (_state.CurrentRound?.Bomb != null) { _state.CurrentRound.Bomb.Status = BombStatus.Defusing; _state.CurrentRound.Bomb.Defuser = e.Player?.PlayerName; } } };

        demo.Source1GameEvents.BeginNewMatch += e =>
        { lock (_stateLock) { _state = new MatchState { Phase = MatchPhase.Live }; } };

        demo.Source1GameEvents.RoundAnnounceWarmup += e =>
        { lock (_stateLock) { _state.Phase = MatchPhase.Warmup; } };

        demo.Source1GameEvents.RoundAnnounceMatchStart += e =>
        { lock (_stateLock) { _state.Phase = MatchPhase.Live; } };

        demo.Source1GameEvents.CsIntermission += e =>
        { lock (_stateLock) { _state.Phase = MatchPhase.Halftime; } };

        demo.Source1GameEvents.CsWinPanelMatch += e =>
        { lock (_stateLock) { _state.Phase = MatchPhase.Ended; } };

        // ── Connect reader ──────────────────────────────────

        _log.LogInformation("Connecting HttpBroadcastReader to {Url}", _broadcastUrl);
        
        var httpClient = new HttpClient();
        httpClient.BaseAddress = new Uri($"http://localhost:{Environment.GetEnvironmentVariable("PORT") ?? "5000"}/");
        
        var reader = HttpBroadcastReader.Create(demo, httpClient);
        
        // CRITICAL: StartReadingAsync must be called first — it fetches /sync,
        // downloads signon data, and starts the background fragment fetch worker.
        // Without this, MoveNextAsync waits on an empty channel forever.
        _log.LogInformation("Calling StartReadingAsync...");
        await reader.StartReadingAsync(ct);
        _log.LogInformation("StartReadingAsync completed, starting MoveNextAsync loop...");

        int tickCount = 0;
        while (await reader.MoveNextAsync(ct))
        {
            tickCount++;
            if (tickCount % 500 == 0)
                _log.LogInformation("Parser running: {Ticks} ticks, {Players} players, map={Map}",
                    tickCount, _state.Players.Count, _state.MapName);
        }
        
        _log.LogInformation("Broadcast stream ended after {Ticks} ticks", tickCount);
        _activeDemo = null;
    }

    private void SnapshotPlayers(CsDemoParser demo)
    {
        try
        {
            lock (_stateLock)
            {
                _state.Players.Clear();
                foreach (var player in demo.Entities.OfType<CCSPlayerController>())
                {
                    if (player == null) continue;
                    var pawn = player.Pawn;
                    var teamStr = player.CSTeamNum switch
                    {
                        CSTeamNumber.Terrorist => "T",
                        CSTeamNumber.CounterTerrorist => "CT",
                        _ => ""
                    };
                    if (string.IsNullOrEmpty(teamStr)) continue;
                    _state.Players.Add(new PlayerState
                    {
                        SteamId = player.SteamID,
                        Name = player.PlayerName ?? "",
                        Team = teamStr,
                        Health = pawn?.Health ?? 0,
                        Armor = 0, HasHelmet = false,
                        Money = player.InGameMoneyServices?.Account ?? 0,
                        Kills = player.ActionTrackingServices?.MatchStats?.Kills ?? 0,
                        Deaths = player.ActionTrackingServices?.MatchStats?.Deaths ?? 0,
                        Assists = player.ActionTrackingServices?.MatchStats?.Assists ?? 0,
                        MVPs = player.MVPs,
                        IsAlive = (pawn?.Health ?? 0) > 0,
                        ActiveWeapon = "", HasDefuser = false
                    });
                }
            }
        }
        catch (Exception) { }
    }
}
