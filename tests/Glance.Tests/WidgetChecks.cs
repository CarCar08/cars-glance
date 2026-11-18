using Xunit;

namespace Glance.Tests;

// Every widget check (time maths, views, smart rules, parsers, layout, motion, Credential Manager round trip)
// as its own test case. The same list powers CarsGlance.exe --selftest on a real PC.
public class WidgetChecks
{
    static readonly Lazy<List<(string Name, bool Ok)>> All = new(() =>
    {
        // Never touch the real data folder from tests.
        Paths.DataDir = Path.Combine(Path.GetTempPath(), "CarsGlance-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Paths.DataDir);
        return SelfTest.Checks();
    });

    public static IEnumerable<object[]> Names() => All.Value.Select(c => new object[] { c.Name });

    [Theory]
    [MemberData(nameof(Names))]
    public void Check(string name) => Assert.True(All.Value.First(c => c.Name == name).Ok, name);

    [Fact]
    public void ThereAreChecks() => Assert.True(All.Value.Count >= 60, "only " + All.Value.Count + " checks ran");
}
