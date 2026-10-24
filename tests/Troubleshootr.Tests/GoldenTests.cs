using Troubleshootr.Checks;
using Troubleshootr.Core;
using Xunit;

namespace Troubleshootr.Tests;

/// <summary>
/// Runs every check on each tests/fixtures/*.snap.json and compares the findings with the matching *.expected.txt.
/// To accept intended changes, run the tests with the environment variable UPDATE_GOLDEN=1 and review the diff in git.
/// </summary>
public class GoldenTests
{
    public static IEnumerable<object[]> Fixtures() =>
        Directory.GetFiles(Path.Combine(Paths.Repo(), "tests", "fixtures"), "*.snap.json").Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void FixtureFindingsMatch(string fixture)
    {
        var dir = Path.Combine(Paths.Repo(), "tests", "fixtures");
        var snapshot = Json.Deserialize<Snapshot>(File.ReadAllText(Path.Combine(dir, fixture)));
        Assert.True(snapshot.Meta.Anonymized, $"{fixture} isn't anonymized; record it with --anonymize");
        var actual = TextReport.Golden(ScanEngine.Evaluate(snapshot, CheckRegistry.All));
        var expectedPath = Path.Combine(dir, fixture.Replace(".snap.json", ".expected.txt"));
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1" || !File.Exists(expectedPath))
        {
            File.WriteAllText(expectedPath, actual);
            return;
        }
        Assert.Equal(File.ReadAllText(expectedPath).Replace("\r\n", "\n"), actual);
    }
}
