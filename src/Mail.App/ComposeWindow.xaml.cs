using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Mail.Core.Compose;
using MimeKit;

namespace Mail.App;

/// <summary>
/// Compose / reply / forward. Builds a MIME message locally (so the MIME tab can
/// show exactly what will go out) and hands it to the caller's send delegate.
/// </summary>
public partial class ComposeWindow : Window
{
    public sealed record AttachmentEntry(string FileName, string ContentType, byte[] Content)
    {
        public string Display => $"{FileName} ({Content.Length / 1024.0:N0} KB)";
    }

    readonly ObservableCollection<AttachmentEntry> _attachments = [];
    readonly Func<string, MimeMessage, Task> _send;
    readonly Draft? _seed;

    /// <param name="accounts">Addresses that can appear in From.</param>
    /// <param name="send">Given the sending account address and the built message, delivers it.</param>
    public ComposeWindow(
        IReadOnlyList<string> accounts,
        Func<string, MimeMessage, Task> send,
        Draft? seed = null)
    {
        InitializeComponent();
        _send = send;
        _seed = seed;
        AttachmentBox.ItemsSource = _attachments;

        foreach (var account in accounts)
            FromBox.Items.Add(account);
        FromBox.SelectedIndex = 0;

        if (seed is not null)
        {
            if (accounts.Any(a => ReplyBuilder.SameAddress(a, seed.From)))
                FromBox.SelectedItem = accounts.First(a => ReplyBuilder.SameAddress(a, seed.From));
            ToBox.Text = string.Join(", ", seed.To);
            CcBox.Text = string.Join(", ", seed.Cc);
            SubjectBox.Text = seed.Subject;
            BodyBox.Text = seed.Body;
            foreach (var attachment in seed.Attachments ?? [])
                _attachments.Add(new AttachmentEntry(
                    attachment.FileName, attachment.ContentType, attachment.Content));
        }

        Loaded += (_, _) =>
        {
            if (seed is null || seed.To.Count == 0)
                ToBox.Focus();
            else
            {
                BodyBox.Focus();
                BodyBox.CaretIndex = 0; // above the quoted text
            }
        };
    }

    Draft CurrentDraft() => new(
        From: FromBox.SelectedItem as string ?? "",
        To: ReplyBuilder.SplitAddresses(ToBox.Text),
        Cc: ReplyBuilder.SplitAddresses(CcBox.Text),
        Bcc: ReplyBuilder.SplitAddresses(BccBox.Text),
        Subject: SubjectBox.Text ?? "",
        Body: BodyBox.Text ?? "",
        InReplyTo: _seed?.InReplyTo,
        References: _seed?.References,
        Attachments: [.. _attachments.Select(a =>
            new DraftAttachment(a.FileName, a.ContentType, a.Content))]);

    MimeMessage? TryBuild(out string? error)
    {
        error = null;
        var draft = CurrentDraft();
        if (draft.To.Count == 0 && draft.Cc.Count == 0 && draft.Bcc.Count == 0)
        {
            error = "Add at least one recipient.";
            return null;
        }
        try
        {
            return ReplyBuilder.ToMimeMessage(draft);
        }
        catch (Exception ex)
        {
            error = $"Address problem: {ex.Message}";
            return null;
        }
    }

    void OnAttach(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true };
        if (dialog.ShowDialog(this) != true)
            return;
        foreach (var path in dialog.FileNames)
        {
            var content = File.ReadAllBytes(path);
            _attachments.Add(new AttachmentEntry(
                Path.GetFileName(path), GuessContentType(path), content));
        }
    }

    void OnRemoveAttachment(object sender, RoutedEventArgs e)
    {
        if (AttachmentBox.SelectedItem is AttachmentEntry entry)
            _attachments.Remove(entry);
    }

    static string GuessContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".txt" or ".log" or ".md" => "text/plain",
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".zip" => "application/zip",
        ".json" => "application/json",
        ".csv" => "text/csv",
        ".html" or ".htm" => "text/html",
        _ => "application/octet-stream",
    };

    void OnShowSource(object sender, RoutedEventArgs e)
    {
        var message = TryBuild(out var error);
        if (message is null)
        {
            StatusText.Text = error;
            return;
        }
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        var source = System.Text.Encoding.UTF8.GetString(stream.ToArray());

        var window = new Window
        {
            Title = "Outgoing MIME",
            Owner = this,
            Width = 820,
            Height = 600,
            Content = new TextBox
            {
                Text = source,
                IsReadOnly = true,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
        };
        window.ShowDialog();
    }

    async void OnSend(object sender, RoutedEventArgs e)
    {
        var message = TryBuild(out var error);
        if (message is null)
        {
            StatusText.Text = error;
            return;
        }
        SendButton.IsEnabled = false;
        StatusText.Text = "Sending…";
        try
        {
            await _send(FromBox.SelectedItem as string ?? "", message);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            SendButton.IsEnabled = true;
            StatusText.Text = $"Send failed: {ex.Message}";
        }
    }
}
