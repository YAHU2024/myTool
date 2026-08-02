namespace QuickTranslate.Core;

public sealed class RecentLookupBuffer
{
    private readonly object _sync = new();
    private readonly int _capacity;
    private readonly List<string> _items = new();

    public RecentLookupBuffer(int capacity = 5)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public IReadOnlyList<string> Items
    {
        get { lock (_sync) return _items.ToArray(); }
    }

    public void AddSuccessful(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var value = query.Trim();
        lock (_sync)
        {
            _items.RemoveAll(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));
            _items.Insert(0, value);
            if (_items.Count > _capacity)
                _items.RemoveRange(_capacity, _items.Count - _capacity);
        }
    }
}
