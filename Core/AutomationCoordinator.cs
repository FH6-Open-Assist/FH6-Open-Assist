using FH6OpenAssist.Windows;

namespace FH6OpenAssist.Core;

public sealed class AutomationCoordinator : IAsyncDisposable
{
    private readonly IReadOnlyDictionary<MacroKind, IMacroWorkflow> _workflows;
    private readonly AutomationContext _context;
    private readonly AutomationLogger _logger;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private CancellationTokenSource? _cancellation;
    private Task? _activeTask;

    public MacroKind? SelectedMacro { get; private set; }
    public MacroRunState State { get; private set; } = MacroRunState.Parado;

    public event Action<MacroRunState, MacroKind?, string>? StateChanged;

    public AutomationCoordinator(
        IEnumerable<IMacroWorkflow> workflows,
        AutomationContext context,
        AutomationLogger logger)
    {
        _workflows = workflows.ToDictionary(workflow => workflow.Kind);
        _context = context;
        _logger = logger;
        _context.RunNestedAsync = RunNestedAsync;
    }

    public void Select(MacroKind kind)
    {
        SelectedMacro = kind;
        if (State is MacroRunState.Parado or MacroRunState.Armado or MacroRunState.Falhou or MacroRunState.CalibracaoNecessaria)
        {
            ChangeState(MacroRunState.Parado, $"{DisplayName(kind)} selecionado. Clique em Ativar BOT para armar.");
        }
    }

    public void ArmSelected()
    {
        if (SelectedMacro is null)
        {
            _logger.Warn("Selecione um bot antes de ativar.");
            return;
        }

        if (State is MacroRunState.Executando or MacroRunState.Parando)
        {
            _logger.Warn("Não é possível trocar a ativação durante uma execução.");
            return;
        }

        ChangeState(MacroRunState.Armado, $"{DisplayName(SelectedMacro.Value)} ativo. F8 inicia; F9 encerra.");
    }

    public async Task StartSelectedAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            if (State == MacroRunState.Executando)
            {
                _logger.Warn("Já existe um macro em execução.");
                return;
            }

            if (SelectedMacro is null || State != MacroRunState.Armado)
            {
                _logger.Warn("Selecione um bot e clique em Ativar BOT antes de pressionar F8.");
                return;
            }

            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            var request = new MacroRunRequest(SelectedMacro.Value);
            ChangeState(MacroRunState.Executando, $"Iniciando {DisplayName(request.Kind)}.");
            _activeTask = ExecuteRootAsync(request, _cancellation.Token);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync()
    {
        Task? task;
        await _lifecycleLock.WaitAsync();
        try
        {
            if (_activeTask is null || _activeTask.IsCompleted)
            {
                await _context.Input.ReleaseAllAsync();
                await _context.Capture.ReleaseSessionAsync();
                ChangeState(SelectedMacro is null ? MacroRunState.Parado : MacroRunState.Armado, "Nenhum macro está executando.");
                return;
            }

            ChangeState(MacroRunState.Parando, "F8 recebido: pausando e soltando todas as teclas.");
            _cancellation?.Cancel();
            task = _activeTask;
        }
        finally
        {
            _lifecycleLock.Release();
        }

        await _context.Input.ReleaseAllAsync();
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Cancelamento solicitado pelo usuário.
        }
    }

    public async Task EndAsync()
    {
        await StopAsync();
        await _lifecycleLock.WaitAsync();
        try
        {
            ChangeState(MacroRunState.Parado, "BOT encerrado com F9. Clique em Ativar BOT para usar novamente.");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task ExecuteRootAsync(MacroRunRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteWorkflowAsync(request, cancellationToken);
            ChangeState(MacroRunState.Armado, $"{DisplayName(request.Kind)} finalizado.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.Info("Macro cancelado pelo usuário.");
            ChangeState(MacroRunState.Armado, "Macro interrompido com segurança.");
        }
        catch (CalibrationRequiredException exception)
        {
            _logger.Error($"CALIBRAÇÃO NECESSÁRIA: {exception.Message}");
            ChangeState(MacroRunState.CalibracaoNecessaria, exception.Message);
        }
        catch (Exception exception)
        {
            _logger.Error($"Falha segura: {exception.Message}");
            ChangeState(MacroRunState.Falhou, exception.Message);
        }
        finally
        {
            await _context.Input.ReleaseAllAsync();
            await _context.Capture.ReleaseSessionAsync();
        }
    }

    private Task RunNestedAsync(MacroRunRequest request, CancellationToken cancellationToken)
    {
        if (!request.Nested)
        {
            request = request with { Nested = true };
        }

        _logger.Info($"Encadeando {DisplayName(request.Kind)} e preservando o retorno ao macro chamador.");
        return ExecuteWorkflowAsync(request, cancellationToken);
    }

    private Task ExecuteWorkflowAsync(MacroRunRequest request, CancellationToken cancellationToken)
    {
        if (!_workflows.TryGetValue(request.Kind, out var workflow))
        {
            throw new AutomationFaultException($"Workflow não registrado: {request.Kind}.");
        }

        return workflow.RunAsync(_context, request, cancellationToken);
    }

    private void ChangeState(MacroRunState state, string message)
    {
        State = state;
        _logger.Info(message);
        StateChanged?.Invoke(state, SelectedMacro, message);
    }

    private static string DisplayName(MacroKind kind) => kind switch
    {
        MacroKind.FarmarSp => "Skill Points",
        MacroKind.Farmar200kMin => "Farm de CR",
        MacroKind.FarmarWheelspins => "WheelSpin Mad Mike",
        MacroKind.GastarWheelspins => "Gastar Wheelspins",
        _ => kind.ToString()
    };

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cancellation?.Dispose();
        _lifecycleLock.Dispose();
    }
}

