using ForzaFarm.Core;

namespace ForzaFarm.Workflows;

public sealed class SpendSpinsWorkflow : IMacroWorkflow
{
    public MacroKind Kind => MacroKind.GastarWheelspins;

    public Task RunAsync(
        AutomationContext context,
        MacroRunRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        context.Logger.State(
            "GastarWheelspins",
            "AguardandoCalibracao",
            "Nenhuma entrada foi enviada: o fluxo de giro, tratamento de prêmio e saldo zero ainda não foi fornecido.");
        throw new CalibrationRequiredException(
            "Gastar Wheelspins está armado com segurança, mas precisa das telas/regras de: abrir o menu, iniciar e finalizar o giro, " +
            "tratar carros duplicados, priorizar Wheelspin/Super Wheelspin e detectar saldo zero.");
    }
}
