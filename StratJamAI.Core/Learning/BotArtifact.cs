using StratJamAI.Core.Bots;
using StratJamAI.Core.Games;

namespace StratJamAI.Core.Learning;

public sealed record BotArtifact(int FormatVersion, GameSpec Game, NetworkWeights Weights, string SelectedBot,
    SearchOptions Search, DateTimeOffset CreatedUtc, long TrainingDecisions, string Description)
{
    public const int CurrentFormatVersion = 1;
    // Mixed strategies matter for simultaneous/hidden-information games. Null selects the capability-based default.
    public bool? SamplePolicy { get; init; }
    public void Validate(GameSpec expected)
    {
        if (FormatVersion != CurrentFormatVersion || Game != expected)
            throw new InvalidDataException("Unsupported artifact version or mismatched game/feature schema.");
        Weights.Validate(Game);
        Search.Validate();
        if (SelectedBot is not ("random" or "tactical" or "policy" or "search" or "search-policy"))
            throw new InvalidDataException("Unknown bot kind in the artifact.");
        if (SelectedBot.StartsWith("search", StringComparison.Ordinal) && !Game.SupportsSearch)
            throw new InvalidDataException("This game does not support PUCT search.");
    }
    public IBot CreateBot(IGameDefinition game)
    {
        Validate(game.Spec);
        return SelectedBot switch
        {
            "random" => new RandomBot(),
            "tactical" => game.CreateTeacher() ?? throw new InvalidDataException("This game has no tactical bot."),
            "policy" => new PolicyBot(new CpuNetwork(Game, Weights), SamplePolicy ?? (!Game.Sequential || !Game.FullyObservable)),
            "search" => new MctsBot(Search),
            "search-policy" => new MctsBot(Search, new CpuNetwork(Game, Weights)),
            _ => throw new InvalidDataException("Unknown bot.")
        };
    }
    public static BotArtifact Load(string path, GameSpec expected)
    {
        var artifact = JsonFiles.Read<BotArtifact>(path);
        artifact.Validate(expected);
        return artifact;
    }
}
