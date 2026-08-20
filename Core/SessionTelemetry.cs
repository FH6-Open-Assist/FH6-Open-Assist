namespace FH6OpenAssist.Core;

public sealed record SessionTelemetrySnapshot(
    MacroKind? Bot,
    MacroRunState Status,
    string Stage,
    string Description,
    DateTimeOffset? StartedAt,
    TimeSpan Elapsed,
    int Cycles,
    int Recoveries,
    int Failures);

public sealed class SessionTelemetry
{
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private MacroKind? _bot;
    private MacroRunState _status = MacroRunState.Parado;
    private string _stage = "Aguardando";
    private string _description = "Selecione um BOT para começar.";
    private DateTimeOffset? _startedAt;
    private TimeSpan _elapsed;
    private long? _runningSinceTimestamp;
    private int _cycles;
    private int _recoveries;
    private int _failures;

    public SessionTelemetry(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event Action<SessionTelemetrySnapshot>? Changed;

    public SessionTelemetrySnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return CreateSnapshotUnsafe();
            }
        }
    }

    public SessionTelemetrySnapshot Begin(
        MacroKind bot,
        string stage = "Iniciando",
        string description = "Preparando a automação.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        SessionTelemetrySnapshot snapshot;
        lock (_sync)
        {
            _bot = bot;
            _status = MacroRunState.Executando;
            _stage = stage;
            _description = description;
            _startedAt = _timeProvider.GetUtcNow();
            _elapsed = TimeSpan.Zero;
            _runningSinceTimestamp = _timeProvider.GetTimestamp();
            _cycles = 0;
            _recoveries = 0;
            _failures = 0;
            snapshot = CreateSnapshotUnsafe();
        }

        return Publish(snapshot);
    }

    public SessionTelemetrySnapshot UpdateStage(string stage, string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);

        SessionTelemetrySnapshot snapshot;
        lock (_sync)
        {
            _stage = stage;
            if (!string.IsNullOrWhiteSpace(description))
            {
                _description = description;
            }

            snapshot = CreateSnapshotUnsafe();
        }

        return Publish(snapshot);
    }

    public SessionTelemetrySnapshot UpdateStatus(
        MacroRunState status,
        string stage,
        string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        if (status is not (MacroRunState.Executando or MacroRunState.Parando))
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "UpdateStatus aceita apenas estados transitórios de execução.");
        }

        SessionTelemetrySnapshot snapshot;
        lock (_sync)
        {
            _status = status;
            _stage = stage;
            if (!string.IsNullOrWhiteSpace(description))
            {
                _description = description;
            }

            snapshot = CreateSnapshotUnsafe();
        }

        return Publish(snapshot);
    }

    public SessionTelemetrySnapshot CycleCompleted(string? description = null)
    {
        SessionTelemetrySnapshot snapshot;
        lock (_sync)
        {
            _cycles++;
            _stage = "Ciclo concluído";
            _description = string.IsNullOrWhiteSpace(description)
                ? $"{_cycles} ciclo(s) concluído(s) nesta sessão."
                : description;
            snapshot = CreateSnapshotUnsafe();
        }

        return Publish(snapshot);
    }

    public SessionTelemetrySnapshot Recovery(string? description = null)
    {
        SessionTelemetrySnapshot snapshot;
        lock (_sync)
        {
            _recoveries++;
            _stage = "Recuperação";
            _description = string.IsNullOrWhiteSpace(description)
                ? $"Recuperação {_recoveries} iniciada."
                : description;
            snapshot = CreateSnapshotUnsafe();
        }

        return Publish(snapshot);
    }

    public SessionTelemetrySnapshot Failure(
        string description,
        MacroRunState finalState = MacroRunState.Falhou,
        string stage = "Falha segura")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        if (finalState is not (MacroRunState.Falhou or MacroRunState.CalibracaoNecessaria))
        {
            throw new ArgumentOutOfRangeException(
                nameof(finalState),
                finalState,
                "Uma falha deve terminar como Falhou ou CalibracaoNecessaria.");
        }

        SessionTelemetrySnapshot snapshot;
        lock (_sync)
        {
            FreezeElapsedUnsafe();
            _failures++;
            _status = finalState;
            _stage = stage;
            _description = description;
            snapshot = CreateSnapshotUnsafe();
        }

        return Publish(snapshot);
    }

    public SessionTelemetrySnapshot Stop(
        MacroRunState finalState,
        string stage,
        string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        if (finalState is MacroRunState.Executando or MacroRunState.Parando)
        {
            throw new ArgumentOutOfRangeException(
                nameof(finalState),
                finalState,
                "Stop exige um estado final que não esteja em execução.");
        }

        SessionTelemetrySnapshot snapshot;
        lock (_sync)
        {
            FreezeElapsedUnsafe();
            _status = finalState;
            _stage = stage;
            if (!string.IsNullOrWhiteSpace(description))
            {
                _description = description;
            }

            snapshot = CreateSnapshotUnsafe();
        }

        return Publish(snapshot);
    }

    public SessionTelemetrySnapshot Reset()
    {
        SessionTelemetrySnapshot snapshot;
        lock (_sync)
        {
            _bot = null;
            _status = MacroRunState.Parado;
            _stage = "Aguardando";
            _description = "Selecione um BOT para começar.";
            _startedAt = null;
            _elapsed = TimeSpan.Zero;
            _runningSinceTimestamp = null;
            _cycles = 0;
            _recoveries = 0;
            _failures = 0;
            snapshot = CreateSnapshotUnsafe();
        }

        return Publish(snapshot);
    }

    private SessionTelemetrySnapshot CreateSnapshotUnsafe()
    {
        var elapsed = _elapsed;
        if (_runningSinceTimestamp is long runningSince)
        {
            elapsed += _timeProvider.GetElapsedTime(runningSince, _timeProvider.GetTimestamp());
        }

        return new SessionTelemetrySnapshot(
            _bot,
            _status,
            _stage,
            _description,
            _startedAt,
            elapsed,
            _cycles,
            _recoveries,
            _failures);
    }

    private void FreezeElapsedUnsafe()
    {
        if (_runningSinceTimestamp is not long runningSince)
        {
            return;
        }

        _elapsed += _timeProvider.GetElapsedTime(runningSince, _timeProvider.GetTimestamp());
        _runningSinceTimestamp = null;
    }

    private SessionTelemetrySnapshot Publish(SessionTelemetrySnapshot snapshot)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return snapshot;
        }

        foreach (Action<SessionTelemetrySnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(snapshot);
            }
            catch
            {
                // Telemetria é best-effort e nunca deve interromper a automação.
            }
        }

        return snapshot;
    }
}
