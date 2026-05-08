using Xunit;

namespace Polyphony.Tests.TestFixtures;

/// <summary>
/// Marker collection for test classes that mutate
/// <see cref="System.Environment.CurrentDirectory"/>. xUnit runs
/// classes in the same collection sequentially, so collecting all
/// cwd-mutating classes here prevents them from racing on the
/// process-global working directory.
/// </summary>
[CollectionDefinition("CwdSerial", DisableParallelization = true)]
public sealed class CwdSerialCollection
{
}
