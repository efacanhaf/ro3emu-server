using System.Diagnostics;

namespace Ro3Emu.Launcher;

/// <summary>Download speed over the last ~5 seconds and the remaining seconds at that speed.</summary>
internal sealed class SpeedMeter
{
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly Queue<(double At, long Bytes)> _samples = new();

    public (double Rate, double Eta) Sample(long done, long total)
    {
        double now = _clock.Elapsed.TotalSeconds;
        _samples.Enqueue((now, done));
        while (_samples.Count > 2 && now - _samples.Peek().At > 5) _samples.Dequeue();
        var first = _samples.Peek();
        double seconds = now - first.At;
        if (seconds < 1) return (0, 0);
        double rate = (done - first.Bytes) / seconds;
        return rate <= 0 ? (0, 0) : (rate, (total - done) / rate);
    }
}
