using SoulsTracker.Domain;

namespace SoulsTracker.Desktop.Tests;

public sealed class AppearanceValidationTests
{
    [Fact]
    public void TitleWhitespaceKeepsTheExistingDomainContract()
    {
        var draft = new OverlayAppearanceDraft(); draft.Load(OverlayAppearance.Default);
        draft.Title = new string(' ', 50) + "Total";
        Assert.Equal("Total", draft.ToDomain(OverlayTextAlignment.Left).Title);
    }
    [Fact]
    public void AllInvalidFieldsAreReportedBeforeConversion()
    {
        var draft = new OverlayAppearanceDraft();
        draft.Load(OverlayAppearance.Default);
        draft.FontSize = "97";
        draft.TextColor = "red";
        draft.OutlineColor = "#abcd";
        Assert.ThrowsAny<ArgumentException>(() => draft.ToDomain(OverlayTextAlignment.Left));
        var errors = Assert.IsAssignableFrom<System.ComponentModel.IDataErrorInfo>(draft);
        Assert.Contains("96", errors[nameof(draft.FontSize)]);
        Assert.Contains("#RRGGBB", errors[nameof(draft.TextColor)]);
        Assert.Contains("#RRGGBB", errors[nameof(draft.OutlineColor)]);
        Assert.Empty(errors[nameof(draft.ShadowColor)]);
    }
}
