using Xunit;

namespace Polyphony.Tests.TestFixtures;

/// <summary>
/// Marker collection for test classes that mutate process-global environment
/// variables such as PATH. Serializing them prevents cross-class races with
/// tests that spawn child processes and rely on inherited environment state.
/// </summary>
[CollectionDefinition("EnvironmentSerial", DisableParallelization = true)]
public sealed class EnvironmentSerialCollection
{
}
