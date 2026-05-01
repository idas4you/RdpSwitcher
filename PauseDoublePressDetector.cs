namespace RdpSwitcher;

internal sealed class PauseDoublePressDetector
{
    private const long DuplicatePressWindowMilliseconds = 80;
    private const long DoublePressWindowMilliseconds = 700;

    private long _lastObservedPressTick = -1;
    private long _lastPressTick = -1;

    public bool RegisterPress()
    {
        var now = Environment.TickCount64;
        if (_lastObservedPressTick >= 0 && now - _lastObservedPressTick <= DuplicatePressWindowMilliseconds)
        {
            return false;
        }

        _lastObservedPressTick = now;

        if (_lastPressTick >= 0 && now - _lastPressTick <= DoublePressWindowMilliseconds)
        {
            _lastPressTick = -1;
            return true;
        }

        _lastPressTick = now;
        return false;
    }
}
