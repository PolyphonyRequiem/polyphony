using Polyphony.Configuration;

namespace Polyphony.Tests.TestFixtures;

/// <summary>
/// Loads process config YAML fixtures by template name.
/// Delegates to <see cref="ProcessConfigLoader"/> so the real loading path is exercised.
/// </summary>
public static class ProcessConfigFixture
{
    private static readonly string FixturesDir = Path.Combine(
        AppContext.BaseDirectory, "TestFixtures", "ProcessConfigs");

    public static ProcessConfig Basic() => Load("basic.yaml");
    public static ProcessConfig Agile() => Load("agile.yaml");
    public static ProcessConfig Scrum() => Load("scrum.yaml");
    public static ProcessConfig Cmmi() => Load("cmmi.yaml");

    private static ProcessConfig Load(string fileName)
    {
        var path = Path.Combine(FixturesDir, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Process config fixture '{fileName}' not found at '{path}'. " +
                "Ensure the YAML files are included as Content with CopyToOutputDirectory in the .csproj.");

        return ProcessConfigLoader.Load(path);
    }
}
