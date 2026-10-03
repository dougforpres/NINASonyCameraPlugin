using System;
using System.Threading;

internal sealed class ManualTimeProvider : TimeProvider {
    private long ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref ticks);
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
    internal void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
}
