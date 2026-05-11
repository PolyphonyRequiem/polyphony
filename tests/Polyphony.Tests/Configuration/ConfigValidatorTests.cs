using Polyphony.Configuration;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Configuration;

public sealed class ConfigValidatorTests
{
    #region V-1: process_template required

    [Fact]
    public void V1_MissingProcessTemplate_ProducesError()
    {
        var config = ValidConfig();
        config.ProcessTemplate = "";

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "V-1");
    }

    [Fact]
    public void V1_WhitespaceProcessTemplate_ProducesError()
    {
        var config = ValidConfig();
        config.ProcessTemplate = "   ";

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "V-1");
    }

    [Fact]
    public void V1_PresentProcessTemplate_NoError()
    {
        var config = ValidConfig();

        var result = ConfigValidator.Validate(config);

        result.Errors.ShouldNotContain(d => d.RuleId == "V-1");
    }

    #endregion

    #region V-2: types list non-empty

    [Fact]
    public void V2_EmptyTypes_ProducesError()
    {
        var config = ValidConfig();
        config.Types = new Dictionary<string, TypeConfig>();

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "V-2");
    }

    [Fact]
    public void V2_NonEmptyTypes_NoError()
    {
        var config = ValidConfig();

        var result = ConfigValidator.Validate(config);

        result.Errors.ShouldNotContain(d => d.RuleId == "V-2");
    }

    #endregion

    #region V-3: each type has at least one facet

    [Fact]
    public void V3_TypeWithNoFacets_ProducesError()
    {
        var config = ValidConfig();
        config.Types["Task"] = new TypeConfig { Facets = [] };

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "V-3" && d.Message.Contains("Task"));
    }

    [Fact]
    public void V3_TypeWithFacets_NoError()
    {
        var config = ValidConfig();

        var result = ConfigValidator.Validate(config);

        result.Errors.ShouldNotContain(d => d.RuleId == "V-3");
    }

    #endregion

    #region V-4: facet values are valid

    [Fact]
    public void V4_InvalidFacet_ProducesError()
    {
        var config = ValidConfig();
        config.Types["Task"] = new TypeConfig { Facets = ["invalid_cap"] };

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "V-4" && d.Message.Contains("invalid_cap"));
    }

    [Fact]
    public void V4_PlannableFacet_NoError()
    {
        var config = ValidConfig();
        config.Types["Task"] = new TypeConfig { Facets = ["plannable"] };

        var result = ConfigValidator.Validate(config);

        result.Errors.ShouldNotContain(d => d.RuleId == "V-4");
    }

    [Fact]
    public void V4_ImplementableFacet_NoError()
    {
        var config = ValidConfig();

        var result = ConfigValidator.Validate(config);

        result.Errors.ShouldNotContain(d => d.RuleId == "V-4");
    }

    [Fact]
    public void V4_CaseInsensitiveFacet_NoError()
    {
        var config = ValidConfig();
        config.Types["Task"] = new TypeConfig { Facets = ["Plannable", "IMPLEMENTABLE"] };

        var result = ConfigValidator.Validate(config);

        result.Errors.ShouldNotContain(d => d.RuleId == "V-4");
    }

    #endregion

    #region V-5: each type has at least one transition

    [Fact]
    public void V5_TypeWithNoTransitions_ProducesError()
    {
        var config = ValidConfig();
        config.Transitions.Remove("Task");

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "V-5" && d.Message.Contains("Task"));
    }

    [Fact]
    public void V5_TypeWithEmptyTransitions_ProducesError()
    {
        var config = ValidConfig();
        config.Transitions["Task"] = new Dictionary<string, string>();

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "V-5");
    }

    [Fact]
    public void V5_TypeWithTransitions_NoError()
    {
        var config = ValidConfig();

        var result = ConfigValidator.Validate(config);

        result.Errors.ShouldNotContain(d => d.RuleId == "V-5");
    }

    #endregion

    #region V-6: transition keys reference defined types

    [Fact]
    public void V6_TransitionForUndefinedType_ProducesError()
    {
        var config = ValidConfig();
        config.Transitions["Ghost"] = new Dictionary<string, string>
        {
            ["begin_implementation"] = "Doing"
        };

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "V-6" && d.Message.Contains("Ghost"));
    }

    [Fact]
    public void V6_TransitionsMatchTypes_NoError()
    {
        var config = ValidConfig();

        var result = ConfigValidator.Validate(config);

        result.Errors.ShouldNotContain(d => d.RuleId == "V-6");
    }

    #endregion

    #region V-7: no duplicate type names

    [Fact]
    public void V7_NoDuplicates_NoError()
    {
        var config = ValidConfig();

        var result = ConfigValidator.Validate(config);

        result.Errors.ShouldNotContain(d => d.RuleId == "V-7");
    }

    [Fact]
    public void V7_CaseInsensitiveDuplicates_ProducesError()
    {
        // Case-insensitive duplicates require an ordinal-comparer dict
        var types = new Dictionary<string, TypeConfig>(StringComparer.Ordinal)
        {
            ["Task"] = new TypeConfig { Facets = ["implementable"] },
            ["task"] = new TypeConfig { Facets = ["implementable"] },
        };
        var transitions = new Dictionary<string, string> { ["begin_implementation"] = "Doing" };
        var config = new ProcessConfig
        {
            ProcessTemplate = "Basic",
            Types = types,
            Transitions = new Dictionary<string, Dictionary<string, string>>
            {
                ["Task"] = transitions,
                ["task"] = transitions,
            },
        };

        var result = ConfigValidator.Validate(config);

        result.Errors.ShouldContain(d => d.RuleId == "V-7");
    }

    #endregion

    #region V-15/V-16: parent-exists and cycle-detection

    [Fact]
    public void V15_MissingParent_ProducesError()
    {
        var config = ValidConfig();
        config.Types["Task"] = new TypeConfig { Facets = ["implementable"], Parent = "NonExistent" };

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "V-15" && d.Message.Contains("NonExistent"));
    }

    [Fact]
    public void V16_CycleDetected_ProducesError()
    {
        var config = ValidConfig();
        config.Types["Epic"] = new TypeConfig { Facets = ["plannable"], Parent = "Task" };
        config.Types["Task"] = new TypeConfig { Facets = ["implementable"], Parent = "Epic" };

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "V-16" && d.Message.Contains("Cycle"));
    }

    [Fact]
    public void V15_ParentExists_NoError()
    {
        var config = ValidConfig();
        config.Types["Epic"] = new TypeConfig { Facets = ["plannable"] };
        config.Types["Task"] = new TypeConfig { Facets = ["implementable"], Parent = "Epic" };

        var result = ConfigValidator.Validate(config);

        result.Errors.ShouldNotContain(d => d.RuleId == "V-15");
    }

    [Fact]
    public void V16_NoCycle_NoError()
    {
        var config = ValidConfig();
        config.Types["Epic"] = new TypeConfig { Facets = ["plannable"] };
        config.Types["Task"] = new TypeConfig { Facets = ["implementable"], Parent = "Epic" };

        var result = ConfigValidator.Validate(config);

        result.Errors.ShouldNotContain(d => d.RuleId == "V-16");
    }

    #endregion

    #region V-8: allowed_child_types reference defined types

    [Fact]
    public void V8_AllowedChildTypesDefined_NoError()
    {
        var config = ValidConfig();
        config.Types["Epic"] = new TypeConfig
        {
            Facets = ["plannable"],
            AllowedChildTypes = ["Task"],
        };

        var result = ConfigValidator.Validate(config);

        result.Errors.ShouldNotContain(d => d.RuleId == "V-8");
    }

    [Fact]
    public void V8_AllowedChildTypesUndefined_ProducesError()
    {
        var config = ValidConfig();
        config.Types["Epic"] = new TypeConfig
        {
            Facets = ["plannable"],
            AllowedChildTypes = ["NonExistent"],
        };

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d =>
            d.RuleId == "V-8" && d.Message.Contains("NonExistent") && d.Message.Contains("Epic"));
    }

    [Fact]
    public void V8_EmptyAllowedChildTypes_NoError()
    {
        var config = ValidConfig();

        var result = ConfigValidator.Validate(config);

        result.Errors.ShouldNotContain(d => d.RuleId == "V-8");
    }

    #endregion

    #region V-9/V-10: type definition and template file checks

    [Fact]
    public void V9_TypeDefinitionFileMissing_ProducesWarning()
    {
        var config = ValidConfig();
        var repoRoot = CreateTempRepoRoot();

        var result = ConfigValidator.Validate(config, repoRoot);

        result.IsValid.ShouldBeTrue();
        result.Warnings.ShouldContain(d => d.RuleId == "V-9");
    }

    [Fact]
    public void V9_TypeDefinitionFileExists_NoWarning()
    {
        var config = SingleTypeConfig("Bug");
        var repoRoot = CreateTempRepoRoot();
        CreateFile(repoRoot, ".polyphony-config", "work-item-types", "bug.md");

        var result = ConfigValidator.Validate(config, repoRoot);

        result.Warnings.ShouldNotContain(d => d.RuleId == "V-9");
    }

    [Fact]
    public void V10_TemplateFileMissing_ProducesWarning()
    {
        var config = ValidConfig();
        var repoRoot = CreateTempRepoRoot();

        var result = ConfigValidator.Validate(config, repoRoot);

        result.Warnings.ShouldContain(d => d.RuleId == "V-10");
    }

    [Fact]
    public void V10_TemplateFileExists_NoWarning()
    {
        var config = SingleTypeConfig("Bug");
        var repoRoot = CreateTempRepoRoot();
        CreateFile(repoRoot, ".polyphony-config", "work-item-types", "templates", "bug-template.md");

        var result = ConfigValidator.Validate(config, repoRoot);

        result.Warnings.ShouldNotContain(d => d.RuleId == "V-10");
    }

    #endregion

    #region V-9/V-10: type names with spaces (slug normalization)

    [Fact]
    public void V9_TypeNameWithSpaces_NormalizesToSlug()
    {
        var config = SingleTypeConfig("Task Group");
        var repoRoot = CreateTempRepoRoot();
        CreateFile(repoRoot, ".polyphony-config", "work-item-types", "task-group.md");

        var result = ConfigValidator.Validate(config, repoRoot);

        result.Warnings.ShouldNotContain(d => d.RuleId == "V-9");
    }

    [Fact]
    public void V10_TypeNameWithSpaces_NormalizesToSlug()
    {
        var config = SingleTypeConfig("Task Group");
        var repoRoot = CreateTempRepoRoot();
        CreateFile(repoRoot, ".polyphony-config", "work-item-types", "templates", "task-group-template.md");

        var result = ConfigValidator.Validate(config, repoRoot);

        result.Warnings.ShouldNotContain(d => d.RuleId == "V-10");
    }

    [Fact]
    public void ToSlug_NormalizesSpacesToHyphens()
    {
        ConfigValidator.ToSlug("Task Group").ShouldBe("task-group");
    }

    [Fact]
    public void ToSlug_LowercasesName()
    {
        ConfigValidator.ToSlug("Epic").ShouldBe("epic");
    }

    [Fact]
    public void ToSlug_HandlesMultipleSpaces()
    {
        ConfigValidator.ToSlug("User Story Item").ShouldBe("user-story-item");
    }

    #endregion

    #region V-11 through V-14: agent guidance and profile files

    [Fact]
    public void V11_RoleGuidanceMissing_ProducesWarningPerRole()
    {
        var config = ValidConfig();
        var repoRoot = CreateTempRepoRoot();
        // Don't create any agent-guidance/<role>.md files.

        var result = ConfigValidator.Validate(config, repoRoot);

        foreach (var role in new[] { "architect", "coder", "reviewer" })
        {
            result.Warnings.ShouldContain(d =>
                d.RuleId == "V-11" && d.Message.Contains($"agent-guidance/{role}.md"));
        }
    }

    [Fact]
    public void V11_AllRoleGuidanceFilesPresent_NoWarning()
    {
        var config = ValidConfig();
        var repoRoot = CreateTempRepoRoot();
        foreach (var role in new[] { "architect", "coder", "reviewer" })
        {
            CreateFile(repoRoot, ".polyphony-config", "agent-guidance", $"{role}.md");
        }

        var result = ConfigValidator.Validate(config, repoRoot);

        result.Warnings.ShouldNotContain(d => d.RuleId == "V-11");
    }

    [Fact]
    public void V11_TypeRefinementSubdirectory_NotWarnedAsMissing()
    {
        // Per-type refinements at agent-guidance/<role>/<typeslug>.md are optional;
        // their absence must not produce V-11 warnings.
        var config = ValidConfig();
        var repoRoot = CreateTempRepoRoot();
        foreach (var role in new[] { "architect", "coder", "reviewer" })
        {
            CreateFile(repoRoot, ".polyphony-config", "agent-guidance", $"{role}.md");
        }

        var result = ConfigValidator.Validate(config, repoRoot);

        // No warning mentions any type slug — only the three role files matter.
        foreach (var typeName in config.Types.Keys)
        {
            var slug = ConfigValidator.ToSlug(typeName);
            result.Warnings.ShouldNotContain(d =>
                d.RuleId == "V-11" && d.Message.Contains($"/{slug}.md"));
        }
    }

    [Fact]
    public void V11_OnlyOneRoleFilePresent_OtherTwoStillWarn()
    {
        var config = ValidConfig();
        var repoRoot = CreateTempRepoRoot();
        CreateFile(repoRoot, ".polyphony-config", "agent-guidance", "architect.md");

        var result = ConfigValidator.Validate(config, repoRoot);

        result.Warnings.ShouldNotContain(d =>
            d.RuleId == "V-11" && d.Message.Contains("agent-guidance/architect.md"));
        result.Warnings.ShouldContain(d =>
            d.RuleId == "V-11" && d.Message.Contains("agent-guidance/coder.md"));
        result.Warnings.ShouldContain(d =>
            d.RuleId == "V-11" && d.Message.Contains("agent-guidance/reviewer.md"));
    }

    [Fact]
    public void V14_ProfileYamlMissing_ProducesWarning()
    {
        var config = ValidConfig();
        var repoRoot = CreateTempRepoRoot();

        var result = ConfigValidator.Validate(config, repoRoot);

        result.Warnings.ShouldContain(d => d.RuleId == "V-14");
    }

    [Fact]
    public void V14_ProfileYamlExists_NoWarning()
    {
        var config = ValidConfig();
        var repoRoot = CreateTempRepoRoot();
        CreateFile(repoRoot, ".polyphony-config", "profile.yaml");

        var result = ConfigValidator.Validate(config, repoRoot);

        result.Warnings.ShouldNotContain(d => d.RuleId == "V-14");
    }

    #endregion

    #region Integration / composite scenarios

    [Fact]
    public void ValidConfig_PassesAllRules()
    {
        var config = ValidConfig();

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void ValidConfig_WithAllFiles_NoWarnings()
    {
        var config = SingleTypeConfig("Bug");
        var repoRoot = CreateTempRepoRoot();

        CreateFile(repoRoot, ".polyphony-config", "work-item-types", "bug.md");
        CreateFile(repoRoot, ".polyphony-config", "work-item-types", "templates", "bug-template.md");
        CreateFile(repoRoot, ".polyphony-config", "agent-guidance", "architect.md");
        CreateFile(repoRoot, ".polyphony-config", "agent-guidance", "coder.md");
        CreateFile(repoRoot, ".polyphony-config", "agent-guidance", "reviewer.md");
        CreateFile(repoRoot, ".polyphony-config", "profile.yaml");

        var result = ConfigValidator.Validate(config, repoRoot);

        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
        result.Warnings.ShouldNotContain(w => w.RuleId == "V-11");
    }

    [Fact]
    public void MultipleErrors_AllReported()
    {
        var config = new ProcessConfig
        {
            ProcessTemplate = "",
            Types = new Dictionary<string, TypeConfig>(),
            Transitions = new Dictionary<string, Dictionary<string, string>>(),
        };

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "V-1");
        result.Errors.ShouldContain(d => d.RuleId == "V-2");
    }

    [Fact]
    public void NoRepoRoot_SkipsFileChecks()
    {
        var config = ValidConfig();

        var result = ConfigValidator.Validate(config, repoRoot: null);

        result.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void AllErrorDiagnostics_HaveCorrectSeverity()
    {
        var config = new ProcessConfig
        {
            ProcessTemplate = "",
            Types = new Dictionary<string, TypeConfig>(),
            Transitions = new Dictionary<string, Dictionary<string, string>>(),
        };

        var result = ConfigValidator.Validate(config);

        result.Errors.ShouldAllBe(d => d.Severity == ConfigValidationSeverity.Error);
    }

    [Fact]
    public void AllWarningDiagnostics_HaveCorrectSeverity()
    {
        var config = ValidConfig();
        var repoRoot = CreateTempRepoRoot();

        var result = ConfigValidator.Validate(config, repoRoot);

        result.Warnings.ShouldAllBe(d => d.Severity == ConfigValidationSeverity.Warning);
    }

    #endregion

    #region V-19: execution_mode must be a known value

    [Fact]
    public void V19_ExecutionModeUnset_NoError()
    {
        var config = ValidConfig();

        var result = ConfigValidator.Validate(config);

        result.Errors.ShouldNotContain(d => d.RuleId == "V-19");
    }

    [Fact]
    public void V19_ExecutionModeParallel_NoError()
    {
        var config = ValidConfig();
        config.Types["Epic"].ExecutionMode = Polyphony.Sdlc.ExecutionMode.Parallel;

        var result = ConfigValidator.Validate(config);

        result.Errors.ShouldNotContain(d => d.RuleId == "V-19");
    }

    [Fact]
    public void V19_ExecutionModePlanThenImplement_NoError()
    {
        var config = ValidConfig();
        config.Types["Epic"].ExecutionMode = Polyphony.Sdlc.ExecutionMode.PlanThenImplement;

        var result = ConfigValidator.Validate(config);

        result.Errors.ShouldNotContain(d => d.RuleId == "V-19");
    }

    [Fact]
    public void V19_ExecutionModeUnknown_ProducesError()
    {
        var config = ValidConfig();
        config.Types["Epic"].ExecutionMode = "serial";

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d =>
            d.RuleId == "V-19"
            && d.Message.Contains("Epic")
            && d.Message.Contains("serial"));
    }

    [Fact]
    public void V19_ExecutionModeWhitespace_TreatedAsUnsetNoError()
    {
        // Whitespace mirrors the resolver's "unset" treatment — falls back
        // to the documented default; no validation error.
        var config = ValidConfig();
        config.Types["Epic"].ExecutionMode = "   ";

        var result = ConfigValidator.Validate(config);

        result.Errors.ShouldNotContain(d => d.RuleId == "V-19");
    }

    #endregion

    #region V-21: per-type state→category mapping (issue #281)

    [Fact]
    public void V21_MissingStatesBlock_TypeWithTransitions_ProducesError()
    {
        var config = ValidConfig();
        config.States.Clear();

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "V-21" && d.Message.Contains("Epic"));
        result.Errors.ShouldContain(d => d.RuleId == "V-21" && d.Message.Contains("Task"));
    }

    [Fact]
    public void V21_StatesEmptyForType_ProducesError()
    {
        var config = ValidConfig();
        config.States["Epic"] = new Dictionary<string, string>();

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "V-21" && d.Message.Contains("Epic"));
    }

    [Fact]
    public void V21_InvalidCategoryName_ProducesError()
    {
        var config = ValidConfig();
        config.States["Epic"]["Doing"] = "active"; // invalid — must be "in_progress"

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "V-21" && d.Message.Contains("active"));
    }

    [Fact]
    public void V21_EmptyCategoryString_ProducesError()
    {
        var config = ValidConfig();
        config.States["Epic"]["Doing"] = "";

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "V-21" && d.Message.Contains("Doing"));
    }

    [Fact]
    public void V21_TransitionTargetNotInStates_ProducesError()
    {
        var config = ValidConfig();
        config.States["Epic"].Remove("Done");

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.RuleId == "V-21" && d.Message.Contains("Done"));
    }

    [Fact]
    public void V21_StatesWithoutTransitionsForType_NoError()
    {
        // A type can declare states even if it has no transitions — useful for
        // pure container types whose items are merely categorized but never
        // transitioned by polyphony events.
        var config = ValidConfig();
        config.Transitions.Remove("Epic");

        var result = ConfigValidator.Validate(config);

        result.Errors.ShouldNotContain(d => d.RuleId == "V-21" && d.Message.Contains("Epic"));
    }

    [Fact]
    public void V21_AllCategoriesValid_NoError()
    {
        var config = ValidConfig();

        var result = ConfigValidator.Validate(config);

        result.Errors.ShouldNotContain(d => d.RuleId == "V-21");
    }

    #endregion

    private static ProcessConfig ValidConfig()
    {
        return new ProcessConfig
        {
            ProcessTemplate = "Basic",
            Types = new Dictionary<string, TypeConfig>
            {
                ["Epic"] = new TypeConfig { Facets = ["plannable"] },
                ["Task"] = new TypeConfig { Facets = ["implementable"] },
            },
            Transitions = new Dictionary<string, Dictionary<string, string>>
            {
                ["Epic"] = new Dictionary<string, string>
                {
                    ["begin_planning"] = "Doing",
                    ["all_children_complete"] = "Done",
                },
                ["Task"] = new Dictionary<string, string>
                {
                    ["begin_implementation"] = "Doing",
                    ["implementation_complete"] = "Done",
                },
            },
            States = new Dictionary<string, Dictionary<string, string>>
            {
                ["Epic"] = new Dictionary<string, string>
                {
                    ["To Do"] = "proposed",
                    ["Doing"] = "in_progress",
                    ["Done"] = "completed",
                },
                ["Task"] = new Dictionary<string, string>
                {
                    ["To Do"] = "proposed",
                    ["Doing"] = "in_progress",
                    ["Done"] = "completed",
                },
            },
        };
    }

    private static ProcessConfig SingleTypeConfig(string typeName)
    {
        return new ProcessConfig
        {
            ProcessTemplate = "Basic",
            Types = new Dictionary<string, TypeConfig>
            {
                [typeName] = new TypeConfig { Facets = ["implementable"] },
            },
            Transitions = new Dictionary<string, Dictionary<string, string>>
            {
                [typeName] = new Dictionary<string, string>
                {
                    ["begin_implementation"] = "Doing",
                },
            },
            States = new Dictionary<string, Dictionary<string, string>>
            {
                [typeName] = new Dictionary<string, string>
                {
                    ["To Do"] = "proposed",
                    ["Doing"] = "in_progress",
                    ["Done"] = "completed",
                },
            },
        };
    }

    private static string CreateTempRepoRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"polyphony-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateFile(params string[] segments)
    {
        var path = Path.Combine(segments);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, "");
    }
}


