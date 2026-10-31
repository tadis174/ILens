using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ILens;

/// <summary>
/// CallTool filter that reads each argument's value against the tool's own input schema before
/// the SDK binds it, and answers a value the parameter does not take with an error naming the
/// parameter and what it takes.
///
/// Left to itself, the SDK's parameter binder answers such a value with a raw serializer failure —
/// <c>The JSON value could not be converted to ILens.AccessibilityFilter. Path: $ | LineNumber: 0 |
/// BytePositionInLine: 7</c> — which names an internal type and a byte offset in a document the
/// caller never wrote, names no parameter, and stops at the first value it cannot convert. It also
/// binds values the schema does not list and answers another question with them: a number in
/// place of a listed name, also one no name has, and several names in one comma-separated text,
/// which it reads as one of them. So no such value reaches the binder.
///
/// A value with exactly one reading is rewritten to the form the schema has instead of refused.
/// Accepting it costs the caller nothing, and unlike <see cref="UnknownArgumentFilter"/>, which
/// rejects because a dropped filter yields a plausible wrong answer, there is no second reading
/// to be wrong about:
/// <list type="bullet">
/// <item>a lone value where the schema declares a list becomes a one-element list;</item>
/// <item>a listed name in another case, without its underscores or with spaces around it becomes
/// the listed name;</item>
/// <item>a whole number given as text, with a zero fraction or with an exponent becomes the plain
/// number;</item>
/// <item><c>"true"</c> or <c>"false"</c> given as text becomes the boolean;</item>
/// <item>null for a parameter the schema does not require is taken out of the call, so the
/// parameter's default applies whatever its type.</item>
/// </list>
///
/// The check is schema-driven, so it covers every parameter of every tool with no per-tool
/// maintenance. It knows the shapes the tool list has: a string, a string with listed values, an
/// integer, a boolean, and a list of either kind of string. A value for a parameter of any other
/// shape is passed on unread; the schema test fails when a tool declares one.
/// </summary>
internal static class ArgumentValueFilter
{
    /// <summary>
    /// The most characters of a refused value an error repeats, so that a long value does not
    /// come back to the caller whole.
    /// </summary>
    private const int EchoLength = 40;

    /// <summary>Writes a refused value with only the escapes JSON requires.</summary>
    private static readonly JsonWriterOptions EchoOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Install the filter. Register it <em>after</em> <see cref="ToolErrorFilter"/> so that
    /// filter stays outermost and formats what is thrown here, and after
    /// <see cref="UnknownArgumentFilter"/> so an unrecognized argument is rejected on the
    /// name it was given, before any value is read.
    /// </summary>
    public static void Install(McpServerOptions options)
    {
        // Same defensive coalescing as the other filters — today's SDK initializes these,
        // but a future version could make them lazy.
        options.Filters ??= new();
        options.Filters.Request ??= new();
        options.Filters.Request.CallToolFilters ??=
            new List<McpRequestFilter<CallToolRequestParams, CallToolResult>>();

        options.Filters.Request.CallToolFilters.Add(next => (request, cancellationToken) =>
        {
            Check(request);
            return next(request, cancellationToken);
        });
    }

    private static void Check(RequestContext<CallToolRequestParams> request)
    {
        // MatchedPrimitive is the tool the SDK resolved this call to. Anything else means
        // we cannot know the declared parameter shapes, so there is nothing to read against.
        if (request.MatchedPrimitive is not McpServerTool tool)
            return;

        var schema = tool.ProtocolTool.InputSchema;
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
            return;

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var names) && names.ValueKind == JsonValueKind.Array)
            foreach (var name in names.EnumerateArray())
                required.Add(name.GetString());

        var arguments = request.Params?.Arguments;

        // The copy is made only once something needs rewriting, which a well-formed call never does.
        Dictionary<string, JsonElement> rewritten = null;

        // Every error of the call is collected, in the order the tool declares its parameters,
        // so a caller with two wrong values learns of both in one answer.
        var errors = new List<string>();
        foreach (var parameter in properties.EnumerateObject())
        {
            JsonElement value = default;
            bool given = arguments != null && arguments.TryGetValue(parameter.Name, out value);
            if (!given || value.ValueKind == JsonValueKind.Null)
            {
                if (required.Contains(parameter.Name))
                    errors.Add($"Missing required argument '{parameter.Name}'.");
                else if (given)
                    (rewritten ??= new Dictionary<string, JsonElement>(arguments)).Remove(parameter.Name);
                continue;
            }

            string expected = Take(parameter.Value, value, out var taken);
            if (expected != null)
                errors.Add($"Invalid value {Echo(value)} for argument '{parameter.Name}'. {expected}");
            else if (taken.ValueKind != JsonValueKind.Undefined)
                (rewritten ??= new Dictionary<string, JsonElement>(arguments))[parameter.Name] = taken;
        }

        if (errors.Count > 0)
            throw new ArgumentException(string.Join(" ", errors));

        if (rewritten != null)
            request.Params.Arguments = rewritten;
    }

    /// <summary>
    /// Reads a value as a parameter of the given schema takes it. Returns null when the parameter
    /// takes the value, with <paramref name="taken"/> set where the value has to be rewritten for
    /// the binder and left undefined where it can stay; otherwise returns the sentence saying
    /// what the parameter expects.
    /// </summary>
    private static string Take(JsonElement schema, JsonElement value, out JsonElement taken)
    {
        taken = default;
        switch (DeclaredType(schema))
        {
            case "array":
                return TakeList(schema, value, out taken);
            case "string" when ListedValues(schema) is { } listed:
                return TakeListedName(listed, value, out taken) ? null : $"Valid values: {Names(listed)}.";
            case "string":
                return value.ValueKind == JsonValueKind.String ? null : "Expected a string.";
            case "integer":
                return TakeInteger(value, out taken);
            case "boolean":
                return TakeBoolean(value, out taken);
            default:
                return null;
        }
    }

    /// <summary>
    /// A list, or a lone string, number or boolean, which is read as a list of one. Each element
    /// is read as the schema's <c>items</c> has it.
    /// </summary>
    private static string TakeList(JsonElement schema, JsonElement value, out JsonElement taken)
    {
        taken = default;
        JsonElement items = schema.TryGetProperty("items", out var declared) ? declared : default;
        string expected =
            ListedValues(items) is { } listed ? $"Expected a list of: {Names(listed)}."
            : DeclaredType(items) == "string" ? "Expected a list of strings."
            : "Expected a list.";

        bool lone = value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            or JsonValueKind.True or JsonValueKind.False;
        if (!lone && value.ValueKind != JsonValueKind.Array)
            return expected;

        bool changed = lone;
        var elements = new List<JsonElement>();
        foreach (var element in lone ? [value] : value.EnumerateArray().ToArray())
        {
            if (Take(items, element, out var takenElement) != null)
                return expected;

            changed |= takenElement.ValueKind != JsonValueKind.Undefined;
            elements.Add(takenElement.ValueKind != JsonValueKind.Undefined ? takenElement : element);
        }

        if (changed)
            taken = Json($"[{string.Join(",", elements.Select(element => element.GetRawText()))}]");
        return null;
    }

    /// <summary>
    /// One of the listed names: as listed, or else in a spelling that means exactly one of them,
    /// where case, underscores and surrounding spaces do not count. A number is not taken for
    /// the name at its position: one that no name has would be bound all the same and answered
    /// as if it meant something.
    /// </summary>
    private static bool TakeListedName(JsonElement[] listed, JsonElement value, out JsonElement taken)
    {
        taken = default;
        if (value.ValueKind != JsonValueKind.String)
            return false;

        // The name as listed comes first, so that two listed names which differ only in what
        // the folding drops would each still be taken.
        string given = value.GetString();
        if (listed.Any(name => name.ValueEquals(given)))
            return true;

        string folded = Fold(given);
        var meant = listed.Where(name => Fold(name.GetString()) == folded).ToList();
        if (meant.Count != 1)
            return false;

        taken = meant[0];
        return true;
    }

    /// <summary>
    /// A whole number within the range of an int, written as a number or as text, with or without
    /// a zero fraction or an exponent. JSON Schema counts <c>1.0</c> as an integer; the binder
    /// does not.
    /// </summary>
    private static string TakeInteger(JsonElement value, out JsonElement taken)
    {
        taken = default;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _))
            return null;

        string text = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            _ => null,
        };
        if (text == null ||
            !decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number) ||
            number != decimal.Truncate(number))
            return "Expected an integer.";

        if (number > int.MaxValue)
            return string.Create(CultureInfo.InvariantCulture, $"Expected an integer of at most {int.MaxValue}.");
        if (number < int.MinValue)
            return string.Create(CultureInfo.InvariantCulture, $"Expected an integer of at least {int.MinValue}.");

        taken = Json(((int)number).ToString(CultureInfo.InvariantCulture));
        return null;
    }

    /// <summary>True or false, as a boolean or as that word in text.</summary>
    private static string TakeBoolean(JsonElement value, out JsonElement taken)
    {
        taken = default;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return null;

        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool flag))
        {
            taken = Json(flag ? "true" : "false");
            return null;
        }

        return "Expected true or false.";
    }

    /// <summary>
    /// The type a schema declares, or null when it declares none or more than one. A nullable
    /// parameter is emitted as <c>"type": ["integer", "null"]</c>, so "null" is passed over.
    /// </summary>
    private static string DeclaredType(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type))
            return null;

        if (type.ValueKind == JsonValueKind.String)
            return type.GetString();
        if (type.ValueKind != JsonValueKind.Array)
            return null;

        var declared = type.EnumerateArray()
            .Where(t => t.ValueKind == JsonValueKind.String && !t.ValueEquals("null"))
            .ToList();
        return declared.Count == 1 ? declared[0].GetString() : null;
    }

    /// <summary>The names a schema lists under <c>enum</c>, or null when it lists none.</summary>
    private static JsonElement[] ListedValues(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("enum", out var values) ||
            values.ValueKind != JsonValueKind.Array)
            return null;

        var listed = values.EnumerateArray().ToArray();
        return listed.All(value => value.ValueKind == JsonValueKind.String) ? listed : null;
    }

    private static string Names(JsonElement[] listed) =>
        string.Join(", ", listed.Select(name => name.GetString()));

    private static string Fold(string name) =>
        name.Trim().Replace("_", "").ToLowerInvariant();

    /// <summary>
    /// A refused value as the caller wrote it, cut at <see cref="EchoLength"/>. The SDK hands a
    /// value over written anew by its default encoder, with <c><</c> for <c>&lt;</c> and an
    /// escape for every letter outside ASCII, which would make a type name such as
    /// <c>List&lt;int&gt;</c> come back unreadable. So the value is written once more without them.
    /// </summary>
    private static string Echo(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, EchoOptions))
            value.WriteTo(writer);

        string text = Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
        return text.Length <= EchoLength ? text : text[..EchoLength] + "...";
    }

    /// <summary>
    /// A value from its JSON text, cloned off its parsed document so that it stays valid once
    /// that document is disposed.
    /// </summary>
    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
