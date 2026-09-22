using System.IO;
using SoulsTracker.Domain;

namespace SoulsTracker.Desktop.Tests;

public sealed class AppearancePreviewModelTests
{
    [Fact]
    public void LocalOnlyLegacyFontDoesNotCrashPreviewOrChangeDraft()
    {
        var applied = new OverlayAppearance("<Title>", "Font (local)", 24, "#FFFFFF", "#000000", "#000000", 0, 0, 0, OverlayTextAlignment.Left);
        var draft = new OverlayAppearanceDraft(); draft.Load(applied);
        var model = new AppearancePreviewModel(draft, applied);
        model.Update(OverlayTitleIconMode.Off, "0");
        Assert.Equal("Font (local)", draft.FontFamily);
        Assert.Equal("<Title>", draft.Title);
        Assert.Equal(24, model.Appearance.FontSize);
    }

    [Fact]
    public void OfflinePreviewBundlesActualRendererWithoutNetworkEntry()
    {
        var assembly = typeof(MainWindow).Assembly;
        using var stream = assembly.GetManifestResourceStream("SoulsTracker.Desktop.AppearancePreview.html");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        string html = reader.ReadToEnd();
        Assert.Contains("function renderHosted(", html);
        Assert.Contains("connect-src 'none'", html);
        Assert.Contains("data:image/png;base64,", html);
        Assert.DoesNotContain("WebSocket", html);
        Assert.DoesNotContain("hosted-client", html);
        Assert.DoesNotContain("overlay.beingkairo.com", html);
    }

    [Fact]
    public void PreviewKeepsInvalidFieldLastValueAndIndependentValidEdits()
    {
        var type = typeof(MainWindow).Assembly.GetType("SoulsTracker.Desktop.AppearancePreviewModel");
        Assert.NotNull(type);
        var draft = new OverlayAppearanceDraft(); draft.Load(OverlayAppearance.Default);
        dynamic model = Activator.CreateInstance(type, draft, OverlayAppearance.Default)!;
        model.Update(OverlayTitleIconMode.PrefixSkull, "0");
        Assert.Equal("0", (string)model.Value);
        Assert.False((bool)model.IsRepresentative);
        draft.FontSize = "36"; model.Update(OverlayTitleIconMode.PrefixSkull, "0");
        Assert.Equal(36, (int)model.Appearance.FontSize);
        draft.FontSize = "-"; draft.TextColor = "#123456";
        model.Update(OverlayTitleIconMode.SkullOnly, "Unavailable");
        Assert.Equal("-", draft.FontSize);
        Assert.NotEmpty(draft[nameof(draft.FontSize)]);
        Assert.Equal(36, (int)model.Appearance.FontSize);
        Assert.Equal("#123456", (string)model.Appearance.TextColor);
        Assert.Equal("skullOnly", (string)model.Appearance.TitleIconMode);
        Assert.Equal("123", (string)model.Value);
        Assert.True((bool)model.IsRepresentative);
        draft.Title = "<invalid>"; draft.BackgroundEnabled = true; draft.BackgroundOpacity = "50";
        model.Update(OverlayTitleIconMode.Off, "42");
        Assert.Equal(OverlayAppearance.Default.Title, (string)model.Appearance.Title);
        Assert.Equal(50, (int)model.Appearance.BackgroundOpacity);
        Assert.Equal("<invalid>", draft.Title);
    }
}
