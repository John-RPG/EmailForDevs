using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Mail.Core.Compose;
using Microsoft.Web.WebView2.Core;
using Mail.Storage.Database;
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

        /// <summary>
        /// Accessible name. Without it the generated ToString() reports the byte
        /// array as its type, so every attachment sounds identical.
        /// </summary>
        public override string ToString() => $"{Display}, {ContentType}";
    }

    readonly ObservableCollection<AttachmentEntry> _attachments = [];
    readonly Func<string, MimeMessage, Task> _send;
    readonly Draft? _seed;
    bool _isRich;
    bool _editorReady;

    /// <summary>
    /// Whether the editor page is dark. Separate from the app theme on purpose:
    /// the message is written on the recipient's background, not ours, so some
    /// people want a light editing surface inside a dark app to see what the
    /// other end will see. It is a view setting and never touches the content.
    /// </summary>
    bool _editorDark;

    /// <summary>Persists unsent work. Null when the caller wants no saving.</summary>
    readonly DraftStore? _drafts;

    /// <summary>
    /// Supplies the signature for a sending account, given whether the message
    /// is a reply. Owned by the shell, which holds the settings store.
    /// </summary>
    readonly Func<string, bool, string?>? _signature;

    /// <summary>
    /// Reads and writes the account's stored signature, so one can be defined
    /// from here instead of sending this message unsigned and going to Settings.
    /// Null when the shell has no settings store to write to.
    /// </summary>
    readonly ISignatureStore? _signatures;

    /// <summary>
    /// The compose window's view of signature storage. An interface rather than
    /// a pair of delegates because get and set have to agree about scope: a
    /// signature saved for the account must be the one read back for it.
    /// </summary>
    public interface ISignatureStore
    {
        /// <summary>The stored signature for this address, or null if none.</summary>
        string? Get(string fromAddress);

        /// <summary>
        /// Stores the signature for this address and enables signing for it.
        /// Empty text clears it.
        /// </summary>
        void Save(string fromAddress, string text);
    }

    /// <summary>Row this window owns, 0 until first saved. Kept so autosave
    /// updates one row rather than accumulating a copy per keystroke burst.</summary>
    long _draftId;

    /// <summary>Set once the message is away, so closing does not re-save it.</summary>
    bool _sent;

    readonly System.Windows.Threading.DispatcherTimer _autosave = new()
    {
        Interval = TimeSpan.FromSeconds(10),
    };

    /// <param name="accounts">Addresses that can appear in From.</param>
    /// <param name="send">Given the sending account address and the built message, delivers it.</param>
    /// <param name="drafts">Where unsent work is kept; null disables saving.</param>
    /// <param name="draftId">Row to resume, when reopening a saved draft.</param>
    public ComposeWindow(
        IReadOnlyList<string> accounts,
        Func<string, MimeMessage, Task> send,
        Draft? seed = null,
        DraftStore? drafts = null,
        long draftId = 0,
        Func<string, bool, string?>? signature = null,
        ISignatureStore? signatures = null)
    {
        InitializeComponent();
        _send = send;
        _seed = seed;
        _drafts = drafts;
        _draftId = draftId;
        _signature = signature;
        _signatures = signatures;
        AttachmentBox.ItemsSource = _attachments;

        // An empty bordered box with no label reads as a broken drop target, so
        // the list only appears once it has something to show.
        _attachments.CollectionChanged += (_, _) =>
            AttachmentBox.Visibility = _attachments.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;

        // Defining a signature needs somewhere to store it; without that the
        // button could only ever report failure.
        SignatureButton.IsEnabled = _signatures is not null;

        // Save periodically as well as on close: a crash or a power cut should
        // not be worse than clicking the X, and both are why the draft exists.
        _autosave.Tick += async (_, _) => await SaveDraftAsync(silent: true);
        Closing += OnComposeClosing;

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

        // Start matching the app, then let the toggle override per message.
        _editorDark = Themes.ThemeManager.IsDark;
        EditorThemeToggle.IsChecked = _editorDark;
        UpdateEditorThemeLabel();

        Loaded += async (_, _) =>
        {
            if (_isRich)
                await ShowRichEditorAsync(seed?.HtmlBody ?? "");
            // After the body is populated: appending first would be overwritten
            // by the seed, and a resumed draft already contains its signature.
            if (draftId == 0) await InsertSignatureAsync(seed is not null);
            // Only start autosaving once the editor exists, or the first tick
            // would read a body that has not been populated yet.
            if (_drafts is not null) _autosave.Start();
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

    /// <summary>
    /// Flips the editor page between a light and a dark ground. Only the page:
    /// the message keeps whatever colours it carries, so this cannot change what
    /// the recipient receives.
    /// </summary>
    void OnToggleEditorTheme(object sender, RoutedEventArgs e)
    {
        _editorDark = EditorThemeToggle.IsChecked == true;
        UpdateEditorThemeLabel();
        _ = ApplyEditorThemeAsync();
    }

    void UpdateEditorThemeLabel() =>
        // Says what pressing it gives you, which is the opposite of the state.
        EditorThemeToggle.Content = _editorDark ? "◑ Light" : "◐ Dark";

    async Task ApplyEditorThemeAsync()
    {
        if (!_editorReady || RichEditor.CoreWebView2 is null) return;
        RichEditor.DefaultBackgroundColor = _editorDark
            ? System.Drawing.Color.FromArgb(0x28, 0x2C, 0x34)
            : System.Drawing.Color.White;
        await RichEditor.CoreWebView2.ExecuteScriptAsync(
            $"window.setTheme({(_editorDark ? "true" : "false")});");
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

        // Pin the surface behind the page. Left alone it follows Windows dark
        // mode, which flashes the wrong colour before the page paints.
        RichEditor.DefaultBackgroundColor = _editorDark
            ? System.Drawing.Color.FromArgb(0x28, 0x2C, 0x34)
            : System.Drawing.Color.White;
        await ApplyEditorThemeAsync();
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

    /// <summary>
    /// True when there is something worth keeping. An untouched window — opened
    /// and closed, or a reply whose quoted text was never added to — should not
    /// leave a draft behind to clean up later.
    /// </summary>
    /// <summary>
    /// Appends the signature for the sending account, if one is configured and
    /// applies to this kind of message. Separated from the body by the standard
    /// "-- " marker, which clients use to trim signatures when quoting.
    /// </summary>
    async Task InsertSignatureAsync(bool isReply)
    {
        if (_signature is null) return;
        var from = FromBox.SelectedItem as string ?? "";
        var text = _signature(from, isReply);
        if (string.IsNullOrWhiteSpace(text)) return;

        await AppendSignatureAsync(text);

        // Leave the caret above the signature, where the message gets written.
        if (_isRich)
            await RichEditor.CoreWebView2.ExecuteScriptAsync("window.focusBody();");
        else
            BodyBox.CaretIndex = 0;
    }

    /// <summary>
    /// Insert a signature on demand, or define one. Deliberately available even
    /// when a signature went on automatically: the common cases are wanting a
    /// different one on this message, and discovering the stored one is wrong
    /// while looking at the message it is wrong on.
    /// </summary>
    async void OnSignature(object sender, RoutedEventArgs e)
    {
        if (_signatures is null) return;
        var from = FromBox.SelectedItem as string ?? "";

        var dialog = new SignatureWindow(from, _signatures.Get(from)) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        // Save first: even if the user only meant to insert it, an edited
        // signature they liked enough to use is one they want next time.
        if (dialog.Save) _signatures.Save(from, dialog.SignatureText);
        if (dialog.Insert && !string.IsNullOrWhiteSpace(dialog.SignatureText))
            await AppendSignatureAsync(dialog.SignatureText);

        StatusText.Text = (dialog.Save, dialog.Insert) switch
        {
            (true, true) => "Signature saved and inserted.",
            (true, false) => "Signature saved for this account.",
            (false, true) => "Signature inserted.",
            _ => "",
        };
    }

    /// <summary>
    /// Appends a signature to whatever is already written, separated by the
    /// standard "-- " marker that clients use to trim signatures when quoting.
    /// </summary>
    async Task AppendSignatureAsync(string text)
    {
        if (_isRich)
        {
            var block = "<br><br><div>--&nbsp;<br>" +
                        System.Net.WebUtility.HtmlEncode(text).Replace("\n", "<br>") +
                        "</div>";
            // Appended through the page rather than by rewriting the whole body,
            // so the caret and undo history survive.
            await RichEditor.CoreWebView2.ExecuteScriptAsync(
                $"window.appendBody({JsonSerializer.Serialize(block)});");
        }
        else
        {
            BodyBox.Text += $"{Environment.NewLine}{Environment.NewLine}-- {Environment.NewLine}{text}";
        }
    }

    async Task<bool> HasContentAsync()
    {
        if (ToBox.Text.Trim().Length > 0 || CcBox.Text.Trim().Length > 0 ||
            BccBox.Text.Trim().Length > 0 || SubjectBox.Text.Trim().Length > 0 ||
            _attachments.Count > 0)
            return true;

        var draft = await CurrentDraftAsync();
        var body = (draft.HtmlBody ?? draft.Body).Trim();
        if (body.Length == 0) return false;

        // A reply arrives pre-filled with the quoted original; that alone is not
        // the user having written something.
        var seeded = (_seed?.HtmlBody ?? _seed?.Body ?? "").Trim();
        return seeded.Length == 0 || body != seeded;
    }

    async Task SaveDraftAsync(bool silent)
    {
        if (_drafts is null || _sent) return;
        try
        {
            if (!await HasContentAsync())
            {
                // Nothing worth keeping. Remove a row saved earlier, so emptying
                // a draft and closing actually discards it.
                if (_draftId != 0) { _drafts.Delete(_draftId); _draftId = 0; }
                return;
            }

            var draft = await CurrentDraftAsync();
            _draftId = _drafts.Save(new DraftStore.DraftRecord(
                _draftId,
                From: FromBox.SelectedItem as string ?? "",
                To: ToBox.Text.Trim(),
                Cc: CcBox.Text.Trim(),
                Bcc: BccBox.Text.Trim(),
                Subject: SubjectBox.Text.Trim(),
                Body: draft.Body,
                HtmlBody: draft.HtmlBody,
                IsRich: _isRich,
                InReplyTo: draft.InReplyTo,
                References: draft.References is { Count: > 0 } refs
                    ? string.Join(' ', refs) : null,
                Attachments: [.. _attachments.Select(a =>
                    new DraftStore.DraftFile(a.FileName, a.ContentType, a.Content))],
                UpdatedAt: DateTimeOffset.UtcNow));

            if (!silent) StatusText.Text = "Draft saved.";
        }
        catch (Exception ex)
        {
            // Never block closing on a save failure, but say so: silently losing
            // the message is exactly what this exists to prevent.
            StatusText.Text = $"Could not save the draft: {ex.Message}";
        }
    }

    /// <summary>
    /// Saves on close. Deliberately without a prompt: the message is kept either
    /// way, so asking would only be a chance to lose it by answering wrongly.
    /// </summary>
    async void OnComposeClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _autosave.Stop();
        if (_drafts is null || _sent) return;

        // Closing cannot await, so cancel it, save, then close for real.
        if (!_saving)
        {
            _saving = true;
            e.Cancel = true;
            await SaveDraftAsync(silent: true);
            Close();
        }
    }

    bool _saving;

    void OnSaveDraft(object sender, RoutedEventArgs e) => _ = SaveDraftAsync(silent: false);

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
            // Sent: drop the saved copy, or it would reappear as unfinished work.
            _sent = true;
            _autosave.Stop();
            if (_drafts is not null && _draftId != 0) _drafts.Delete(_draftId);
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
