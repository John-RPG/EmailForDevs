using System.Globalization;

namespace Mail.Core.Search;

public sealed class QueryParseException(string message, int position)
    : Exception($"{message} (position {position})")
{
    public int Position { get; } = position;
}

/// <summary>
/// Parses the search-box mini-language into a <see cref="QueryNode"/> tree.
///
///   from:alice has:attachment before:2024-06 -subject:"weekly report"
///   (from:bob OR from:carol) AND is:unread larger:2mb
///
/// Grammar: OR binds loosest, then implicit/explicit AND, then NOT (- or NOT),
/// then a term: either field:value or a bare word (full-text). Parentheses
/// group. Quoted strings keep spaces. Unknown fields are a parse error rather
/// than being silently treated as text — silent misreads are how search boxes
/// lie to you.
/// </summary>
public static class QueryParser
{
    public static QueryNode Parse(string input)
    {
        var tokens = Tokenize(input);
        if (tokens.Count == 0)
            throw new QueryParseException("Empty query", 0);
        var position = 0;
        var node = ParseOr(tokens, ref position);
        if (position < tokens.Count)
            throw new QueryParseException($"Unexpected '{tokens[position].Text}'", tokens[position].Start);
        return node;
    }

    /// <summary>Parses if possible; falls back to a plain full-text search of the whole input.</summary>
    public static QueryNode ParseOrText(string input)
    {
        try
        {
            return Parse(input);
        }
        catch (QueryParseException)
        {
            return QueryNode.Text(input);
        }
    }

    // ---- tokenizer -----------------------------------------------------------

    enum TokenKind { Word, Quoted, LParen, RParen, Minus }

    readonly record struct Token(TokenKind Kind, string Text, int Start);

    static List<Token> Tokenize(string input)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < input.Length)
        {
            var c = input[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '(') { tokens.Add(new(TokenKind.LParen, "(", i)); i++; continue; }
            if (c == ')') { tokens.Add(new(TokenKind.RParen, ")", i)); i++; continue; }
            if (c == '-' && (tokens.Count == 0 || tokens[^1].Kind is TokenKind.LParen or TokenKind.Minus
                    || IsOperatorWord(tokens[^1])))
            {
                tokens.Add(new(TokenKind.Minus, "-", i));
                i++;
                continue;
            }
            if (c == '"')
            {
                var end = input.IndexOf('"', i + 1);
                if (end < 0)
                    throw new QueryParseException("Unterminated quote", i);
                tokens.Add(new(TokenKind.Quoted, input[(i + 1)..end], i));
                i = end + 1;
                continue;
            }
            // A word runs to whitespace or a paren; a field:"quoted value" is
            // consumed whole so the value keeps its spaces.
            var start = i;
            while (i < input.Length && !char.IsWhiteSpace(input[i]) && input[i] != '(' && input[i] != ')')
            {
                if (input[i] == '"')
                {
                    var close = input.IndexOf('"', i + 1);
                    if (close < 0)
                        throw new QueryParseException("Unterminated quote", i);
                    i = close + 1;
                    continue;
                }
                i++;
            }
            tokens.Add(new(TokenKind.Word, input[start..i], start));
        }
        return tokens;
    }

    static bool IsOperatorWord(Token token) =>
        token.Kind == TokenKind.Word &&
        (token.Text.Equals("AND", StringComparison.OrdinalIgnoreCase) ||
         token.Text.Equals("OR", StringComparison.OrdinalIgnoreCase) ||
         token.Text.Equals("NOT", StringComparison.OrdinalIgnoreCase));

    // ---- recursive descent ---------------------------------------------------

    static QueryNode ParseOr(List<Token> tokens, ref int position)
    {
        var children = new List<QueryNode> { ParseAnd(tokens, ref position) };
        while (position < tokens.Count && tokens[position].Kind == TokenKind.Word &&
               tokens[position].Text.Equals("OR", StringComparison.OrdinalIgnoreCase))
        {
            position++;
            children.Add(ParseAnd(tokens, ref position));
        }
        return children.Count == 1 ? children[0] : new GroupNode(GroupOp.Or, children);
    }

    static QueryNode ParseAnd(List<Token> tokens, ref int position)
    {
        var children = new List<QueryNode> { ParseNot(tokens, ref position) };
        while (position < tokens.Count)
        {
            var token = tokens[position];
            if (token.Kind == TokenKind.RParen)
                break;
            if (token.Kind == TokenKind.Word &&
                token.Text.Equals("OR", StringComparison.OrdinalIgnoreCase))
                break;
            if (token.Kind == TokenKind.Word &&
                token.Text.Equals("AND", StringComparison.OrdinalIgnoreCase))
                position++; // explicit AND is optional
            children.Add(ParseNot(tokens, ref position));
        }
        return children.Count == 1 ? children[0] : new GroupNode(GroupOp.And, children);
    }

    static QueryNode ParseNot(List<Token> tokens, ref int position)
    {
        if (position >= tokens.Count)
            throw new QueryParseException("Expected a term", tokens.Count > 0 ? tokens[^1].Start : 0);
        var token = tokens[position];
        if (token.Kind == TokenKind.Minus ||
            (token.Kind == TokenKind.Word && token.Text.Equals("NOT", StringComparison.OrdinalIgnoreCase)))
        {
            position++;
            return new NotNode(ParseNot(tokens, ref position));
        }
        return ParsePrimary(tokens, ref position);
    }

    static QueryNode ParsePrimary(List<Token> tokens, ref int position)
    {
        var token = tokens[position];
        if (token.Kind == TokenKind.LParen)
        {
            position++;
            var inner = ParseOr(tokens, ref position);
            if (position >= tokens.Count || tokens[position].Kind != TokenKind.RParen)
                throw new QueryParseException("Expected ')'", token.Start);
            position++;
            return inner;
        }
        if (token.Kind == TokenKind.RParen)
            throw new QueryParseException("Unexpected ')'", token.Start);
        position++;
        return token.Kind == TokenKind.Quoted
            ? QueryNode.Text(token.Text)
            : ParseTerm(token);
    }

    // ---- field terms ---------------------------------------------------------

    static QueryNode ParseTerm(Token token)
    {
        var text = token.Text;
        var colon = text.IndexOf(':');
        if (colon <= 0)
            return QueryNode.Text(Unquote(text));

        var field = text[..colon].ToLowerInvariant();
        var value = Unquote(text[(colon + 1)..]);
        if (value.Length == 0)
            throw new QueryParseException($"'{field}:' needs a value", token.Start);

        return field switch
        {
            "from" => Address(MessageProperty.From, value),
            "to" => Address(MessageProperty.To, value),
            "cc" => Address(MessageProperty.Cc, value),
            "bcc" => Address(MessageProperty.Bcc, value),
            "recipient" => Address(MessageProperty.AnyRecipient, value),
            "participant" or "involves" => Address(MessageProperty.Participant, value),
            "subject" => Text(MessageProperty.Subject, value),
            "body" => QueryNode.Where(MessageProperty.Body, ConditionOperator.Contains, value),
            "folder" or "in" => HasWildcard(value)
                ? QueryNode.Where(MessageProperty.Folder, ConditionOperator.Matches, value)
                : QueryNode.Where(MessageProperty.Folder, ConditionOperator.Equals, value),
            "filename" or "attachment" => Text(MessageProperty.AttachmentName, value),
            "has" => Has(value, token.Start),
            "is" => Is(value, token.Start),
            "after" or "since" =>
                QueryNode.Where(MessageProperty.Received, ConditionOperator.GreaterOrEqual, ParseDate(value, token.Start)),
            "before" or "until" =>
                QueryNode.Where(MessageProperty.Received, ConditionOperator.LessThan, ParseDate(value, token.Start)),
            "on" => OnDay(value, token.Start),
            "larger" or "bigger" =>
                QueryNode.Where(MessageProperty.Size, ConditionOperator.GreaterThan, ParseSize(value, token.Start)),
            "smaller" =>
                QueryNode.Where(MessageProperty.Size, ConditionOperator.LessThan, ParseSize(value, token.Start)),
            _ => throw new QueryParseException($"Unknown field '{field}'", token.Start),
        };
    }

    /// <summary>True when the value has an unescaped wildcard (\* and \? are literals).</summary>
    static bool HasWildcard(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\') { i++; continue; }
            if (value[i] is '*' or '?') return true;
        }
        return false;
    }

    /// <summary>Strips escape backslashes for the non-wildcard path: `a\*b` searches for `a*b`.</summary>
    static string Unescape(string value)
    {
        if (!value.Contains('\\')) return value;
        var sb = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length) i++;
            sb.Append(value[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Wildcarded values match as anchored globs; plain values keep the
    /// friendlier substring behaviour (from:alice finds alice@example.com).
    /// </summary>
    static QueryNode Address(MessageProperty property, string value) =>
        HasWildcard(value)
            ? QueryNode.Where(property, ConditionOperator.Matches, value)
            : QueryNode.Where(property, ConditionOperator.Contains, Unescape(value));

    static QueryNode Text(MessageProperty property, string value) =>
        HasWildcard(value)
            ? QueryNode.Where(property, ConditionOperator.Matches, value)
            : QueryNode.Where(property, ConditionOperator.Contains, Unescape(value));

    static QueryNode Has(string value, int position) => value.ToLowerInvariant() switch
    {
        "attachment" or "attachments" or "file" =>
            QueryNode.Where(MessageProperty.HasAttachments, ConditionOperator.Equals, true),
        _ => throw new QueryParseException($"Unknown has: value '{value}'", position),
    };

    static QueryNode Is(string value, int position) => value.ToLowerInvariant() switch
    {
        "read" => QueryNode.Where(MessageProperty.IsRead, ConditionOperator.Equals, true),
        "unread" => QueryNode.Where(MessageProperty.IsRead, ConditionOperator.Equals, false),
        "flagged" => QueryNode.Where(MessageProperty.IsFlagged, ConditionOperator.Equals, true),
        "unflagged" => QueryNode.Where(MessageProperty.IsFlagged, ConditionOperator.Equals, false),
        "draft" => QueryNode.Where(MessageProperty.IsDraft, ConditionOperator.Equals, true),
        _ => throw new QueryParseException($"Unknown is: value '{value}'", position),
    };

    /// <summary>on:2026-08-31 → the whole local day.</summary>
    static QueryNode OnDay(string value, int position)
    {
        var start = ParseDate(value, position);
        var end = value.Length switch
        {
            4 => start.AddYears(1),      // on:2026
            7 => start.AddMonths(1),     // on:2026-08
            _ => start.AddDays(1),
        };
        return QueryNode.And(
            QueryNode.Where(MessageProperty.Received, ConditionOperator.GreaterOrEqual, start),
            QueryNode.Where(MessageProperty.Received, ConditionOperator.LessThan, end));
    }

    /// <summary>Accepts 2026, 2026-08, 2026-08-31, today, yesterday, 7d/2w/3m/1y ago.</summary>
    static DateTimeOffset ParseDate(string value, int position)
    {
        var text = value.Trim().ToLowerInvariant();
        var today = new DateTimeOffset(DateTime.Today, DateTimeOffset.Now.Offset);
        switch (text)
        {
            case "today": return today;
            case "yesterday": return today.AddDays(-1);
            case "tomorrow": return today.AddDays(1);
        }
        if (text.Length > 1 && char.IsDigit(text[0]) && "dwmy".Contains(text[^1]))
        {
            var numberPart = text[..^1];
            if (int.TryParse(numberPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount))
                return text[^1] switch
                {
                    'd' => today.AddDays(-amount),
                    'w' => today.AddDays(-7 * amount),
                    'm' => today.AddMonths(-amount),
                    _ => today.AddYears(-amount),
                };
        }
        var formats = new[] { "yyyy", "yyyy-MM", "yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy" };
        foreach (var format in formats)
            if (DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
                return new DateTimeOffset(parsed, DateTimeOffset.Now.Offset);
        throw new QueryParseException($"Unrecognized date '{value}'", position);
    }

    /// <summary>Accepts 100, 50kb, 2mb, 1g — returns bytes.</summary>
    static long ParseSize(string value, int position)
    {
        var text = value.Trim().ToLowerInvariant().Replace("b", "");
        var multiplier = 1L;
        if (text.EndsWith('k')) { multiplier = 1024; text = text[..^1]; }
        else if (text.EndsWith('m')) { multiplier = 1024 * 1024; text = text[..^1]; }
        else if (text.EndsWith('g')) { multiplier = 1024 * 1024 * 1024; text = text[..^1]; }
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            throw new QueryParseException($"Unrecognized size '{value}'", position);
        return (long)(number * multiplier);
    }

    static string Unquote(string text) =>
        text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;
}
