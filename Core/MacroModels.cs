namespace FH6OpenAssist.Core;

public enum MacroKind
{
    FarmarSp,
    Farmar200kMin,
    FarmarWheelspins,
    GastarWheelspins
}

public enum MacroRunState
{
    Parado,
    Armado,
    Executando,
    Parando,
    Falhou,
    CalibracaoNecessaria
}

public sealed record MacroDescriptor(
    MacroKind Kind,
    string Name,
    string Description,
    string ResourceSummary,
    bool IsCalibrated = true);

public sealed class CalibrationRequiredException(string message) : Exception(message);

public sealed class AutomationFaultException(string message) : Exception(message);
