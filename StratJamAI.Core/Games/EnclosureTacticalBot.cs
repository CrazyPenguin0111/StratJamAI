using StratJamAI.Core.Bots;

namespace StratJamAI.Core.Games;

/// <summary>A training teacher with exact area evaluation limited to 32 promising moves.</summary>
public sealed class EnclosureTacticalBot : IBot
{
    public string Name => "tactical";

    public int ChooseAction(DecisionRequest decision, BotContext context)
    {
        var fallback = decision.Actions[0].Id;
        if (context.Deadline.Expired || fallback == Enclosure.PassActionId) return fallback;
        Span<int> shortlist = stackalloc int[32];
        Span<double> ranks = stackalloc double[32];
        var count = 0;
        foreach (var action in decision.Actions)
        {
            if (context.Deadline.Expired) break;
            var f = action.Features;
            var rank = f.Length == 12 ? f[9] * 8 + f[10] * 12 + f[6] + (f[0] + f[2]) * .5 : 0;
            var index = count;
            if (index == 32) { if (rank <= ranks[31]) continue; index--; }
            else count++;
            while (index > 0 && rank > ranks[index - 1])
            { ranks[index] = ranks[index - 1]; shortlist[index] = shortlist[index - 1]; index--; }
            ranks[index] = rank; shortlist[index] = action.Id;
        }
        if (count == 0) return fallback;
        var best = shortlist[0];
        if (context.SearchState is not Enclosure state) return best;
        var copy = state.Copy(); var player = decision.Player; var value = double.NegativeInfinity;
        for (var i = 0; i < count && !context.Deadline.Expired; i++)
        {
            copy.CopyFrom(state); copy.Play(shortlist[i]);
            var score = copy.Scores[player] - copy.Scores[1 - player] +
                4 * (copy.Areas[player] - copy.Areas[1 - player]) + ranks[i] * .002;
            if (score > value) { value = score; best = shortlist[i]; }
        }
        return best;
    }
}
