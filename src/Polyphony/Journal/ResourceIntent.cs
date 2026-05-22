using System.Text.Json;
using System.Text.Json.Serialization;

namespace Polyphony.Journal;

[JsonConverter(typeof(ResourceIntentJsonConverter))]
public enum ResourceIntent
{
    EnsurePresent,
    EnsureAbsent,
    SetState,
    UpdateMetadata,
    AdvancePointer,
    Attach,
    Detach,
    Observe,
}

internal static class ResourceIntentCodec
{
    public static string ToStorage(ResourceIntent intent) => intent switch
    {
        ResourceIntent.EnsurePresent => "ensure_present",
        ResourceIntent.EnsureAbsent => "ensure_absent",
        ResourceIntent.SetState => "set_state",
        ResourceIntent.UpdateMetadata => "update_metadata",
        ResourceIntent.AdvancePointer => "advance_pointer",
        ResourceIntent.Attach => "attach",
        ResourceIntent.Detach => "detach",
        ResourceIntent.Observe => "observe",
        _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, "Unknown resource intent."),
    };

    public static ResourceIntent Parse(string value) => value switch
    {
        "ensure_present" => ResourceIntent.EnsurePresent,
        "ensure_absent" => ResourceIntent.EnsureAbsent,
        "set_state" => ResourceIntent.SetState,
        "update_metadata" => ResourceIntent.UpdateMetadata,
        "advance_pointer" => ResourceIntent.AdvancePointer,
        "attach" => ResourceIntent.Attach,
        "detach" => ResourceIntent.Detach,
        "observe" => ResourceIntent.Observe,
        _ => throw new JsonException($"Unknown resource intent '{value}'."),
    };
}

public sealed class ResourceIntentJsonConverter : JsonConverter<ResourceIntent>
{
    public override ResourceIntent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (string.IsNullOrEmpty(value))
            throw new JsonException("Resource intent must be a non-empty string.");

        return ResourceIntentCodec.Parse(value);
    }

    public override void Write(Utf8JsonWriter writer, ResourceIntent value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(ResourceIntentCodec.ToStorage(value));
    }
}
