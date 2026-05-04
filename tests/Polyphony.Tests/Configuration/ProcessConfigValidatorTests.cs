using Polyphony.Configuration;
using Shouldly;
using System.Collections.Generic;
using Xunit;

namespace Polyphony.Tests.Configuration;

public sealed class ProcessConfigValidatorTests
{
    [Fact]
    public void ValidateParentRules_NoParents_NoErrors()
    {
        var config = new ProcessConfig
        {
            Types = new Dictionary<string, TypeConfig>
            {
                ["Epic"] = new TypeConfig { Capabilities = new[] { "plannable" } },
                ["Task"] = new TypeConfig { Capabilities = new[] { "implementable" } }
            }
        };
        var errors = ProcessConfigValidator.ValidateParentRules(config);
        errors.ShouldBeEmpty();
    }

    [Fact]
    public void ValidateParentRules_ParentExists_NoErrors()
    {
        var config = new ProcessConfig
        {
            Types = new Dictionary<string, TypeConfig>
            {
                ["Epic"] = new TypeConfig { Capabilities = new[] { "plannable" } },
                ["Task"] = new TypeConfig { Capabilities = new[] { "implementable" }, Parent = "Epic" }
            }
        };
        var errors = ProcessConfigValidator.ValidateParentRules(config);
        errors.ShouldBeEmpty();
    }

    [Fact]
    public void ValidateParentRules_ParentMissing_ReturnsV15Error()
    {
        var config = new ProcessConfig
        {
            Types = new Dictionary<string, TypeConfig>
            {
                ["Task"] = new TypeConfig { Capabilities = new[] { "implementable" }, Parent = "Epic" }
            }
        };
        var errors = ProcessConfigValidator.ValidateParentRules(config);
        errors.ShouldContain(e => e.Contains("V-15"));
    }

    [Fact]
    public void ValidateParentRules_CycleDetected_ReturnsV16Error()
    {
        var config = new ProcessConfig
        {
            Types = new Dictionary<string, TypeConfig>
            {
                ["Epic"] = new TypeConfig { Capabilities = new[] { "plannable" }, Parent = "Task" },
                ["Task"] = new TypeConfig { Capabilities = new[] { "implementable" }, Parent = "Epic" }
            }
        };
        var errors = ProcessConfigValidator.ValidateParentRules(config);
        errors.ShouldContain(e => e.Contains("V-16"));
    }

    [Fact]
    public void ValidateParentRules_IndirectCycleDetected_ReturnsV16Error()
    {
        var config = new ProcessConfig
        {
            Types = new Dictionary<string, TypeConfig>
            {
                ["Epic"] = new TypeConfig { Capabilities = new[] { "plannable" }, Parent = "Task" },
                ["Task"] = new TypeConfig { Capabilities = new[] { "implementable" }, Parent = "Story" },
                ["Story"] = new TypeConfig { Capabilities = new[] { "implementable" }, Parent = "Epic" }
            }
        };
        var errors = ProcessConfigValidator.ValidateParentRules(config);
        errors.ShouldContain(e => e.Contains("V-16"));
    }
}
