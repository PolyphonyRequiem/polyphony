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
    /// Uses the shared <see cref="ConsoleTestLock.AsyncLock"/> semaphore so sync and async
    /// tests serialize against each other (two separate primitives would let parallel
    /// test classes race on <see cref="Console.Out"/>).
    /// </summary>
    protected static (int ExitCode, string Output) CaptureConsole(Func<int> action)
    {
        ConsoleTestLock.AsyncLock.Wait();
        try
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
        finally
        {
            ConsoleTestLock.AsyncLock.Release();
        }
    }

    /// <summary>
    /// Executes an asynchronous command while capturing stdout.
    /// Uses a <see cref="SemaphoreSlim"/> (not <see cref="Monitor"/>) because real async
    /// I/O inside the action (e.g. <c>File.ReadAllTextAsync</c>) can resume on a different
    /// thread, which would break <c>Monitor.Exit</c>'s thread-affinity requirement.
    /// </summary>
    protected static async Task<(int ExitCode, string Output)> CaptureConsoleAsync(Func<Task<int>> action)
    {
        await ConsoleTestLock.AsyncLock.WaitAsync();
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
            ConsoleTestLock.AsyncLock.Release();
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
