using System.Globalization;
using System.Text;

namespace Mail.Core.Search;

public sealed record CompiledQuery(string WhereSql, IReadOnlyDictionary<string, object> Parameters);

public sealed class QueryCompilationException(string message) : Exception(message);

/// <summary>
/// Compiles a <see cref="QueryNode"/> tree to a WHERE clause over the mailbox
/// schema ("m" aliases the messages table). Envelope fields use SQL/LIKE,
/// body text uses FTS5, addresses and attachments use EXISTS sub-queries.
/// </summary>
public static class SqliteQueryCompiler
{
    public static CompiledQuery Compile(QueryNode root)
    {
        var ctx = new Context();
        var sql = ctx.Visit(root);
        return new CompiledQuery(sql, ctx.Parameters);
    }

    sealed class Context
    {
        public Dictionary<string, object> Parameters { get; } = [];

        string AddParam(object value)
        {
            var name = "@p" + Parameters.Count.ToString(CultureInfo.InvariantCulture);
            Parameters[name] = value;
            return name;
        }

        public string Visit(QueryNode node) => node switch
        {
            GroupNode g => VisitGroup(g),
            NotNode n => $"NOT ({Visit(n.Inner)})",
            TextNode t => FtsMatch(null, t.Term, negate: false),
            ConditionNode c => VisitCondition(c),
            _ => throw new QueryCompilationException($"Unknown node type {node.GetType().Name}."),
        };

        string VisitGroup(GroupNode g)
        {
            if (g.Children.Count == 0)
                throw new QueryCompilationException("A group must have at least one child.");
            var op = g.Op == GroupOp.And ? " AND " : " OR ";
            return "(" + string.Join(op, g.Children.Select(Visit)) + ")";
        }

        string VisitCondition(ConditionNode c)
        {
            var kind = MessagePropertySchema.KindOf(c.Property);
            if (!MessagePropertySchema.OperatorsFor(kind).Contains(c.Operator))
                throw new QueryCompilationException(
                    $"Operator {c.Operator} is not valid for {c.Property} ({kind}).");
            CheckArity(c);
            return c.Property switch
            {
                MessageProperty.Subject => TextCondition("m.subject", c),
                MessageProperty.AttachmentName =>
                    $"EXISTS (SELECT 1 FROM attachments att WHERE att.message_id = m.id AND {TextCondition("att.file_name", c)})",
                MessageProperty.Body => FtsMatch("body_text", (string?)c.Values[0]
                        ?? throw new QueryCompilationException("Body search needs a term."),
                    negate: c.Operator == ConditionOperator.NotContains),
                MessageProperty.Folder =>
                    $"EXISTS (SELECT 1 FROM folders f WHERE f.id = m.folder_id AND {TextCondition("f.name", c)})",
                MessageProperty.Received => ScalarCondition("m.received_at", c, ToUnixSeconds),
                MessageProperty.Sent => ScalarCondition("m.sent_at", c, ToUnixSeconds),
                MessageProperty.Size => ScalarCondition("m.size", c, ToInt64),
                MessageProperty.IsRead => BoolCondition("m.is_read", c),
                MessageProperty.IsFlagged => BoolCondition("m.is_flagged", c),
                MessageProperty.IsDraft => BoolCondition("m.is_draft", c),
                MessageProperty.HasAttachments => BoolCondition("m.has_attachments", c),
                _ => AddressCondition(c),
            };
        }

        static void CheckArity(ConditionNode c)
        {
            var expected = c.Operator switch
            {
                ConditionOperator.Between => 2,
                ConditionOperator.IsEmpty or ConditionOperator.NotEmpty => 0,
                ConditionOperator.In => -1, // one or more
                _ => 1,
            };
            var ok = expected == -1 ? c.Values.Count >= 1 : c.Values.Count == expected;
            if (!ok)
                throw new QueryCompilationException(
                    $"{c.Operator} on {c.Property} expects {(expected == -1 ? "one or more" : expected.ToString())} value(s), got {c.Values.Count}.");
        }

        // ---- text (LIKE-based, case-insensitive) --------------------------------

        string TextCondition(string col, ConditionNode c) => c.Operator switch
        {
            ConditionOperator.Equals => $"{col} = {AddParam(Str(c.Values[0]))} COLLATE NOCASE",
            ConditionOperator.NotEquals => $"({col} IS NULL OR {col} <> {AddParam(Str(c.Values[0]))} COLLATE NOCASE)",
            ConditionOperator.Contains => Like(col, "%" + EscapeLike(Str(c.Values[0])) + "%"),
            ConditionOperator.NotContains => $"({col} IS NULL OR NOT {Like(col, "%" + EscapeLike(Str(c.Values[0])) + "%")})",
            ConditionOperator.StartsWith => Like(col, EscapeLike(Str(c.Values[0])) + "%"),
            ConditionOperator.EndsWith => Like(col, "%" + EscapeLike(Str(c.Values[0]))),
            ConditionOperator.In =>
                $"{col} COLLATE NOCASE IN ({string.Join(", ", c.Values.Select(v => AddParam(Str(v))))})",
            ConditionOperator.IsEmpty => $"({col} IS NULL OR {col} = '')",
            ConditionOperator.NotEmpty => $"({col} IS NOT NULL AND {col} <> '')",
            _ => throw new QueryCompilationException($"Unsupported text operator {c.Operator}."),
        };

        string Like(string col, string pattern) => $"{col} LIKE {AddParam(pattern)} ESCAPE '\\'";

        static string EscapeLike(string s) =>
            s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

        static string Str(object? v) => v as string
            ?? throw new QueryCompilationException("Expected a string value.");

        // ---- scalar (numbers and dates) -----------------------------------------

        string ScalarCondition(string col, ConditionNode c, Func<object?, long> convert)
        {
            string P(int i) => AddParam(convert(c.Values[i]));
            return c.Operator switch
            {
                ConditionOperator.Equals => $"{col} = {P(0)}",
                ConditionOperator.NotEquals => $"{col} <> {P(0)}",
                ConditionOperator.GreaterThan => $"{col} > {P(0)}",
                ConditionOperator.GreaterOrEqual => $"{col} >= {P(0)}",
                ConditionOperator.LessThan => $"{col} < {P(0)}",
                ConditionOperator.LessOrEqual => $"{col} <= {P(0)}",
                ConditionOperator.Between => $"{col} BETWEEN {P(0)} AND {P(1)}",
                ConditionOperator.In =>
                    $"{col} IN ({string.Join(", ", Enumerable.Range(0, c.Values.Count).Select(P))})",
                _ => throw new QueryCompilationException($"Unsupported scalar operator {c.Operator}."),
            };
        }

        static long ToUnixSeconds(object? v) => v switch
        {
            DateTimeOffset dto => dto.ToUnixTimeSeconds(),
            DateTime dt => new DateTimeOffset(dt.ToUniversalTime(), TimeSpan.Zero).ToUnixTimeSeconds(),
            string s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal).ToUnixTimeSeconds(),
            long l => l,
            int i => i,
            _ => throw new QueryCompilationException($"Cannot interpret '{v}' as a date."),
        };

        static long ToInt64(object? v) => v switch
        {
            long l => l,
            int i => i,
            string s => long.Parse(s, CultureInfo.InvariantCulture),
            _ => throw new QueryCompilationException($"Cannot interpret '{v}' as a number."),
        };

        // ---- booleans ------------------------------------------------------------

        string BoolCondition(string col, ConditionNode c)
        {
            var b = c.Values[0] switch
            {
                bool v => v,
                _ => throw new QueryCompilationException($"{c.Property} expects a boolean value."),
            };
            if (c.Operator == ConditionOperator.NotEquals) b = !b;
            return $"{col} = {(b ? 1 : 0)}";
        }

        // ---- addresses -----------------------------------------------------------

        static int[] AddressKinds(MessageProperty p) => p switch
        {
            MessageProperty.From => [0],
            MessageProperty.To => [1],
            MessageProperty.Cc => [2],
            MessageProperty.Bcc => [3],
            MessageProperty.Sender => [5],
            MessageProperty.AnyRecipient => [1, 2, 3],
            MessageProperty.Participant => [0, 1, 2, 3, 4, 5],
            _ => throw new QueryCompilationException($"{p} is not an address property."),
        };

        string AddressCondition(ConditionNode c)
        {
            // Negative operators become NOT EXISTS around the positive form.
            var (positiveOp, negated) = c.Operator switch
            {
                ConditionOperator.NotEquals => (ConditionOperator.Equals, true),
                ConditionOperator.NotContains => (ConditionOperator.Contains, true),
                var op => (op, false),
            };
            var kinds = string.Join(", ", AddressKinds(c.Property));
            string match = positiveOp switch
            {
                ConditionOperator.Equals => $"a.email = {AddParam(Str(c.Values[0]))} COLLATE NOCASE",
                ConditionOperator.Contains =>
                    Or2(Like("a.email", "%" + EscapeLike(Str(c.Values[0])) + "%"),
                        Like("a.display_name", "%" + EscapeLike(Str(c.Values[0])) + "%")),
                ConditionOperator.StartsWith =>
                    Or2(Like("a.email", EscapeLike(Str(c.Values[0])) + "%"),
                        Like("a.display_name", EscapeLike(Str(c.Values[0])) + "%")),
                ConditionOperator.EndsWith =>
                    Or2(Like("a.email", "%" + EscapeLike(Str(c.Values[0]))),
                        Like("a.display_name", "%" + EscapeLike(Str(c.Values[0])))),
                ConditionOperator.In =>
                    $"a.email COLLATE NOCASE IN ({string.Join(", ", c.Values.Select(v => AddParam(Str(v))))})",
                _ => throw new QueryCompilationException($"Unsupported address operator {c.Operator}."),
            };
            var exists =
                "EXISTS (SELECT 1 FROM message_addresses ma JOIN addresses a ON a.id = ma.address_id " +
                $"WHERE ma.message_id = m.id AND ma.kind IN ({kinds}) AND {match})";
            return negated ? "NOT " + exists : exists;

            static string Or2(string x, string y) => $"({x} OR {y})";
        }

        // ---- full text -----------------------------------------------------------

        string FtsMatch(string? column, string term, bool negate)
        {
            var tokens = term.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0)
                throw new QueryCompilationException("Full-text search needs a non-empty term.");
            var sb = new StringBuilder();
            for (var i = 0; i < tokens.Length; i++)
            {
                if (i > 0) sb.Append(" AND ");
                if (column is not null) sb.Append(column).Append(": ");
                sb.Append('"').Append(tokens[i].Replace("\"", "\"\"")).Append('"');
            }
            var inClause = $"m.id IN (SELECT rowid FROM messages_fts WHERE messages_fts MATCH {AddParam(sb.ToString())})";
            return negate ? "NOT " + inClause : inClause;
        }
    }
}
