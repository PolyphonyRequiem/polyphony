using Polyphony.Annotations;
using Polyphony.Journal;

namespace Polyphony.Commands;

[VerbGroup("")]
public sealed partial class JournalCommands(IJournalReader store)
{
    private readonly IJournalReader _store = store;

    private static void EmitError(string message, string? path = null)
    {
        if (string.IsNullOrEmpty(path))
        {
            Console.WriteLine($$"""{"error":"{{EscapeJsonString(message)}}"}""");
            return;
        }

        Console.WriteLine($$"""{"error":"{{EscapeJsonString(message)}}","path":"{{EscapeJsonString(path)}}"}""");
    }

    private static string EscapeJsonString(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
}
