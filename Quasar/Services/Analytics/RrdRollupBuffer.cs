namespace Quasar.Services.Analytics;

public sealed class RrdRollupBuffer
{
    private readonly object _sync = new();
    private readonly RrdCircularBuffer _buffer;
    private readonly int _rollupIntervalSeconds;

    private bool _hasWindow;
    private long _windowStartUnixSeconds;
    private int _sampleCount;
    private float _sumSimSpeed;
    private float _sumCpuPercent;
    private float _sumMemoryMb;
    private float _sumFrameTimeMs;
    private int _maxPlayersOnline;
    private int _maxUsedPcu;
    private long _sumActiveGridCount;
    private int _activeGridCountSamples;
    private long _sumActiveEntityCount;
    private int _activeEntityCountSamples;

    public RrdRollupBuffer(int capacity, int rollupIntervalSeconds)
    {
        if (rollupIntervalSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(rollupIntervalSeconds));

        _buffer = new RrdCircularBuffer(capacity);
        _rollupIntervalSeconds = rollupIntervalSeconds;
    }

    public int RollupIntervalSeconds => _rollupIntervalSeconds;

    public void Observe(in MetricSample sample)
    {
        var windowStart = sample.TimestampUnixSeconds - (sample.TimestampUnixSeconds % _rollupIntervalSeconds);

        lock (_sync)
        {
            if (!_hasWindow)
            {
                StartWindow(windowStart);
                Accumulate(sample);
                return;
            }

            if (windowStart < _windowStartUnixSeconds)
                return;

            if (windowStart != _windowStartUnixSeconds)
            {
                FlushWindow();
                StartWindow(windowStart);
            }

            Accumulate(sample);
        }
    }

    public MetricSample[] Read(long fromUnixSeconds, long toUnixSeconds) => _buffer.Read(fromUnixSeconds, toUnixSeconds);

    public MetricSample[] ReadLatest(int n) => _buffer.ReadLatest(n);

    public MetricSample[] ReadAll() => _buffer.ReadAll();

    public void ReplaceAll(IReadOnlyList<MetricSample> samples)
    {
        _buffer.ReplaceAll(samples);

        lock (_sync)
        {
            ResetAccumulator();
        }
    }

    private void StartWindow(long windowStartUnixSeconds)
    {
        ResetAccumulator();
        _hasWindow = true;
        _windowStartUnixSeconds = windowStartUnixSeconds;
    }

    private void Accumulate(in MetricSample sample)
    {
        _sampleCount++;
        _sumSimSpeed += sample.SimSpeed;
        _sumCpuPercent += sample.CpuPercent;
        _sumMemoryMb += sample.MemoryMb;
        _sumFrameTimeMs += sample.FrameTimeMs;
        _maxPlayersOnline = Math.Max(_maxPlayersOnline, sample.PlayersOnline);
        _maxUsedPcu = Math.Max(_maxUsedPcu, sample.UsedPcu);

        if (sample.ActiveGridCount >= 0)
        {
            _sumActiveGridCount += sample.ActiveGridCount;
            _activeGridCountSamples++;
        }

        if (sample.ActiveEntityCount >= 0)
        {
            _sumActiveEntityCount += sample.ActiveEntityCount;
            _activeEntityCountSamples++;
        }
    }

    private void FlushWindow()
    {
        if (!_hasWindow || _sampleCount == 0)
            return;

        var consolidated = new MetricSample(
            timestampUnixSeconds: _windowStartUnixSeconds,
            simSpeed: _sumSimSpeed / _sampleCount,
            cpuPercent: _sumCpuPercent / _sampleCount,
            memoryMb: _sumMemoryMb / _sampleCount,
            frameTimeMs: _sumFrameTimeMs / _sampleCount,
            playersOnline: _maxPlayersOnline,
            usedPcu: _maxUsedPcu,
            activeGridCount: _activeGridCountSamples > 0 ? (int)MathF.Round((float)_sumActiveGridCount / _activeGridCountSamples) : -1,
            activeEntityCount: _activeEntityCountSamples > 0 ? (int)MathF.Round((float)_sumActiveEntityCount / _activeEntityCountSamples) : -1);

        _buffer.Push(consolidated);
    }

    private void ResetAccumulator()
    {
        _hasWindow = false;
        _windowStartUnixSeconds = 0;
        _sampleCount = 0;
        _sumSimSpeed = 0f;
        _sumCpuPercent = 0f;
        _sumMemoryMb = 0f;
        _sumFrameTimeMs = 0f;
        _maxPlayersOnline = 0;
        _maxUsedPcu = 0;
        _sumActiveGridCount = 0;
        _activeGridCountSamples = 0;
        _sumActiveEntityCount = 0;
        _activeEntityCountSamples = 0;
    }
}
