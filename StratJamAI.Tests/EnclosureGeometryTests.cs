using System.Reflection;
using System.Text.Json;
using StratJamAI.Core.Games;
using Xunit;

namespace StratJamAI.Tests;

public sealed class EnclosureGeometryTests
{
    private delegate double AreaMethod(ReadOnlySpan<int> actions);
    private delegate (double Area, EnclosurePosition[][] Territories) PolygonMethod(ReadOnlySpan<int> actions, bool includeTerritories);
    private static readonly Type Geometry = typeof(Enclosure).Assembly.GetType("StratJamAI.Core.Games.EnclosureGeometry")!;
    private static readonly AreaMethod Area = Geometry.GetMethod("Area", BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<AreaMethod>();
    private static readonly PolygonMethod Polygons = Geometry.GetMethod("Calculate", BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<PolygonMethod>();

    private static int[][] ReferenceGeometries()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "EnclosureReference.json")));
        return document.RootElement.GetProperty("traces").EnumerateArray()
            .SelectMany(trace => trace.GetProperty("frames").EnumerateArray())
            .SelectMany(frame => frame.GetProperty("segments").EnumerateArray())
            .Select(segments => segments.EnumerateArray().Select(segment => segment.GetProperty("id").GetInt32()).ToArray())
            .ToArray();
    }

    [Fact]
    public void ReusedAreaWorkspaceMatchesIndependentPolygonExtractionForEveryReferenceState()
    {
        var geometries = ReferenceGeometries();
        // Alternating large and small inputs exercises scratch cleanup and retained capacities.
        foreach (var geometry in geometries.Reverse().Concat(geometries))
            Assert.Equal(Polygons(geometry, true).Area, Area(geometry));
    }

    [Fact]
    public async Task ParallelAreaCalculationsKeepThreadWorkspacesIndependent()
    {
        var geometries = ReferenceGeometries().Where((_, index) => index % 7 == 0).ToArray();
        var expected = geometries.Select(geometry => Polygons(geometry, false).Area).ToArray();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < geometries.Length * 4; i++)
            {
                var index = (i * 17 + worker) % geometries.Length;
                Assert.Equal(expected[index], Area(geometries[index]));
            }
        })));
    }

    [Fact]
    public void WarmedAreaCalculationsReuseTheirGeometryStorage()
    {
        var geometry = ReferenceGeometries().MaxBy(actions => actions.Length)!;
        for (var i = 0; i < 30; i++) Area(geometry);
        var expected = Area(geometry);
        var before = GC.GetAllocatedBytesForCurrentThread();
        double actual = 0;
        for (var i = 0; i < 100; i++) actual = Area(geometry);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(expected, actual);
        Assert.True(allocated <= 1024, $"Warmed area calculation allocated {allocated} bytes across 100 calls.");
    }
}
