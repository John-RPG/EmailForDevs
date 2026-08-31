using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mail.Core.Search;

/// <summary>
/// Compact JSON form of the query AST, used for saved searches and editor state:
///   {"and":[{"prop":"From","op":"Contains","values":["alice"]},{"text":"invoice"}]}
/// Dates may be ISO-8601 strings; the compiler interprets them per property type.
/// </summary>
public static class QueryJson
{
    public static string Serialize(QueryNode node) => ToNode(node).ToJsonString();

    public static QueryNode Deserialize(string json) =>
        FromNode(JsonNode.Parse(json) ?? throw new JsonException("Empty query document."));

    static JsonNode ToNode(QueryNode node) => node switch
    {
        GroupNode g => new JsonObject
        {
            [g.Op == GroupOp.And ? "and" : "or"] = new JsonArray([.. g.Children.Select(ToNode)]),
        },
        NotNode n => new JsonObject { ["not"] = ToNode(n.Inner) },
        TextNode t => new JsonObject { ["text"] = t.Term },
        ConditionNode c => new JsonObject
        {
            ["prop"] = c.Property.ToString(),
            ["op"] = c.Operator.ToString(),
            ["values"] = new JsonArray([.. c.Values.Select(ToValue)]),
        },
        _ => throw new JsonException($"Unknown node type {node.GetType().Name}."),
    };

    static JsonNode? ToValue(object? value) => value switch
    {
        null => null,
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        string s => JsonValue.Create(s),
        DateTimeOffset dto => JsonValue.Create(dto.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
        DateTime dt => JsonValue.Create(dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
        _ => throw new JsonException($"Unsupported query value type {value.GetType().Name}."),
    };

    static QueryNode FromNode(JsonNode node)
    {
        var obj = node.AsObject();
        if (obj.TryGetPropertyValue("and", out var and))
            return new GroupNode(GroupOp.And, Children(and!));
        if (obj.TryGetPropertyValue("or", out var or))
            return new GroupNode(GroupOp.Or, Children(or!));
        if (obj.TryGetPropertyValue("not", out var not))
            return new NotNode(FromNode(not!));
        if (obj.TryGetPropertyValue("text", out var text))
            return new TextNode(text!.GetValue<string>());
        if (obj.TryGetPropertyValue("prop", out var prop))
        {
            var property = Enum.Parse<MessageProperty>(prop!.GetValue<string>(), ignoreCase: true);
            var op = Enum.Parse<ConditionOperator>(
                obj["op"]!.GetValue<string>(), ignoreCase: true);
            var values = obj.TryGetPropertyValue("values", out var vals) && vals is JsonArray arr
                ? arr.Select(FromValue).ToArray()
                : [];
            return new ConditionNode(property, op, values);
        }
        throw new JsonException($"Unrecognized query node: {obj.ToJsonString()}");
    }

    static IReadOnlyList<QueryNode> Children(JsonNode node) =>
        [.. node.AsArray().Select(c => FromNode(c ?? throw new JsonException("Null child in group.")))];

    static object? FromValue(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => throw new JsonException($"Unsupported query value: {node.ToJsonString()}"),
    };
}
