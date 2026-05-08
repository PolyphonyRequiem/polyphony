using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Configuration;

namespace Polyphony.Commands;

/// <summary>
/// Validates the process configuration file against all Polyphony validation rules.
/// Loads the config YAML, runs <see cref="ConfigValidator"/>, and outputs a structured
/// <see cref="ConfigValidationResult"/>. Exit code 0 = valid, non-zero = errors found.
/// </summary>
[VerbGroup("")]
public sealed class ValidateConfigCommand
{
    /// <summary>
    /// Validate process configuration against all Polyphony validation rules.
    /// </summary>
    /// <param name="config">Path to .conductor directory containing process-config.yaml</param>
    /// <param name="output">Output format: json or human</param>
    [Command("validate-config")]
    [VerbResult(typeof(ConfigValidationResult))]
    public int ValidateConfig(string config = ".conductor", string output = "json")
    {
        var configPath = Path.Combine(config, "process-config.yaml");
        var isJson = !string.Equals(output, "human", StringComparison.OrdinalIgnoreCase);

        ProcessConfig processConfig;
        try
        {
            processConfig = ProcessConfigLoader.Load(configPath);
        }
        catch (FileNotFoundException ex)
        {
            return OutputLoadError(ex.Message, isJson);
        }
        catch (InvalidOperationException ex)
        {
            return OutputLoadError(ex.Message, isJson);
        }

        var repoRoot = Path.GetFullPath(Path.Combine(config, ".."));
        var result = ConfigValidator.Validate(processConfig, repoRoot);

        if (isJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.ConfigValidationResult));
        }
        else
        {
            RenderHuman(result);
        }

        return result.IsValid ? ExitCodes.Success : ExitCodes.ConfigError;
    }

    private static int OutputLoadError(string message, bool isJson)
    {
        if (isJson)
        {
            var errorResult = new ConfigValidationResult
            {
                IsValid = false,
                Errors =
                [
                    new ConfigValidationDiagnostic
                    {
                        RuleId = "CONFIG",
                        Message = message,
                        Severity = ConfigValidationSeverity.Error,
                    }
                ],
                Warnings = [],
            };
            Console.WriteLine(JsonSerializer.Serialize(errorResult, PolyphonyJsonContext.Default.ConfigValidationResult));
        }
        else
        {
            Console.WriteLine($"Error: {message}");
        }

        return ExitCodes.ConfigError;
    }

    private static void RenderHuman(ConfigValidationResult result)
    {
        if (result.IsValid && result.Warnings.Length == 0)
        {
            Console.WriteLine("Configuration is valid.");
            return;
        }

        if (result.Errors.Length > 0)
        {
            Console.WriteLine($"Errors ({result.Errors.Length}):");
            foreach (var error in result.Errors)
            {
                Console.WriteLine($"  [{error.RuleId}] {error.Message}");
            }
        }

        if (result.Warnings.Length > 0)
        {
            Console.WriteLine($"Warnings ({result.Warnings.Length}):");
            foreach (var warning in result.Warnings)
            {
                Console.WriteLine($"  [{warning.RuleId}] {warning.Message}");
            }
        }

        Console.WriteLine(result.IsValid
            ? "Configuration is valid (with warnings)."
            : $"Configuration has {result.Errors.Length} error(s).");
    }
}
