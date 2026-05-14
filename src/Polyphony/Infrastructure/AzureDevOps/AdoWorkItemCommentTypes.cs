using System.Text.Json.Serialization;

namespace Polyphony.Infrastructure.AzureDevOps;

/// <summary>
/// Public projection of a single ADO work item discussion comment, used
/// in the reset archive sidecar and in the <see cref="IWorkItemCommentClient"/>
/// return value.
/// </summary>
public sealed record AdoWorkItemComment
{
    public required int WorkItemId { get; init; }
    public required long CommentId { get; init; }
    public required string Text { get; init; }
    public required string CreatedBy { get; init; }
    public string? CreatedDate { get; init; }
}

/// <summary>
/// Wire-level envelope for the ADO
/// <c>GET /_apis/wit/workItems/{id}/comments?api-version=7.1-preview.4</c>
/// response. ADO returns camelCase, so explicit property-name attributes
/// override the context's snake_case naming policy.
/// </summary>
public sealed class AdoWorkItemCommentListResponse
{
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("comments")]
    public List<AdoWorkItemCommentRaw>? Comments { get; set; }

    [JsonPropertyName("continuationToken")]
    public string? ContinuationToken { get; set; }
}

/// <summary>
/// Wire-level shape of a single comment inside
/// <see cref="AdoWorkItemCommentListResponse"/>. Mapped to the public
/// <see cref="AdoWorkItemComment"/> by the client implementation.
/// </summary>
public sealed class AdoWorkItemCommentRaw
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("createdBy")]
    public AdoCommentIdentity? CreatedBy { get; set; }

    [JsonPropertyName("createdDate")]
    public string? CreatedDate { get; set; }
}

/// <summary>
/// Identity sub-object inside <see cref="AdoWorkItemCommentRaw"/>.
/// </summary>
public sealed class AdoCommentIdentity
{
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("uniqueName")]
    public string? UniqueName { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }
}
