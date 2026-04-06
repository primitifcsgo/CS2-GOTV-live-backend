namespace GotvPlusServer.Services;

public class FragmentStore
{
    private readonly object _lock = new();
    private readonly Dictionary<int, byte[]> _startFragments = new();
    private readonly Dictionary<int, byte[]> _fullFragments = new();
    private readonly Dictionary<int, byte[]> _deltaFragments = new();
    
    // Keep the latest /start data permanently — parser needs it to initialize
    private byte[]? _latestStartData;
    
    private int _latestFragment = -1;
    private int _ticksPerSecond = 64;
    private int _protocol = 5;
    private string? _activeToken;
    private DateTime _lastReceived = DateTime.MinValue;
    
    public bool IsActive => _activeToken != null && _latestFragment >= 0
        && (DateTime.UtcNow - _lastReceived).TotalSeconds < 30;
    public int LatestFragment => _latestFragment;
    public string? ActiveToken => _activeToken;
    public DateTime LastReceived => _lastReceived;

    // ── Ingest (CS2 server POSTs here) ──────────────────────

    public void StoreStart(string token, int fragment, byte[] data, int tps)
    {
        lock (_lock)
        {
            _activeToken = token;
            _startFragments[fragment] = data;
            _latestStartData = data;  // Always keep latest
            _ticksPerSecond = tps;
            if (fragment > _latestFragment) _latestFragment = fragment;
            _lastReceived = DateTime.UtcNow;
            Cleanup(fragment);
        }
    }

    public void StoreFull(string token, int fragment, byte[] data)
    {
        lock (_lock)
        {
            _activeToken = token;
            _fullFragments[fragment] = data;
            if (fragment > _latestFragment) _latestFragment = fragment;
            _lastReceived = DateTime.UtcNow;
        }
    }

    public void StoreDelta(string token, int fragment, byte[] data)
    {
        lock (_lock)
        {
            _activeToken = token;
            _deltaFragments[fragment] = data;
            if (fragment > _latestFragment) _latestFragment = fragment;
            _lastReceived = DateTime.UtcNow;
        }
    }

    // ── Serve (HttpBroadcastReader GETs here) ───────────────

    public byte[]? GetStart(int fragment)
    {
        lock (_lock)
        {
            // Try exact match first, then fall back to latest start data.
            // CS2 sends /start once at broadcast begin — parser needs it
            // for ANY fragment to initialize the demo reader.
            return _startFragments.GetValueOrDefault(fragment) ?? _latestStartData;
        }
    }

    public byte[]? GetFull(int fragment)
    {
        lock (_lock)
        {
            return _fullFragments.GetValueOrDefault(fragment);
        }
    }

    public byte[]? GetDelta(int fragment)
    {
        lock (_lock)
        {
            return _deltaFragments.GetValueOrDefault(fragment);
        }
    }

    public string GetSyncResponse(int? requestedFragment = null)
    {
        lock (_lock)
        {
            // Pick a fragment 2 behind latest for smooth playback
            var startAt = requestedFragment ?? Math.Max(0, _latestFragment - 2);
            
            // Make sure we actually have a full fragment for it
            while (startAt < _latestFragment && !_fullFragments.ContainsKey(startAt))
                startAt++;

            // signup_fragment = earliest fragment we actually have
            var earliest = startAt;
            if (_fullFragments.Count > 0)
                earliest = _fullFragments.Keys.Min();

            return string.Join("\n", new[]
            {
                $"tick:{startAt * _ticksPerSecond * 3}",
                "rtdelay:0",
                "rcvage:0",
                $"fragment:{startAt}",
                $"signup_fragment:{earliest}",
                $"tps:{_ticksPerSecond}",
                "keyframe_interval:3",
                $"protocol:{_protocol}"
            });
        }
    }

    /// <summary>
    /// Returns sync data as an object for JSON serialization.
    /// HttpBroadcastReader uses GetFromJsonAsync and expects JSON.
    /// </summary>
    public object GetSyncJson(int? requestedFragment = null)
    {
        lock (_lock)
        {
            var startAt = requestedFragment ?? Math.Max(0, _latestFragment - 2);
            while (startAt < _latestFragment && !_fullFragments.ContainsKey(startAt))
                startAt++;

            var earliest = startAt;
            if (_fullFragments.Count > 0)
                earliest = _fullFragments.Keys.Min();

            return new
            {
                tick = startAt * _ticksPerSecond * 3,
                rtdelay = 0,
                rcvage = 0,
                fragment = startAt,
                signup_fragment = earliest,
                tps = _ticksPerSecond,
                keyframe_interval = 3,
                protocol = _protocol
            };
        }
    }

    private void Cleanup(int currentFragment)
    {
        const int keep = 60;
        var cutoff = currentFragment - keep;
        if (cutoff <= 0) return;

        // Clean up old fragments but NEVER delete _latestStartData
        foreach (var key in _startFragments.Keys.Where(k => k < cutoff).ToList())
            _startFragments.Remove(key);
        foreach (var key in _fullFragments.Keys.Where(k => k < cutoff).ToList())
            _fullFragments.Remove(key);
        foreach (var key in _deltaFragments.Keys.Where(k => k < cutoff).ToList())
            _deltaFragments.Remove(key);
    }

    public void Reset()
    {
        lock (_lock)
        {
            _startFragments.Clear();
            _fullFragments.Clear();
            _deltaFragments.Clear();
            _latestStartData = null;
            _latestFragment = -1;
            _activeToken = null;
        }
    }
}
