namespace SwarmAssistant.Runtime.Agents;

public sealed class PortAllocator
{
    private readonly int _min;
    private readonly int _max;
    private readonly SortedSet<int> _available;
    private readonly object _lock = new();

    public PortAllocator(string range)
    {
        var parts = range.Split('-');
        _min = int.Parse(parts[0]);
        _max = int.Parse(parts[1]);
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
