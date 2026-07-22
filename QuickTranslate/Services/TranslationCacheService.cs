using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuickTranslate.Models;

namespace QuickTranslate.Services;

/// <summary>
/// Small, process-local cache for completed translation requests.
/// </summary>
public sealed class TranslationCacheService
{
    private const int CacheKeyVersion = 1;
    private readonly object _sync = new();
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _timeProvider;
    private long _accessSequence;
    private long _hits;
    private long _misses;

    public TranslationCacheService(
        int capacity = 128,
        TimeSpan? ttl = null,
        TimeProvider? timeProvider = null)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _capacity = capacity;
        _ttl = ttl ?? TimeSpan.FromMinutes(30);
        if (_ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl));

        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public long Hits
    {
        get { lock (_sync) return _hits; }
    }

    public long Misses
    {
        get { lock (_sync) return _misses; }
    }

    public double HitRate
    {
        get
        {
            lock (_sync)
            {
                var total = _hits + _misses;
                return total == 0 ? 0 : (double)_hits / total;
            }
        }
    }

    public bool TryGet(TranslationRequest request, out string result)
    {
        var key = CreateKey(request);
        var now = _timeProvider.GetUtcNow();

        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresAt > now)
                {
                    entry.LastAccessSequence = ++_accessSequence;
                    _hits++;
                    result = entry.Result;
                    return true;
                }

                _entries.Remove(key);
            }

            _misses++;
            result = string.Empty;
            return false;
        }
    }

    public void Set(TranslationRequest request, string result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return;

        var key = CreateKey(request);
        var now = _timeProvider.GetUtcNow();

        lock (_sync)
        {
            RemoveExpired(now);

            if (!_entries.ContainsKey(key) && _entries.Count >= _capacity)
            {
                var leastRecentlyUsed = _entries.MinBy(pair => pair.Value.LastAccessSequence);
                _entries.Remove(leastRecentlyUsed.Key);
            }

            _entries[key] = new CacheEntry(
                result,
                now + _ttl,
                ++_accessSequence);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
        }
    }

    internal static string CreateKey(TranslationRequest request)
    {
        var semanticRequest = new
        {
            Version = CacheKeyVersion,
            Kind = request.Kind,
            Text = request.Text,
            TargetLanguage = request.TargetLanguage,
            ContentType = request.ContentType,
            ApiBaseUrl = request.ApiBaseUrl.TrimEnd('/'),
            request.ModelName,
            request.SystemPrompt
        };

        var json = JsonSerializer.Serialize(semanticRequest);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        if (_entries.Count == 0)
            return;

        var expiredKeys = _entries
            .Where(pair => pair.Value.ExpiresAt <= now)
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var key in expiredKeys)
            _entries.Remove(key);
    }

    private sealed class CacheEntry
    {
        public CacheEntry(string result, DateTimeOffset expiresAt, long lastAccessSequence)
        {
            Result = result;
            ExpiresAt = expiresAt;
            LastAccessSequence = lastAccessSequence;
        }

        public string Result { get; }
        public DateTimeOffset ExpiresAt { get; }
        public long LastAccessSequence { get; set; }
    }
}
