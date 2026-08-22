using System.Drawing;
using System.Text.RegularExpressions;
using FH6OpenAssist.Core;
using FH6OpenAssist.Vision;
using FH6OpenAssist.Windows;

namespace FH6OpenAssist.Workflows;

internal sealed record SpinFarmProfile(
    MacroKind Kind,
    string Workflow,
    string VehicleName,
    string Manufacturer,
    IReadOnlyList<string> VehicleSearchTexts,
    IReadOnlyList<string> VehicleCompactNameTokens,
    IReadOnlyList<string> VehicleIdentityCompactTokens,
    IReadOnlyList<string> PostManufacturerTokens,
    string RecoveryVehicleKey,
    bool NormalizeMasteryFocusToBottomLeft,
    IReadOnlyList<GameKey?> MasteryDirections,
    IReadOnlyList<SpinMasteryTextCheck> MasteryTextChecks,
    RectangleF FinalPerkRegion,
    RectangleF FinalPerkIconRegion)
{
    private static readonly RectangleF TopRightPerkRegion = new(0.14f, 0.14f, 0.34f, 0.56f);
    private static readonly RectangleF TopRightPerkIconRegion = new(0.285f, 0.175f, 0.055f, 0.095f);
    private static readonly RectangleF RevueltoFinalPerkRegion = new(0.215f, 0.125f, 0.052f, 0.090f);

    public static SpinFarmProfile MadMike { get; } = new(
        MacroKind.FarmarWheelspins,
        "FarmarWheelspins",
        "Mad Mike 808",
        "MAZDA",
        ["MAD MIKE 808", "#123 MAD MIKE", "MAD MIKE"],
        ["MADMIKE", "MADMLKE"],
        [],
        [
            "MCLAREN",
            "MERCEDES AMG",
            "MERCEDES BENZ",
            "MEYERS",
            "MG",
            "MINI",
            "MITSUBISHI",
            "MORGAN",
            "MORRIS",
            "MOSLER",
            "NAPIER",
            "NISSAN"
        ],
        "1974-MAZDA-123-MAD-MIKE-808-WAGON-FURSTY",
        NormalizeMasteryFocusToBottomLeft: false,
        [GameKey.Right, GameKey.Right, GameKey.Up, GameKey.Up, GameKey.Up, null],
        [
            new SpinMasteryTextCheck(
                PurchaseIndex: 5,
                Alternatives:
                [
                    ["WHEELSPIN"],
                    ["MAGODAROLETA", "SUPERSORTEIO"]
                ])
        ],
        TopRightPerkRegion,
        TopRightPerkIconRegion);

    public static SpinFarmProfile Revuelto { get; } = new(
        MacroKind.FarmarWheelspinsRevuelto,
        "FarmarWheelspinsRevuelto",
        "Lamborghini Revuelto 2024",
        "LAMBORGHINI",
        ["REVUELTO", "REVUELTO 2024", "2024 LAMBORGHINI"],
        ["REVUELTO"],
        ["2024", "LAMBORGHINI"],
        [
            "LANCIA",
            "LAND ROVER",
            "LEXUS",
            "LINCOLN",
            "LOLA",
            "LOTUS",
            "LUCID",
            "LYNK & CO",
            "MASERATI",
            "MAZDA",
            "MCLAREN",
            "MERCEDES AMG",
            "MERCEDES BENZ",
            "MEYERS",
            "MG",
            "MINI",
            "MITSUBISHI",
            "MORGAN",
            "MORRIS",
            "MOSLER",
            "NAPIER",
            "NISSAN"
        ],
        "2024-LAMBORGHINI-REVUELTO",
        NormalizeMasteryFocusToBottomLeft: true,
        [GameKey.Up, GameKey.Up, GameKey.Up, GameKey.Right, GameKey.Right, null],
        [
            new SpinMasteryTextCheck(
                PurchaseIndex: 3,
                Alternatives:
                [
                    ["GOLPETRIPLO", "3SORTEIOS"]
                ]),
            new SpinMasteryTextCheck(
                PurchaseIndex: 5,
                Alternatives:
                [
                    ["SUPERSORTEIO"],
                    ["SUPERWHEELSPIN"]
                ])
        ],
        RevueltoFinalPerkRegion,
        RevueltoFinalPerkRegion);

    public SpinSettings GetSettings(AutomationSettings settings) => Kind switch
    {
        MacroKind.FarmarWheelspins => settings.Spins,
        MacroKind.FarmarWheelspinsRevuelto => settings.RevueltoSpins,
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Perfil de farm de spins inválido.")
    };

    public bool MatchesVehicleText(string text)
    {
        var compact = Compact(text);
        return VehicleCompactNameTokens.Any(token =>
            compact.Contains(token, StringComparison.Ordinal));
    }

    public bool MatchesCurrentVehicleText(string text)
    {
        var compact = Compact(text);
        return VehicleCompactNameTokens.Any(token =>
                   compact.Contains(token, StringComparison.Ordinal)) &&
               VehicleIdentityCompactTokens.All(token =>
                   compact.Contains(token, StringComparison.Ordinal));
    }

    public bool MatchesAnyTargetVehicleEvidence(string text)
    {
        var compact = Compact(text);
        var hasName = VehicleCompactNameTokens.Any(token =>
            compact.Contains(token, StringComparison.Ordinal));
        var hasCompleteIdentityWithoutName = VehicleIdentityCompactTokens.Count > 0 &&
                                             VehicleIdentityCompactTokens.All(token =>
                                                 compact.Contains(token, StringComparison.Ordinal));
        return hasName || hasCompleteIdentityWithoutName;
    }

    public bool MatchesMasteryText(int purchaseIndex, string text)
    {
        var check = MasteryTextChecks.FirstOrDefault(candidate =>
            candidate.PurchaseIndex == purchaseIndex);
        if (check is null)
        {
            return true;
        }

        var compact = Compact(text);
        return check.Alternatives.Any(alternative =>
            alternative.All(token => compact.Contains(token, StringComparison.Ordinal)));
    }

    public bool MatchesFinalPerkText(string text) =>
        MatchesMasteryText(MasteryDirections.Count - 1, text);

    public string? RecognizeFocusedManufacturer(string text)
    {
        var normalized = GameVisionService.Normalize(text);
        if (ContainsManufacturer(normalized, Manufacturer))
        {
            return Manufacturer;
        }

        return PostManufacturerTokens.FirstOrDefault(candidate =>
            ContainsManufacturer(normalized, candidate));
    }

    private static bool ContainsManufacturer(string normalizedText, string manufacturer)
    {
        var normalizedManufacturer = GameVisionService.Normalize(manufacturer);
        return Regex.IsMatch(
            normalizedText,
            $@"\b{Regex.Escape(normalizedManufacturer).Replace("\\ ", @"\s+")}\b",
            RegexOptions.CultureInvariant);
    }

    private static string Compact(string text) =>
        Regex.Replace(
            GameVisionService.Normalize(text),
            @"[^A-Z0-9]",
            string.Empty,
            RegexOptions.CultureInvariant);
}

internal sealed record SpinMasteryTextCheck(
    int PurchaseIndex,
    IReadOnlyList<IReadOnlyList<string>> Alternatives);
