namespace Mail.Core.Search;

public enum MessageProperty
{
    From, Sender, To, Cc, Bcc, AnyRecipient, Participant,
    Subject, Body, AttachmentName, Folder,
    Received, Sent, Size,
    IsRead, IsFlagged, IsDraft, HasAttachments,
}

public enum PropertyKind { Address, Text, FullText, Number, Date, Boolean, Folder }

public enum ConditionOperator
{
    Equals, NotEquals,
    Contains, NotContains, StartsWith, EndsWith,
    /// <summary>Glob match where * and ? are wildcards; anchored unless the pattern says otherwise.</summary>
    Matches,
    GreaterThan, GreaterOrEqual, LessThan, LessOrEqual, Between,
    In, IsEmpty, NotEmpty,
}

/// <summary>
/// Property/operator metadata. Drives compiler validation now and, later, the
/// filter editor's field and operator dropdowns.
/// </summary>
public static class MessagePropertySchema
{
    public static PropertyKind KindOf(MessageProperty property) => property switch
    {
        MessageProperty.From or MessageProperty.Sender or MessageProperty.To
            or MessageProperty.Cc or MessageProperty.Bcc
            or MessageProperty.AnyRecipient or MessageProperty.Participant => PropertyKind.Address,
        MessageProperty.Subject or MessageProperty.AttachmentName => PropertyKind.Text,
        MessageProperty.Body => PropertyKind.FullText,
        MessageProperty.Folder => PropertyKind.Folder,
        MessageProperty.Received or MessageProperty.Sent => PropertyKind.Date,
        MessageProperty.Size => PropertyKind.Number,
        MessageProperty.IsRead or MessageProperty.IsFlagged
            or MessageProperty.IsDraft or MessageProperty.HasAttachments => PropertyKind.Boolean,
        _ => throw new ArgumentOutOfRangeException(nameof(property), property, null),
    };

    public static IReadOnlyList<ConditionOperator> OperatorsFor(PropertyKind kind) => kind switch
    {
        PropertyKind.Address =>
            [ConditionOperator.Equals, ConditionOperator.NotEquals, ConditionOperator.Contains,
             ConditionOperator.NotContains, ConditionOperator.StartsWith, ConditionOperator.EndsWith,
             ConditionOperator.Matches, ConditionOperator.In],
        PropertyKind.Text =>
            [ConditionOperator.Equals, ConditionOperator.NotEquals, ConditionOperator.Contains,
             ConditionOperator.NotContains, ConditionOperator.StartsWith, ConditionOperator.EndsWith,
             ConditionOperator.Matches, ConditionOperator.In,
             ConditionOperator.IsEmpty, ConditionOperator.NotEmpty],
        PropertyKind.FullText =>
            [ConditionOperator.Contains, ConditionOperator.NotContains],
        PropertyKind.Folder =>
            [ConditionOperator.Equals, ConditionOperator.NotEquals,
             ConditionOperator.Matches, ConditionOperator.In],
        PropertyKind.Date or PropertyKind.Number =>
            [ConditionOperator.Equals, ConditionOperator.NotEquals, ConditionOperator.GreaterThan,
             ConditionOperator.GreaterOrEqual, ConditionOperator.LessThan, ConditionOperator.LessOrEqual,
             ConditionOperator.Between, ConditionOperator.In],
        PropertyKind.Boolean =>
            [ConditionOperator.Equals, ConditionOperator.NotEquals],
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
