using Polyphony.Journal;
using Polyphony.Manifest;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Manifest;

/// <summary>
/// Verifies the W2 schema-v2 additions on <see cref="RunManifest"/>:
/// <see cref="RunManifest.RunId"/> round-trips through
/// <see cref="RunManifestStore"/>; the validator accepts the schema
/// range [1, 2] and rejects out-of-range values; legacy schema-1
/// manifests load without RunId; warnings surface the legacy gap.
/// </summary>
public sealed class RunManifestSchemaV2Tests : IDisposable
{
    private readonly string tempDir;

    public RunManifestSchemaV2Tests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), "polyphony-manifest-v2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* best effort */ }
    }

    private string PathOf(string name) => Path.Combine(this.tempDir, name);

    private static RunManifest GoodBaseline(int schema, string? runId) => new()
    {
        Schema = schema,
        RootId = 1234,
        RunId = runId,
        PlatformProject = "dev.azure.com/org/project",
        CreatedAt = new DateTime(2026, 5, 6, 15, 30, 0, DateTimeKind.Utc),
        CreatedBy = "dangreen",
        BranchModelVersion = 1,
    };

    [Fact]
    public void V2_WithRunId_RoundTripsThroughStore()
    {
        var ulid = RunIdMint.NewRunId();
        var path = PathOf("run.yaml");
        var manifest = GoodBaseline(schema: 2, runId: ulid);
        RunManifestStore.Save(path, manifest);

        var loaded = RunManifestStore.LoadOrThrow(path);
        loaded.Schema.ShouldBe(2);
        loaded.RunId.ShouldBe(ulid);
    }

    [Fact]
    public void V1_LegacyWithoutRunId_LoadsCleanly_AndNotErrored()
    {
        var path = PathOf("legacy.yaml");
        var manifest = GoodBaseline(schema: 1, runId: null);
        RunManifestStore.Save(path, manifest);

        var loaded = RunManifestStore.LoadOrThrow(path);
        loaded.Schema.ShouldBe(1);
        loaded.RunId.ShouldBeNull();

        // CollectWarnings surfaces the legacy gap without making it fatal.
        var warnings = RunManifestValidator.CollectWarnings(loaded);
        warnings.ShouldContain(w => w.Contains("legacy schema 1"));
    }

    [Fact]
    public void V2_WithoutRunId_Warns_ButDoesNotError()
    {
        var manifest = GoodBaseline(schema: 2, runId: null);
        RunManifestValidator.Validate(manifest).ShouldBeEmpty();
        RunManifestValidator.CollectWarnings(manifest).ShouldContain(w => w.Contains("schema 2") && w.Contains("missing run_id"));
    }

    [Fact]
    public void Schema_OutOfRange_IsRejected()
    {
        var manifest = GoodBaseline(schema: 0, runId: null);
        RunManifestValidator.Validate(manifest).ShouldContain(s => s.Contains("schema must be in"));

        manifest.Schema = 99;
        RunManifestValidator.Validate(manifest).ShouldContain(s => s.Contains("schema must be in"));
    }

    [Fact]
    public void RunId_MalformedUlid_IsRejected()
    {
        var manifest = GoodBaseline(schema: 2, runId: "not-a-real-ulid");
        RunManifestValidator.Validate(manifest).ShouldContain(s => s.Contains("run_id"));
    }

    [Fact]
    public void RunId_WellFormedUlid_PassesValidation()
    {
        var manifest = GoodBaseline(schema: 2, runId: RunIdMint.NewRunId());
        RunManifestValidator.Validate(manifest).ShouldBeEmpty();
    }

    [Fact]
    public void CurrentSchema_Constant_IsTwo()
    {
        // Locking the value so a future bump is intentional, not accidental.
        RunManifestValidator.CurrentSchema.ShouldBe(2);
        RunManifestValidator.MaxSupportedSchema.ShouldBe(2);
        RunManifestValidator.MinSupportedSchema.ShouldBe(1);
    }
}
