namespace FH6OpenAssist.Core;

public enum MacroKind
{
    FarmarSp,
    Farmar200kMin,
    FarmarWheelspins,
    GastarWheelspins,
    FarmarWheelspinsRevuelto
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

public sealed class CalibrationRequiredException(string message) : Exception(message);

public sealed class AutomationFaultException(string message) : Exception(message);
