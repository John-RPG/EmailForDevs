using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Mail.Core.Compose;
using Microsoft.Web.WebView2.Core;
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
    bool _isRich;
    bool _editorReady;

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

        // Format follows the message being replied to; new mail starts plain.
        _isRich = seed?.HtmlBody is not null;
        RichRadio.IsChecked = _isRich;
        PlainRadio.IsChecked = !_isRich;

        Loaded += async (_, _) =>
        {
            if (_isRich)
                await ShowRichEditorAsync(seed?.HtmlBody ?? "");
            if (seed is null || seed.To.Count == 0)
                ToBox.Focus();
            else if (!_isRich)
            {
                BodyBox.Focus();
                BodyBox.CaretIndex = 0; // above the quoted text
            }
        };
    }

    async Task<Draft> CurrentDraftAsync()
    {
        var html = _isRich ? await ReadRichBodyAsync() : null;
        return new Draft(
            From: FromBox.SelectedItem as string ?? "",
            To: ReplyBuilder.SplitAddresses(ToBox.Text),
            Cc: ReplyBuilder.SplitAddresses(CcBox.Text),
            Bcc: ReplyBuilder.SplitAddresses(BccBox.Text),
            Subject: SubjectBox.Text ?? "",
            // In rich mode ReplyBuilder derives the text alternative from the HTML.
            Body: _isRich ? "" : BodyBox.Text ?? "",
            InReplyTo: _seed?.InReplyTo,
            References: _seed?.References,
            Attachments: [.. _attachments.Select(a =>
                new DraftAttachment(a.FileName, a.ContentType, a.Content))],
            HtmlBody: html);
    }

    async Task<MimeMessage?> TryBuildAsync(Action<string> onError)
    {
        var draft = await CurrentDraftAsync();
        if (draft.To.Count == 0 && draft.Cc.Count == 0 && draft.Bcc.Count == 0)
        {
            onError("Add at least one recipient.");
            return null;
        }
        try
        {
            return ReplyBuilder.ToMimeMessage(draft);
        }
        catch (Exception ex)
        {
            onError($"Address problem: {ex.Message}");
            return null;
        }
    }

    // ---- rich editor ---------------------------------------------------------

    void OnFormatChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _ = SwitchFormatAsync(RichRadio.IsChecked == true);
    }

    async Task SwitchFormatAsync(bool rich)
    {
        if (rich == _isRich) return;
        if (rich)
        {
            // Carry the plain text across as escaped HTML so nothing is lost.
            var html = System.Net.WebUtility.HtmlEncode(BodyBox.Text ?? "")
                .Replace("\r\n", "\n").Replace("\n", "<br>");
            _isRich = true;
            await ShowRichEditorAsync(html);
        }
        else
        {
            // Going back to plain flattens the HTML; warn before losing formatting.
            var html = await ReadRichBodyAsync();
            if (!string.IsNullOrWhiteSpace(html) &&
                MessageBox.Show(this,
                    "Switching to plain text will discard formatting. Continue?",
                    "eeeMail", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                != MessageBoxResult.OK)
            {
                RichRadio.IsChecked = true;
                return;
            }
            BodyBox.Text = Mail.Core.Ingest.HtmlText.ToPlainText(html ?? "");
            _isRich = false;
            RichEditor.Visibility = Visibility.Collapsed;
            BodyBox.Visibility = Visibility.Visible;
            BodyBox.Focus();
        }
    }

    async Task ShowRichEditorAsync(string html)
    {
        BodyBox.Visibility = Visibility.Collapsed;
        RichEditor.Visibility = Visibility.Visible;
        await EnsureEditorAsync();
        var json = JsonSerializer.Serialize(html ?? "");
        await RichEditor.CoreWebView2.ExecuteScriptAsync($"window.setBody({json});");
        await RichEditor.CoreWebView2.ExecuteScriptAsync("window.focusBody();");
    }

    async Task EnsureEditorAsync()
    {
        if (_editorReady) return;
        var environment = await CoreWebView2Environment.CreateAsync(
            userDataFolder: Path.Combine(Path.GetTempPath(), "eeemail-editor"));
        await RichEditor.EnsureCoreWebView2Async(environment);

        var settings = RichEditor.CoreWebView2.Settings;
        settings.AreDevToolsEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreDefaultContextMenusEnabled = true; // spell-check and clipboard

        // The editor is a local page: refuse every network request, exactly like
        // the reading pane. Composing must never phone home.
        RichEditor.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        RichEditor.CoreWebView2.WebResourceRequested += (_, args) =>
        {
            var uri = args.Request.Uri;
            if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                return;
            args.Response = RichEditor.CoreWebView2.Environment
                .CreateWebResourceResponse(null, 403, "Blocked", "");
        };

        var page = ReadEmbeddedEditor();
        var loaded = new TaskCompletionSource();
        void OnLoaded(object? _, CoreWebView2NavigationCompletedEventArgs __)
        {
            RichEditor.CoreWebView2.NavigationCompleted -= OnLoaded;
            loaded.TrySetResult();
        }
        RichEditor.CoreWebView2.NavigationCompleted += OnLoaded;
        RichEditor.CoreWebView2.NavigateToString(page);
        await loaded.Task;
        _editorReady = true;
    }

    static string ReadEmbeddedEditor()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("editor.html", StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    async Task<string?> ReadRichBodyAsync()
    {
        if (!_editorReady || RichEditor.CoreWebView2 is null) return _seed?.HtmlBody;
        var json = await RichEditor.CoreWebView2.ExecuteScriptAsync("window.getBody();");
        return JsonSerializer.Deserialize<string>(json);
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

    async void OnShowSource(object sender, RoutedEventArgs e)
    {
        var message = await TryBuildAsync(msg => StatusText.Text = msg);
        if (message is null)
            return;
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
        var message = await TryBuildAsync(msg => StatusText.Text = msg);
        if (message is null)
            return;
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
