using System.Text.Json.Serialization;

namespace GotvPlusServer.Models;

// ────────────────────────────────────────────────────────────
//  Enums (serialized as integers to match plugin output)
// ────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MatchPhase { Idle, Warmup, KnifeRound, Live, Halftime, Overtime, Ended }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BombStatus { Carried, Dropped, Planted, Defusing, Defused, Exploded }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WinReason { CTWinElimination, CTWinDefuse, CTWinTime, TWinElimination, TWinBombExplode }

// ────────────────────────────────────────────────────────────
//  Match State  — top-level object served at GET /state
// ────────────────────────────────────────────────────────────

public class MatchState
{
    public string MapName { get; set; } = "";
    public MatchPhase Phase { get; set; } = MatchPhase.Idle;
    public int RoundNumber { get; set; } = 0;
    public int MaxRounds { get; set; } = 24;  // MR12
    public TeamState CT { get; set; } = new();
    public TeamState T { get; set; } = new();
    public List<PlayerState> Players { get; set; } = new();
    public RoundState? CurrentRound { get; set; }
    public List<RoundHistoryEntry> RoundHistory { get; set; } = new();
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

// ────────────────────────────────────────────────────────────
//  Team
// ────────────────────────────────────────────────────────────

public class TeamState
{
    public string Name { get; set; } = "";
    public int Score { get; set; } = 0;
    public int TimeoutsRemaining { get; set; } = 0;
}

// ────────────────────────────────────────────────────────────
//  Player
// ────────────────────────────────────────────────────────────

public class PlayerState
{
    public ulong SteamId { get; set; }
    public string Name { get; set; } = "";
    public string Team { get; set; } = "";  // "CT" or "T"
    public int Health { get; set; }
    public int Armor { get; set; }
    public bool HasHelmet { get; set; }
    public int Money { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public int MVPs { get; set; }
    public bool IsAlive { get; set; }
    public string ActiveWeapon { get; set; } = "";
    public bool HasDefuser { get; set; }
}

// ────────────────────────────────────────────────────────────
//  Round state + events
// ────────────────────────────────────────────────────────────

public class RoundState
{
    public int Number { get; set; }
    public string Phase { get; set; } = "live";
    public BombInfo? Bomb { get; set; }
    public List<KillEvent> Kills { get; set; } = new();
}

public class BombInfo
{
    public BombStatus Status { get; set; } = BombStatus.Carried;
    public string? Carrier { get; set; }
    public string? Planter { get; set; }
    public string? Defuser { get; set; }
    public string? Site { get; set; }
    public double? PlantedAt { get; set; }
}

public class KillEvent
{
    public string Attacker { get; set; } = "";
    public string Victim { get; set; } = "";
    public string Weapon { get; set; } = "";
    public bool Headshot { get; set; }
    public bool ThroughSmoke { get; set; }
    public bool NoScope { get; set; }
    public bool Wallbang { get; set; }
    public int AttackerFlashed { get; set; }
    public double Timestamp { get; set; }
}

// ────────────────────────────────────────────────────────────
//  Round history
// ────────────────────────────────────────────────────────────

public class RoundHistoryEntry
{
    public int Round { get; set; }
    public string Winner { get; set; } = "";   // "CT" or "T"
    public WinReason Reason { get; set; }
    public int CTScore { get; set; }
    public int TScore { get; set; }
}
