// Vendored from the monorepo: MapleKiosk/Infrastructure/VoiceAgent/SPC.Infrastructure.VoiceAgent/GeminiLive/GeminiSchemaAdapter.cs
// Kept as close to the original as possible (it is tuned); only namespaces and dependencies the site
// lacks were changed. See Assistant/Voice/WebsiteVoiceEndpoints.cs for what was stripped.

using System.Text.Json.Nodes;

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// Converts a general JSON Schema into the narrow subset Gemini's function declarations accept.
///
/// <para>Gemini validates <c>parameters</c> against its own <c>Schema</c> protobuf, not against JSON
/// Schema, and protobuf JSON parsing rejects — rather than ignores — anything it does not recognise. Two
/// things our MCP tools emit routinely are fatal there:</para>
/// <list type="bullet">
/// <item><c>"type": ["string", "null"]</c> for an optional parameter. <c>type</c> is a single enum in
/// Gemini's Schema, so an array fails with <c>Unknown name "type"</c>. Gemini spells the same idea
/// <c>"type": "string", "nullable": true</c>.</item>
/// <item><c>"default": null</c>, which has no field in Gemini's Schema at all.</item>
/// </list>
///
/// <para>So this is a whitelist, not a blacklist: anything not known to be part of the subset is dropped.
/// A rejected <c>setup</c> takes down the whole session, not just one tool, so being generous here would
/// mean one new schema keyword silently breaking every conversation.</para>
///
/// <para>Lives beside the Gemini provider on purpose — each vendor shapes tool schemas to its own dialect,
/// and putting this in the shared sanitizer would make one vendor's quirks everyone's problem.</para>
/// </summary>
internal static class GeminiSchemaAdapter
{
    /// <summary>The fields Gemini's Schema actually defines.</summary>
    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        "type", "format", "description", "nullable", "enum", "items", "properties", "required",
    };

    public static JsonNode? Adapt(JsonNode? node) => node switch
    {
        JsonObject obj => AdaptObject(obj),
        JsonArray arr => new JsonArray(arr.Select(item => Adapt(item?.DeepClone())).ToArray()),
        _ => node?.DeepClone(),
    };

    private static JsonObject AdaptObject(JsonObject source)
    {
        var result = new JsonObject();
        var nullable = false;

        foreach (var (key, value) in source)
        {
            if (!Supported.Contains(key))
                continue;

            switch (key)
            {
                case "type" when value is JsonArray types:
                {
                    // ["string","null"] -> type string + nullable. Keep the first concrete type; a union
                    // of two real types cannot be expressed here, and picking one beats failing the setup.
                    foreach (var entry in types)
                    {
                        var name = entry?.GetValue<string>();
                        if (string.Equals(name, "null", StringComparison.OrdinalIgnoreCase))
                            nullable = true;
                        else if (name is not null && result["type"] is null)
                            result["type"] = name;
                    }
                    break;
                }

                case "properties" when value is JsonObject properties:
                {
                    var adapted = new JsonObject();
                    foreach (var (name, schema) in properties)
                        adapted[name] = Adapt(schema);
                    result["properties"] = adapted;
                    break;
                }

                case "items":
                    result["items"] = Adapt(value);
                    break;

                default:
                    result[key] = value?.DeepClone();
                    break;
            }
        }

        if (nullable)
            result["nullable"] = true;

        // An object schema with no type is ambiguous to the validator; every tool parameter bag is an object.
        if (result["type"] is null && result["properties"] is not null)
            result["type"] = "object";

        return result;
    }
}
