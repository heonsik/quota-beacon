using System.Reflection;

namespace QuotaBeacon.Tests;

/// <summary>
/// Loads a hand-authored response fixture.
/// </summary>
/// <remarks>
/// Fixtures are written by hand and contain no real account data or tokens, so they are safe to
/// commit. They are resolved relative to the test assembly so the tests do not depend on the
/// working directory.
/// </remarks>
internal static class Fixture
{
    private static readonly string Directory = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
        "Fixtures");

    public static string Read(string fileName) => File.ReadAllText(Path.Combine(Directory, fileName));
}
