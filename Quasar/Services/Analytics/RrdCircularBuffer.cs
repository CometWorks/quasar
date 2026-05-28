namespace Quasar.Services.Analytics;

public sealed class RrdCircularBuffer
{
    private readonly object _sync = new();
    private readonly MetricSample[] _slots;
    private int _head;
    private int _count;

    public RrdCircularBuffer(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _slots = new MetricSample[capacity];
    }

    public int Capacity => _slots.Length;

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    public bool Push(in MetricSample sample)
    {
        lock (_sync)
        {
            if (_count > 0)
            {
                var latest = GetAt(_count - 1);
                if (sample.TimestampUnixSeconds <= latest.TimestampUnixSeconds)
                    return false;
            }

            if (_count < _slots.Length)
            {
                _slots[(_head + _count) % _slots.Length] = sample;
                _count++;
                return true;
            }

            _slots[_head] = sample;
            _head = (_head + 1) % _slots.Length;
            return true;
        }
    }

    public MetricSample[] Read(long fromUnixSeconds, long toUnixSeconds)
    {
        if (toUnixSeconds < fromUnixSeconds)
            return [];

        lock (_sync)
        {
            if (_count == 0)
                return [];

            var result = new List<MetricSample>();
            for (var i = 0; i < _count; i++)
            {
                var sample = GetAt(i);
                if (sample.TimestampUnixSeconds < fromUnixSeconds)
                    continue;

                if (sample.TimestampUnixSeconds > toUnixSeconds)
                    break;

                result.Add(sample);
            }

            return result.ToArray();
        }
    }

    public MetricSample[] ReadLatest(int n)
    {
        if (n <= 0)
            return [];

        lock (_sync)
        {
            if (_count == 0)
                return [];

            var take = Math.Min(n, _count);
            var result = new MetricSample[take];
            var start = _count - take;
            for (var i = 0; i < take; i++)
                result[i] = GetAt(start + i);

            return result;
        }
    }

    public MetricSample[] ReadAll()
    {
        lock (_sync)
        {
            if (_count == 0)
                return [];

            var result = new MetricSample[_count];
            for (var i = 0; i < _count; i++)
                result[i] = GetAt(i);

            return result;
        }
    }

    public void ReplaceAll(IReadOnlyList<MetricSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        lock (_sync)
        {
            _head = 0;
            _count = 0;

            var start = Math.Max(0, samples.Count - _slots.Length);
            for (var i = start; i < samples.Count; i++)
            {
                var sample = samples[i];
                if (_count > 0)
                {
                    var latest = GetAt(_count - 1);
                    if (sample.TimestampUnixSeconds <= latest.TimestampUnixSeconds)
                        continue;
                }

                _slots[_count] = sample;
                _count++;
            }
        }
    }

    private MetricSample GetAt(int offset)
    {
        return _slots[(_head + offset) % _slots.Length];
    }
}
