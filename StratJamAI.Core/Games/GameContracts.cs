namespace StratJamAI.Core.Games;

public sealed record GameSpec(string Id, string FeatureSchema, int ObservationSize, int ActionFeatureSize,
    int PlayerCount, bool Sequential, bool FullyObservable, bool Deterministic, bool ZeroSum)
{
    public bool SupportsSearch => Sequential && FullyObservable && Deterministic && ZeroSum && PlayerCount == 2;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(FeatureSchema) ||
            ObservationSize < 1 || ActionFeatureSize < 1 || PlayerCount < 1)
            throw new ArgumentException("A game needs an ID, feature schema, positive feature dimensions and players.");
    }
}

// IDs are opaque: neither the model nor search assumes a dense or fixed action vocabulary.
public sealed record LegalAction(int Id, float[] Features);

public sealed record DecisionRequest(int Player, float[] Observation, LegalAction[] Actions)
{
    public DecisionRequest Copy() => new(Player, (float[])Observation.Clone(),
        Actions.Select(a => new LegalAction(a.Id, (float[])a.Features.Clone())).ToArray());

    public void Validate(GameSpec spec)
    {
        if (Player < 0 || Player >= spec.PlayerCount || Observation.Length != spec.ObservationSize ||
            Observation.Any(x => !float.IsFinite(x)) || Actions.Length == 0)
            throw new InvalidDataException("Invalid observation, player, or empty legal-action list. Pass must be explicit.");
        var ids = new HashSet<int>();
        foreach (var action in Actions)
            if (!ids.Add(action.Id) || action.Features.Length != spec.ActionFeatureSize ||
                action.Features.Any(x => !float.IsFinite(x)))
                throw new InvalidDataException("Action IDs must be unique and action features must be finite and match the schema.");
    }
}

// Rewards belong to the transition that produced this frame, including rewards on other players' turns.
public sealed record GameFrame(DecisionRequest[] Decisions, float[] Rewards, bool Terminated,
    bool Truncated, float[] Returns)
{
    public bool Finished => Terminated || Truncated;

    public void Validate(GameSpec spec)
    {
        if (Terminated && Truncated || Rewards.Length != spec.PlayerCount || Returns.Length != spec.PlayerCount ||
            Rewards.Any(x => !float.IsFinite(x)) || Returns.Any(x => !float.IsFinite(x)))
            throw new InvalidDataException("Invalid rewards, returns, or episode boundary.");
        if (Finished && Decisions.Length != 0 || !Finished && Decisions.Length == 0 ||
            spec.Sequential && Decisions.Length > 1 || Decisions.Select(d => d.Player).Distinct().Count() != Decisions.Length)
            throw new InvalidDataException("Invalid acting-player set.");
        foreach (var decision in Decisions) decision.Validate(spec);
    }
}

public interface IGameAdapter
{
    GameSpec Spec { get; }
    GameFrame Frame { get; }
    GameFrame Reset(ulong seed);
    // All joint actions must be collected from the same frame before this is called.
    GameFrame Step(IReadOnlyDictionary<int, int> actions);
    // Available even at truncation and between this player's decisions; contains only visible information.
    float[] Observe(int player);
}

public interface ISearchableGame : IGameAdapter
{
    // Independent simulation state. Implement only for genuinely full-information games.
    ISearchableGame Fork();
}

public interface IGameDefinition
{
    GameSpec Spec { get; }
    IGameAdapter Create();
    Bots.IBot? CreateTeacher();
    Bots.IBot? CreateOracle();
}
