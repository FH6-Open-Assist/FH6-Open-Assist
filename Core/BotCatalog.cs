using System.Collections.ObjectModel;

namespace FH6OpenAssist.Core;

public enum BotRequirementKind
{
    Automated,
    Required,
    Advisory
}

public sealed record BotRequirement(string Text, BotRequirementKind Kind);

public sealed record BotDefinition(
    MacroKind Kind,
    string Name,
    string Description,
    string ResourceSummary,
    IReadOnlyList<BotRequirement> Requirements,
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
                Automated("Subaru Impreza 22B-STI Version: o BOT seleciona se necessário"),
                Advisory("Árvore de habilidades desbloqueada é recomendada"),
                Advisory("Todas as assistências devem estar ativadas"),
                Required("Jogo aberto e renderizando, sem estar minimizado")),
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
                Automated("Nissan S-Cargo exatamente S1 800: o BOT seleciona se necessário"),
                Advisory("Carro sem tunagem"),
                Advisory("Dificuldade Imbatível"),
                Advisory("Todas as assistências devem estar desativadas"),
                Required("ViGEmBus instalado e conectado")),
            "Na rua, fora da garagem.",
            SupportsBackground: true,
            RequiresViGEm: true,
            Experimental: false),
        new BotDefinition(
            MacroKind.FarmarWheelspins,
            "WheelSpin Mad Mike",
            "Compra o Mad Mike, libera o Wheelspin da Maestria e prepara o próximo ciclo.",
            "SP e créditos",
            Requirements(
                Required("Conta VIP"),
                Automated("SP: ao faltar para outro ciclo, o BOT farma até 999 e relê o saldo"),
                Automated("CR: ao faltar para outro ciclo, o BOT farma até 10.000.000 e relê o saldo"),
                Automated("Subaru 22B e Nissan S-Cargo S1 800 são selecionados nos reabastecimentos"),
                Advisory("Assistências permanecem pré-requisitos informados pelos BOTs SP e CR"),
                Required("Aceitar que uma cópia compatível do Mad Mike será removida ao fim de cada ciclo")),
            "Na rua, no menu de pausa ou na garagem; o BOT normaliza a entrada.",
            SupportsBackground: true,
            RequiresViGEm: true,
            Experimental: false),
        new BotDefinition(
            MacroKind.GastarWheelspins,
            "Gastar Wheelspins",
            "Gira Super Wheelspins e Wheelspins com confirmação visual.",
            "Wheelspins",
            Requirements(
                Required("Interface do jogo em português do Brasil"),
                Required("Pacote de OCR em português disponível no Windows"),
                Required("Wheelspins ou Super Wheelspins disponíveis para realizar giros"),
                Advisory("Aceitar que carros duplicados serão mantidos")),
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

    private static BotRequirement Automated(string text) =>
        new(text, BotRequirementKind.Automated);

    private static BotRequirement Required(string text) =>
        new(text, BotRequirementKind.Required);

    private static BotRequirement Advisory(string text) =>
        new(text, BotRequirementKind.Advisory);

    private static IReadOnlyList<BotRequirement> Requirements(params BotRequirement[] requirements) =>
        Array.AsReadOnly(requirements);
}
