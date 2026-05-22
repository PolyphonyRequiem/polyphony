using System.Text.Json;
using System.Text.Json.Serialization;

namespace Polyphony.Journal;

[JsonConverter(typeof(ResourceMutationJsonConverter))]
public enum ResourceMutation
{
    CreatedNow,
    DeletedNow,
    Changed,
    NoChangedAlreadySatisfied,
    NoChangedExternalAlreadyPresent,
}

internal static class ResourceMutationCodec
{
    public static string ToStorage(ResourceMutation mutation) => mutation switch
    {
        ResourceMutation.CreatedNow => "created_now",
        ResourceMutation.DeletedNow => "deleted_now",
        ResourceMutation.Changed => "changed",
        ResourceMutation.NoChangedAlreadySatisfied => "no_changed_already_satisfied",
        ResourceMutation.NoChangedExternalAlreadyPresent => "no_changed_external_already_present",
        _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown resource mutation."),
    };

    public static ResourceMutation Parse(string value) => value switch
    {
        "created_now" => ResourceMutation.CreatedNow,
        "deleted_now" => ResourceMutation.DeletedNow,
        "changed" => ResourceMutation.Changed,
        "no_changed_already_satisfied" => ResourceMutation.NoChangedAlreadySatisfied,
        "no_changed_external_already_present" => ResourceMutation.NoChangedExternalAlreadyPresent,
        _ => throw new JsonException($"Unknown resource mutation '{value}'."),
    };
}

public sealed class ResourceMutationJsonConverter : JsonConverter<ResourceMutation>
{
    public override ResourceMutation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (string.IsNullOrEmpty(value))
            throw new JsonException("Resource mutation must be a non-empty string.");

        return ResourceMutationCodec.Parse(value);
    }

    public override void Write(Utf8JsonWriter writer, ResourceMutation value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(ResourceMutationCodec.ToStorage(value));
    }
}
