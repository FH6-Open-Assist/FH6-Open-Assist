using System.Collections.ObjectModel;

namespace FH6OpenAssist.Core;

public sealed record BotDefinition(
    MacroKind Kind,
    string Name,
    string Description,
    string ResourceSummary,
    IReadOnlyList<string> Requirements,
    string StartContext,
    bool SupportsBackground,
    bool RequiresViGEm,
    bool Experimental);

public static class BotCatalog
{
    private static readonly ReadOnlyCollection<BotDefinition> Definitions = Array.AsReadOnly(
    [
        new BotDefinition(
            MacroKind.FarmarSp,
            "Skill Points",
            "Farma pontos de habilidade com corrida e repetição assistida.",
            "SP",
            Requirements(
                "Subaru Impreza 22B-STI Version selecionado",
                "Árvore de habilidades desbloqueada é recomendada",
                "Todas as assistências ativadas",
                "Jogo aberto e renderizando, sem estar minimizado"),
            "Na rua, fora da garagem.",
            SupportsBackground: true,
            RequiresViGEm: false,
            Experimental: false),
        new BotDefinition(
            MacroKind.Farmar200kMin,
            "Farm de CR",
            "Executa a rota calibrada para ganho recorrente de créditos.",
            "Créditos",
            Requirements(
                "Nissan S-Cargo S1 800 selecionado",
                "Carro sem tunagem",
                "Dificuldade Imbatível",
                "Todas as assistências desativadas",
                "ViGEmBus instalado e conectado"),
            "Na rua, fora da garagem.",
            SupportsBackground: true,
            RequiresViGEm: true,
            Experimental: false),
        new BotDefinition(
            MacroKind.FarmarWheelspins,
            "WheelSpin Mad Mike",
            "Compra, melhora e gira carros pela sequência Mad Mike.",
            "SP e créditos",
            Requirements(
                "Conta VIP",
                "Pelo menos 100.000 CR disponíveis por ciclo",
                "Pelo menos 30 SP disponíveis por ciclo",
                "Aceitar que uma cópia compatível do Mad Mike pode ser removida"),
            "Na garagem, com o menu Campanha aberto.",
            SupportsBackground: true,
            RequiresViGEm: false,
            Experimental: false),
        new BotDefinition(
            MacroKind.GastarWheelspins,
            "Gastar Wheelspins",
            "Gira Super Wheelspins e Wheelspins com confirmação visual.",
            "Wheelspins",
            Requirements(
                "Interface do jogo em português do Brasil",
                "Pacote de OCR em português disponível no Windows",
                "Wheelspins ou Super Wheelspins disponíveis para realizar giros",
                "Aceitar que carros duplicados serão mantidos"),
            "Na rua, no menu de pausa ou em uma tela de Wheelspin.",
            SupportsBackground: true,
            RequiresViGEm: false,
            Experimental: false)
    ]);

    private static readonly IReadOnlyDictionary<MacroKind, BotDefinition> DefinitionsByKind =
        new ReadOnlyDictionary<MacroKind, BotDefinition>(
            Definitions.ToDictionary(definition => definition.Kind));

    public static IReadOnlyList<BotDefinition> All => Definitions;

    public static BotDefinition Get(MacroKind kind)
    {
        if (DefinitionsByKind.TryGetValue(kind, out var definition))
        {
            return definition;
        }

        throw new ArgumentOutOfRangeException(nameof(kind), kind, "BOT não cadastrado.");
    }

    public static bool TryGet(MacroKind kind, out BotDefinition? definition) =>
        DefinitionsByKind.TryGetValue(kind, out definition);

    private static IReadOnlyList<string> Requirements(params string[] requirements) =>
        Array.AsReadOnly(requirements);
}
