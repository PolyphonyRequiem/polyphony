using System.Text.Json;
using System.Text.Json.Serialization;

namespace Polyphony.Journal;

[JsonConverter(typeof(JournalOutcomeJsonConverter))]
public enum JournalOutcome
{
    Success,
    Failure,
    NoOp,
}

internal static class JournalOutcomeCodec
{
    public static string ToStorage(JournalOutcome outcome) => outcome switch
    {
        JournalOutcome.Success => "success",
        JournalOutcome.Failure => "failure",
        JournalOutcome.NoOp => "no_op",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown journal outcome."),
    };

    public static JournalOutcome Parse(string value) => value switch
    {
        "success" => JournalOutcome.Success,
        "failure" => JournalOutcome.Failure,
        "no_op" => JournalOutcome.NoOp,
        _ => throw new JsonException($"Unknown journal outcome '{value}'."),
    };
}

public sealed class JournalOutcomeJsonConverter : JsonConverter<JournalOutcome>
{
    public override JournalOutcome Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (string.IsNullOrEmpty(value))
            throw new JsonException("Journal outcome must be a non-empty string.");

        return JournalOutcomeCodec.Parse(value);
    }

    public override void Write(Utf8JsonWriter writer, JournalOutcome value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(JournalOutcomeCodec.ToStorage(value));
    }
}
