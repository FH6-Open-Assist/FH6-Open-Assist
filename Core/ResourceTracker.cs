namespace FH6OpenAssist.Core;

public sealed record ResourceSnapshot(
    int? SkillPoints,
    bool SkillPointsEstimated,
    long? Credits,
    bool CreditsEstimated,
    DateTimeOffset UpdatedAt);

public sealed class ResourceTracker
{
    private readonly object _sync = new();
    private ResourceSnapshot _snapshot = new(null, false, null, false, DateTimeOffset.Now);

    public event Action<ResourceSnapshot>? Changed;

    public ResourceSnapshot Current
    {
        get
        {
            lock (_sync)
            {
                return _snapshot;
            }
        }
    }

    public void SetSkillPoints(int value, bool estimated)
    {
        ResourceSnapshot snapshot;
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                SkillPoints = Math.Clamp(value, 0, 999),
                SkillPointsEstimated = estimated,
                UpdatedAt = DateTimeOffset.Now
            };
            snapshot = _snapshot;
        }

        Changed?.Invoke(snapshot);
    }

    public void SetCredits(long value, bool estimated)
    {
        ResourceSnapshot snapshot;
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                Credits = Math.Max(0, value),
                CreditsEstimated = estimated,
                UpdatedAt = DateTimeOffset.Now
            };
            snapshot = _snapshot;
        }

        Changed?.Invoke(snapshot);
    }

    public void AdjustCredits(long delta)
    {
        var current = Current;
        if (current.Credits is { } credits)
        {
            SetCredits(credits + delta, current.CreditsEstimated);
        }
    }
}
