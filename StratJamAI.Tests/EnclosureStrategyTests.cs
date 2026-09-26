using System.Reflection;
using StratJamAI.Core;
using StratJamAI.Core.Bots;
using StratJamAI.Core.Games;
using Xunit;
namespace StratJamAI.Tests;
public sealed class EnclosureStrategyTests
{
    [Fact]
    public void ForecastRecognizesReportedLateComeback()
    {
        var game = new Enclosure(); Set(game, nameof(Enclosure.MoveNumber), 72);
        Field<double[]>(game,"areas")[0]=183; Field<double[]>(game,"areas")[1]=103.58;
        Field<double[]>(game,"scores")[0]=448; Field<double[]>(game,"scores")[1]=1671;
        Assert.True(448+4*183 < 1671+4*103.58);
        Assert.True(EnclosureStrategyEvaluation.Forecast(game,0).ProjectedScore > EnclosureStrategyEvaluation.Forecast(game,1).ProjectedScore);
        Assert.True(EnclosureStrategyEvaluation.Evaluate(game,0)>0);
    }
    [Fact]
    public void DepthOneInvestsInLargeBoundaryInsteadOfImmediateTinyTriangle()
    {
        var game=OpenBoundary(88,2);
        Assert.Equal(0, game.Areas[0]);
        Assert.Equal(2, EnclosureStrategyEvaluation.Forecast(game,0).CompletionMoves);
        Assert.True(EnclosureStrategyEvaluation.Forecast(game,0).PotentialArea>80);
        var immediate=game.GenerateLegalActions().Select(action=>{var copy=game.Copy();copy.Play(action);return copy.Areas[0];}).Max();
        Assert.True(immediate>0);
        var result=new EnclosureAlphaBetaBot(new(10000,1)).Search(game,Deadline.Never);
        game.Play(result.Action);
        Assert.Equal(0,game.Areas[0]);
        Assert.Equal(1,EnclosureStrategyEvaluation.Forecast(game,0).CompletionMoves);
    }
    [Fact]
    public void NoPotentialCreditWhenCompletionCannotFitBeforeGameEnd()
    {
        var game=OpenBoundary(119,1);
        Assert.Equal(2,EnclosureStrategyEvaluation.Forecast(game,0).CompletionMoves);
        Assert.Equal(0,EnclosureStrategyEvaluation.Forecast(game,0).PotentialArea);
    }
    [Fact]
    public void DoubleWallsBlockTwoCrossingsWithoutInvincibilityAndRetainBackupArea()
    {
        var outer=Square(3,3,12); var both=new List<int>(outer);
        both.AddRange(Square(4,4,10)); both.Add(Id(3,3,4,4));
        var single=Position(outer,[Id(8,0,8,2)],72,1,1);
        var doubled=Position(both,[Id(8,0,8,2)],72,1,1);
        Assert.All(doubled.Segments(0),edge=>Assert.False(edge.Invincible));
        Assert.Equal(144,single.Areas[0]); Assert.Equal(144,doubled.Areas[0]);
        Assert.True(single.IsLegal(Id(8,2,8,5))); Assert.False(doubled.IsLegal(Id(8,2,8,5)));
        var a=EnclosureStrategyEvaluation.Forecast(single,0);var b=EnclosureStrategyEvaluation.Forecast(doubled,0);
        Assert.Equal(0,b.ProtectedEdges); Assert.True(b.ReinforcedBoundary>a.ReinforcedBoundary); Assert.True(b.ReinforcedBoundary > 0);
        doubled.Play(Id(8,2,8,3)); Assert.Equal(100,doubled.Areas[0]);
    }
    [Fact]
    public void DisconnectedFragmentsDoNotReceiveTheirCombinedBoundingBoxArea()
    {
        var game=Position([Id(0,0,3,0),Id(15,18,18,18)],[Id(18,0,18,3)],40,0,1);
        Assert.Equal(0,EnclosureStrategyEvaluation.Forecast(game,0).PotentialArea);
    }
    [Fact]
    public void OpeningSearchAdvancesIntoContestedSpace()
    {
        var game = new Enclosure();
        var before = EnclosureStrategyEvaluation.Forecast(game, 0);
        var result = new EnclosureAlphaBetaBot(new(10000, 1)).Search(game, Deadline.Never);
        var edge = Enclosure.DecodeAction(result.Action);
        game.Play(result.Action);
        Assert.True(Math.Max(edge.From.X, edge.To.X) >= 6);
        Assert.True(EnclosureStrategyEvaluation.Forecast(game, 0).ControlledSpace > before.ControlledSpace);
    }

    [Fact]
    public void ClosedCompactTerritoryExpandsInsteadOfAddingInteriorWalls()
    {
        var game = Position(Square(0, 6, 6), [Id(18, 9, 15, 9)], 40, 0, 2);
        var before = EnclosureStrategyEvaluation.Forecast(game, 0);
        var result = new EnclosureAlphaBetaBot(new(10000, 1)).Search(game, Deadline.Never);
        var edge = Enclosure.DecodeAction(result.Action);
        game.Play(result.Action);
        Assert.True(edge.From.X > 6 || edge.To.X > 6 || edge.From.Y < 6 || edge.To.Y < 6 || edge.From.Y > 12 || edge.To.Y > 12);
        Assert.True(EnclosureStrategyEvaluation.Forecast(game, 0).ControlledSpace > before.ControlledSpace);
    }

    [Fact]
    public void EarlyConstructionClosesValuableAreaInsteadOfEndlessExpansion()
    {
        var edges = Square(0, 0, 6);
        edges.Remove(Id(0, 0, 0, 3));
        var game = Position(edges, [Id(18, 9, 15, 9)], 20, 0, 1);
        var result = new EnclosureAlphaBetaBot(new(10000, 1)).Search(game, Deadline.Never);
        game.Play(result.Action);
        Assert.Equal(36, game.Areas[0]);
    }

    [Fact]
    public void FinalTurnClosesAchievableAreaInsteadOfChasingSpace()
    {
        var edges = Square(0, 6, 6);
        edges.Remove(Id(0, 6, 0, 9));
        var game = Position(edges, [Id(18, 9, 15, 9)], 119, 0, 1);
        Assert.Equal(0, EnclosureStrategyEvaluation.Forecast(game, 0).ExpansionScore);
        var result = new EnclosureAlphaBetaBot(new(10000, 1)).Search(game, Deadline.Never);
        game.Play(result.Action);
        Assert.Equal(36, game.Areas[0]);
        Assert.Equal(36, game.Scores[0]);
    }

    [Fact]
    public void SpaceEstimateIsContestedBoundedAndRotationSymmetric()
    {
        var game = new Enclosure();
        var blue = EnclosureStrategyEvaluation.Forecast(game, 0);
        var red = EnclosureStrategyEvaluation.Forecast(game, 1);
        Assert.Equal(162, blue.ControlledSpace, 8);
        Assert.Equal(blue.ControlledSpace, red.ControlledSpace, 8);
        game.Play(Id(3, 9, 6, 9));
        blue = EnclosureStrategyEvaluation.Forecast(game, 0);
        red = EnclosureStrategyEvaluation.Forecast(game, 1);
        Assert.InRange(blue.ControlledSpace, 162, 324);
        Assert.True(red.ControlledSpace < 162);
        Assert.Equal(324, blue.ControlledSpace + red.ControlledSpace, 8);
    }

    private static Enclosure OpenBoundary(int move,int remaining)
    {
        var blue=new List<int>();Line(blue,0,12,0,0);Line(blue,0,0,12,0);Line(blue,12,0,12,12);Line(blue,12,12,6,12);
        return Position(blue,[Id(18,0,18,3)],move,0,remaining);
    }
    private static List<int> Square(int x,int y,int width)
    {
        var edges=new List<int>();Line(edges,x,y,x+width,y);Line(edges,x+width,y,x+width,y+width);Line(edges,x+width,y+width,x,y+width);Line(edges,x,y+width,x,y);return edges;
    }
    private static void Line(List<int> edges,int x,int y,int tx,int ty)
    {
        while(x!=tx||y!=ty){var nx=x+Math.Clamp(tx-x,-3,3);var ny=y+Math.Clamp(ty-y,-3,3);edges.Add(Id(x,y,nx,ny));x=nx;y=ny;}
    }
    private static int Id(int x,int y,int tx,int ty)=>Enclosure.GetActionId(x,y,tx,ty);
    private static Enclosure Position(IReadOnlyList<int> blue,IReadOnlyList<int> red,int move,int turn,int remaining)
    {
        var game=new Enclosure();var edges=Field<int[]>(game,"edges");var owners=Field<byte[]>(game,"owners");var nodes=Field<bool[]>(game,"nodes");
        Array.Clear(nodes);Array.Clear(Field<byte[]>(game,"flags"));var count=0;
        foreach(var(player,actions)in new[]{(0,blue),(1,red)})foreach(var action in actions){edges[count]=action;owners[count++]=(byte)player;var edge=Enclosure.DecodeAction(action);nodes[player*361+edge.From.X*19+edge.From.Y]=true;nodes[player*361+edge.To.X*19+edge.To.Y]=true;}
        typeof(Enclosure).GetField("edgeCount",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(game,count);
        Array.Fill(Field<bool[]>(game,"areaDirty"),true);Set(game,nameof(Enclosure.MoveNumber),move);Set(game,nameof(Enclosure.Turn),turn);Set(game,nameof(Enclosure.ActionsRemaining),remaining);return game;
    }
    private static T Field<T>(Enclosure game,string name)=>(T)typeof(Enclosure).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(game)!;
    private static void Set(Enclosure game,string name,object value)=>typeof(Enclosure).GetProperty(name)!.SetValue(game,value);
}
