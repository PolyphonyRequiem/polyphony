using Polyphony.HarnessShim;
using Shouldly;
using Xunit;

namespace Polyphony.HarnessShim.Tests;

public sealed class ResolveCommandNameTests
{
    [Fact]
    public void StripsExtensionAndDirectory_PortablePathSeparator()
    {
        // Use the host's path separator so the test means the same thing on
        // both Windows (\) and Linux (/). The shim itself reads
        // Environment.GetCommandLineArgs()[0] which is always written with
        // the host separator, so this matches production behavior.
        var sep = Path.DirectorySeparatorChar;
        Path.GetFileNameWithoutExtension($"{sep}tmp{sep}scenario-bin{sep}polyphony.exe").ShouldBe("polyphony");
        Path.GetFileNameWithoutExtension($"{sep}usr{sep}local{sep}bin{sep}polyphony").ShouldBe("polyphony");
        Path.GetFileNameWithoutExtension($"bin{sep}twig.exe").ShouldBe("twig");
        Path.GetFileNameWithoutExtension("gh").ShouldBe("gh");
    }
}
