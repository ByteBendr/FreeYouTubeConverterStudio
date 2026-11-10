using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FreeYouTubeConverterStudio.Controls;

/// <summary>
/// A Windows-11-style settings row: icon, header, description and an arbitrary control on the right.
/// Usage: &lt;controls:SettingCard Glyph="&amp;#xE790;" Header="Theme" Description="..."&gt; &lt;ComboBox/&gt; &lt;/controls:SettingCard&gt;
/// </summary>
public sealed class SettingCard : ContentControl
{
    public SettingCard()
    {
        DefaultStyleKey = typeof(SettingCard);
    }

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingCard), new PropertyMetadata(""));

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingCard), new PropertyMetadata(""));

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SettingCard), new PropertyMetadata(""));
}
