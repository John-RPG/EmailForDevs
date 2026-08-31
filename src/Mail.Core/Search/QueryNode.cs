namespace Mail.Core.Search;

public enum GroupOp { And, Or }

/// <summary>
/// The search AST every query front-end produces (filter editor, plain search
/// box, saved searches). Compiles to local SQL+FTS5 and, where expressible,
/// to Graph $filter/$search.
/// </summary>
public abstract record QueryNode
{
    public static GroupNode And(params QueryNode[] children) => new(GroupOp.And, children);
    public static GroupNode Or(params QueryNode[] children) => new(GroupOp.Or, children);
    public static NotNode Not(QueryNode inner) => new(inner);
    public static ConditionNode Where(MessageProperty property, ConditionOperator op, params object?[] values) =>
        new(property, op, values);
    public static TextNode Text(string term) => new(term);
}

/// <summary>and/or over one or more children.</summary>
public sealed record GroupNode(GroupOp Op, IReadOnlyList<QueryNode> Children) : QueryNode;

public sealed record NotNode(QueryNode Inner) : QueryNode;

/// <summary>property ⟨op⟩ value(s); Between takes two values, In takes one or more.</summary>
public sealed record ConditionNode(
    MessageProperty Property, ConditionOperator Operator, IReadOnlyList<object?> Values) : QueryNode;

/// <summary>Bare full-text term matched across subject, body, and participants.</summary>
public sealed record TextNode(string Term) : QueryNode;
