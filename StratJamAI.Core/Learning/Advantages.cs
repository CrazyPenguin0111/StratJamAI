using StratJamAI.Core.Games;

namespace StratJamAI.Core.Learning;

public sealed class Transition
{
    public required DecisionRequest Decision { get; init; }
    public int Environment { get; init; }
    public int ActionIndex { get; init; }
    public float OldLogProbability { get; init; }
    public float OldValue { get; init; }
    public float Reward { get; init; }
    public float Discount { get; init; }
    public float NextValue { get; set; }
    public bool Terminated { get; init; }
    public bool EpisodeBoundary { get; init; }
    public float Advantage { get; set; }
    public float Return { get; set; }
}

public static class Advantages
{
    // Gamma is applied per simulator step; lambda is applied per learner decision.
    // A time-limit truncation bootstraps its final observation, but never joins the next episode's trace.
    public static void Compute(IReadOnlyList<Transition> transitions, float lambda, bool normalize = true)
    {
        var following = new Dictionary<int, float>();
        for (var i = transitions.Count - 1; i >= 0; i--)
        {
            var t = transitions[i];
            var next = !t.EpisodeBoundary && following.TryGetValue(t.Environment, out var value) ? value : 0;
            var delta = t.Reward + (t.Terminated ? 0 : t.Discount * t.NextValue) - t.OldValue;
            t.Advantage = delta + t.Discount * lambda * next;
            t.Return = t.Advantage + t.OldValue;
            following[t.Environment] = t.Advantage;
        }
        if (!normalize || transitions.Count < 2) return;
        var mean = transitions.Average(t => (double)t.Advantage);
        var deviation = Math.Sqrt(transitions.Average(t => Math.Pow(t.Advantage - mean, 2)) + 1e-8);
        foreach (var t in transitions) t.Advantage = (float)((t.Advantage - mean) / deviation);
    }

    public static double ClippedSurrogate(double newLogProbability, double oldLogProbability, double advantage, double clip)
    {
        var ratio = Math.Exp(newLogProbability - oldLogProbability);
        return Math.Min(ratio * advantage, Math.Clamp(ratio, 1 - clip, 1 + clip) * advantage);
    }
}
