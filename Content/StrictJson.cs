using System.Text.Json;

namespace Train.Content;

internal static class StrictJson
{
    public static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 16
    };
}
