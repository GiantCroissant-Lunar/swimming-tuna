namespace SwarmAssistant.Runtime.Agents;

public sealed class PortAllocator
{
    private readonly int _min;
    private readonly int _max;
    private readonly SortedSet<int> _available;
    private readonly object _lock = new();

    public int AvailableCount
    {
        get { lock (_lock) return _available.Count; }
    }

    public PortAllocator(string range)
    {
        if (string.IsNullOrWhiteSpace(range))
            throw new ArgumentException("Port range must be in 'min-max' format.", nameof(range));

        var parts = range.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var min)
            || !int.TryParse(parts[1], out var max))
        {
            throw new ArgumentException($"Port range '{range}' must be in 'min-max' format.", nameof(range));
        }

        if (min < 0 || max > 65535 || min > max)
            throw new ArgumentOutOfRangeException(nameof(range), $"Port range {min}-{max} must be within 0-65535 and min <= max.");

        _min = min;
        _max = max;
        _available = new SortedSet<int>(Enumerable.Range(_min, _max - _min + 1));
    }

    public int Allocate()
    {
        lock (_lock)
        {
            if (_available.Count == 0)
                throw new InvalidOperationException(
                    $"No ports available in range {_min}-{_max}");

            var port = _available.Min;
            _available.Remove(port);
            return port;
        }
    }

    public void Release(int port)
    {
        lock (_lock)
        {
            if (port >= _min && port <= _max)
                _available.Add(port);
        }
    }
}
