using Mail.Core.Themes;

namespace Mail.Tests;

/// <summary>
/// Verifies the themes are actually readable, rather than trusting that they
/// looked fine on one monitor. WCAG AA is 4.5:1 for normal text and 3:1 for
/// large text and UI components; dark mode is held to the same bar, since
/// "it's dark mode" is not a reason for text to be harder to read.
/// </summary>
public sealed class PaletteTests
{
    const double NormalText = 4.5;
    const double UiComponent = 3.0;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Text_meets_AA_against_its_background(bool dark)
    {
        foreach (var (textKey, backgroundKey) in Palette.TextOn)
        {
            var text = Palette.ByKey(textKey);
            var background = Palette.ByKey(backgroundKey);
            Assert.NotNull(text);
            Assert.NotNull(background);

            var foreground = dark ? text!.Dark : text.Light;
            var behind = dark ? background!.Dark : background.Light;
            var ratio = Palette.ContrastRatio(foreground, behind);

            Assert.True(ratio >= NormalText,
                $"{(dark ? "dark" : "light")} {textKey} ({foreground}) on {backgroundKey} " +
                $"({behind}) is {ratio:N2}:1, needs {NormalText:N1}:1");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Accent_is_readable_behind_white_text(bool dark)
    {
        var accent = Palette.ByKey("Accent")!;
        var colour = dark ? accent.Dark : accent.Light;
        var ratio = Palette.ContrastRatio("#FFFFFF", colour);
        // Accent carries white label text, so it is a large-text/UI surface.
        Assert.True(ratio >= UiComponent,
            $"{(dark ? "dark" : "light")} accent {colour} against white is {ratio:N2}:1");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Alternate_surfaces_stay_distinguishable_from_the_main_one(bool dark)
    {
        // Zebra striping and raised panels have to be visible without being
        // loud — a difference too small to see makes the layout mush.
        var main = Palette.ByKey("Chrome.Background")!;
        foreach (var key in new[] { "Chrome.Alt", "Chrome.Raised" })
        {
            var alt = Palette.ByKey(key)!;
            var ratio = Palette.ContrastRatio(
                dark ? main.Dark : main.Light,
                dark ? alt.Dark : alt.Light);
            Assert.True(ratio > 1.0 && ratio < 2.0,
                $"{(dark ? "dark" : "light")} {key} differs from the background by {ratio:N2}:1");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Text_on_a_selected_row_meets_AA(bool dark)
    {
        // A row readable until you click it is still a contrast failure. This
        // was missed until dark mode showed secondary text at 3.16:1 on a
        // selection, which is why selection is now checked explicitly.
        var selection = Palette.ByKey("Selection.Background")!;
        foreach (var key in Palette.TextOnSelection)
        {
            var text = Palette.ByKey(key)!;
            var ratio = Palette.ContrastRatio(
                dark ? text.Dark : text.Light,
                dark ? selection.Dark : selection.Light);
            Assert.True(ratio >= NormalText,
                $"{(dark ? "dark" : "light")} {key} on a selected row is {ratio:N2}:1");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_selected_row_can_be_found_by_its_edge(bool dark)
    {
        // The fill alone cannot mark the selected row. It sits between the base,
        // the stripe and the hover tint, and it cannot move far from them
        // without dropping the text on top of it below AA — measured, the fill
        // is about 1.4:1 against its neighbours whatever we do. So the edge is
        // what makes selection findable, and it is held to the 3:1 that applies
        // to a meaningful non-text indicator.
        //
        // Worth keeping because the failure mode was invisible: the edge was
        // specified for months while the stock DataGridRow template quietly
        // discarded it, and no test noticed.
        const double Indicator = 3.0;
        var edge = Palette.ByKey("Selection.Edge")!;
        foreach (var key in new[] { "Chrome.Background", "Row.Stripe", "Row.Hover" })
        {
            var behind = Palette.ByKey(key)!;
            var ratio = Palette.ContrastRatio(
                dark ? edge.Dark : edge.Light,
                dark ? behind.Dark : behind.Light);
            Assert.True(ratio >= Indicator,
                $"{(dark ? "dark" : "light")} selection edge on {key} is {ratio:N2}:1");
        }
    }

    [Fact]
    public void Risk_colours_are_distinguishable_from_body_text()
    {
        // A warning that reads as ordinary text is not a warning. Each risk
        // colour must differ from the primary text colour, in both themes.
        var text = Palette.ByKey("Text.Primary")!;
        foreach (var key in new[] { "Risk.Destructive", "Risk.Security", "Risk.Performance" })
        {
            var risk = Palette.ByKey(key)!;
            Assert.NotEqual(text.Light, risk.Light);
            Assert.NotEqual(text.Dark, risk.Dark);
        }
    }

    [Fact]
    public void Dark_body_text_avoids_pure_white()
    {
        // Pure white on a dark ground haloes; One Dark uses #ABB2BF for this
        // reason and it is why the dark theme does not simply invert.
        var text = Palette.ByKey("Text.Primary")!;
        Assert.NotEqual("#FFFFFF", text.Dark.ToUpperInvariant());
    }

    [Fact]
    public void Every_entry_defines_both_themes()
    {
        foreach (var entry in Palette.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Light), entry.Key);
            Assert.False(string.IsNullOrWhiteSpace(entry.Dark), entry.Key);
            // Parseable, or the resource dictionary fails at runtime instead.
            Palette.ParseRgb(entry.Light);
            Palette.ParseRgb(entry.Dark);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Dividers_are_visible_without_being_heavy(bool dark)
    {
        // A hairline needs to be seen, not to meet a text threshold. Too little
        // contrast and panels merge; too much and a dense list looks caged.
        var background = Palette.ByKey("Chrome.Background")!;
        foreach (var key in Palette.Dividers)
        {
            var divider = Palette.ByKey(key)!;
            var ratio = Palette.ContrastRatio(
                dark ? divider.Dark : divider.Light,
                dark ? background.Dark : background.Light);
            Assert.InRange(ratio, 1.1, 4.0);
        }
    }

    [Fact]
    public void Keys_are_unique()
    {
        var keys = Palette.Entries.Select(e => e.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void Contrast_maths_matches_the_published_reference_values()
    {
        // Sanity-check the implementation against known ratios rather than
        // trusting it: black on white is 21:1, and a mid grey is 1:1 with itself.
        Assert.Equal(21.0, Palette.ContrastRatio("#000000", "#FFFFFF"), 1);
        Assert.Equal(1.0, Palette.ContrastRatio("#777777", "#777777"), 2);
        // One Dark's foreground on its background, measured rather than assumed:
        // 6.57:1, which passes AA and falls short of AAA.
        var oneDark = Palette.ContrastRatio("#ABB2BF", "#282C34");
        Assert.InRange(oneDark, 6.5, 6.7);
    }
}
