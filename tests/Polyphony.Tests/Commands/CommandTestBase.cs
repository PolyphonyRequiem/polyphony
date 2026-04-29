using Polyphony.Configuration;
using Polyphony.Tests.TestFixtures;
using Twig.Domain.Aggregates;
using Twig.Domain.Services;
using Twig.Infrastructure.Persistence;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Base class for end-to-end command tests. Provides an in-memory SQLite database
/// seeded with test work items, stdout capture, and a default <see cref="ProcessConfig"/>.
/// Each test method gets a fresh database instance (xUnit creates a new class per test).
/// </summary>
public abstract class CommandTestBase : IDisposable
{
    private static readonly object ConsoleLock = new();
    private static readonly object InitLock = new();
    private static bool s_sqliteInitialized;

    protected SqliteCacheStore Store { get; }
    protected SqliteWorkItemRepository Repository { get; }
    protected ProcessConfig Config { get; }

    protected CommandTestBase()
    {
        EnsureSqliteInitialized();
        Store = new SqliteCacheStore("Data Source=:memory:");
        Repository = new SqliteWorkItemRepository(Store, new WorkItemMapper());
        Config = CreateDefaultConfig();
    }

    /// <summary>
    /// Executes a synchronous command while capturing stdout.
    /// Uses a lock to prevent parallel test interference with <see cref="Console.Out"/>.
    /// </summary>
    protected static (int ExitCode, string Output) CaptureConsole(Func<int> action)
    {
        lock (ConsoleLock)
        {
            using var writer = new StringWriter();
            var original = Console.Out;
            Console.SetOut(writer);
            try
            {
                var exitCode = action();
                return (exitCode, writer.ToString().Trim());
            }
            finally
            {
                Console.SetOut(original);
            }
        }
    }

    /// <summary>
    /// Executes an asynchronous command while capturing stdout.
    /// Uses a lock to prevent parallel test interference with <see cref="Console.Out"/>.
    /// </summary>
    protected static async Task<(int ExitCode, string Output)> CaptureConsoleAsync(Func<Task<int>> action)
    {
        // Acquire the lock synchronously, then run the async action inside it.
        // Safe because command methods are CPU-bound once the walker completes.
        Monitor.Enter(ConsoleLock);
        try
        {
            using var writer = new StringWriter();
            var original = Console.Out;
            Console.SetOut(writer);
            try
            {
                var exitCode = await action();
                return (exitCode, writer.ToString().Trim());
            }
            finally
            {
                Console.SetOut(original);
            }
        }
        finally
        {
            Monitor.Exit(ConsoleLock);
        }
    }

    /// <summary>
    /// Seeds work items into the in-memory SQLite database.
    /// </summary>
    protected async Task SeedAsync(params WorkItem[] items)
    {
        foreach (var item in items)
            await Repository.SaveAsync(item);
    }

    private static ProcessConfig CreateDefaultConfig()
    {
        return new ProcessConfigBuilder()
            .WithType("Epic", ["plannable"], new Dictionary<string, string>
            {
                ["begin_planning"] = "Doing",
                ["implementation_complete"] = "Done"
            })
            .WithType("Issue", ["plannable", "implementable"], new Dictionary<string, string>
            {
                ["begin_planning"] = "Doing",
                ["implementation_complete"] = "Done"
            })
            .WithType("Task", ["implementable"], new Dictionary<string, string>
            {
                ["begin_implementation"] = "Doing",
                ["implementation_complete"] = "Done"
            })
            .WithBranchStrategy()
            .Build();
    }

    private static void EnsureSqliteInitialized()
    {
        if (s_sqliteInitialized) return;
        lock (InitLock)
        {
            if (s_sqliteInitialized) return;
            SQLitePCL.Batteries.Init();
            s_sqliteInitialized = true;
        }
    }

    public void Dispose()
    {
        Store.Dispose();
        GC.SuppressFinalize(this);
    }
}
