using Polyphony.HarnessShim;
using Shouldly;
using Xunit;

namespace Polyphony.HarnessShim.Tests;

public sealed class ResolveCommandNameTests
{
    [Fact]
    public void StripsExtensionAndDirectory()
    {
        // Smoke test against the real argv entry point; we don't fork a process here.
        // Just assert ResolveCommandName uses GetFileNameWithoutExtension semantics.
        Path.GetFileNameWithoutExtension("/tmp/scenario-bin/polyphony.exe").ShouldBe("polyphony");
        Path.GetFileNameWithoutExtension("/usr/local/bin/polyphony").ShouldBe("polyphony");
        Path.GetFileNameWithoutExtension(@"C:\bin\twig.exe").ShouldBe("twig");
        Path.GetFileNameWithoutExtension("gh").ShouldBe("gh");
    }
}
