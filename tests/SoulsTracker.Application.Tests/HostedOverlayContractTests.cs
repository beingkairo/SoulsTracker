using SoulsTracker.Application;
using System.Text.Json;
using System.Text.Json.Nodes;
using SoulsTracker.Domain;
using System.Text;

namespace SoulsTracker.Application.Tests;

public sealed class HostedOverlayContractTests
{
    private static JsonObject Corpus => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts.json")))!.AsObject();

    [Fact]
    public void DefaultAndNormalizedAppearanceMatchesDomainAndGoldenFields()
    {
        var golden = HostedOverlayJson.Parse(Corpus["valid"]![4]!.ToJsonString());
        Assert.Equal(golden.Appearance, HostedAppearance.From(new OverlayPresentationConfiguration(true, false), "0"));
        var blank = HostedOverlayJson.Parse(Corpus["valid"]![5]!.ToJsonString()).Appearance!;
        Assert.Equal(string.Empty, blank.Title);
        Assert.Equal("#ABCDEF", blank.TextColor);
        foreach (OverlayTitleIconMode icon in Enum.GetValues<OverlayTitleIconMode>())
        {
            var a = HostedAppearance.From(new OverlayPresentationConfiguration(true, true, false, icon), "0");
            Assert.Equal(icon switch { OverlayTitleIconMode.Off => "off", OverlayTitleIconMode.PrefixSkull => "prefixSkull", _ => "skullOnly" }, a.TitleIconMode);
        }
    }

    [Fact]
    public void ExactByteLimitAndUnicodeTitleFontLimitsAreBounded()
    {
        string json = Corpus["valid"]![0]!.ToJsonString();
        string exact = json + new string(' ', HostedOverlayJson.MaximumBytes - Encoding.UTF8.GetByteCount(json));
        _ = HostedOverlayJson.Parse(exact);
        Assert.Throws<JsonException>(() => HostedOverlayJson.Parse(exact + " "));
        JsonNode style = Corpus["valid"]![4]!.DeepClone();
        style["appearance"]!["fontFamily"] = new string('F', 128);
        style["appearance"]!["title"] = new string('\u754c', 40);
        _ = HostedOverlayJson.Parse(style.ToJsonString());
        style["appearance"]!["fontFamily"] = new string('F', 129);
        Assert.Throws<JsonException>(() => HostedOverlayJson.Parse(style.ToJsonString()));
        foreach (string field in new[] { "title", "fontFamily" })
        {
            style = Corpus["valid"]![4]!.DeepClone();
            style["appearance"]![field] = "\u0085 \u754c \u0085";
            if (field == "title") Assert.Equal("\u754c", HostedOverlayJson.Parse(style.ToJsonString()).Appearance!.Title);
            else Assert.Throws<JsonException>(() => HostedOverlayJson.Parse(style.ToJsonString()));
        }
    }

    [Fact]
    public void CompleteFieldAllowlistRejectsMissingUnknownAndWronglyTypedValues()
    {
        foreach (string channel in new[] { "death", "appearance" })
        {
            JsonNode baseline = Corpus["valid"]![6]!;
            foreach (string field in baseline[channel]!.AsObject().Select(p => p.Key))
            {
                JsonNode changed = baseline.DeepClone();
                changed[channel]!.AsObject().Remove(field);
                Assert.Throws<JsonException>(() => HostedOverlayJson.Parse(changed.ToJsonString()));
            }
            foreach (JsonNode? forbidden in Corpus["forbiddenFields"]!.AsArray())
            {
                JsonNode changed = baseline.DeepClone();
                changed[channel]![forbidden!.GetValue<string>()] = "forbidden";
                Assert.Throws<JsonException>(() => HostedOverlayJson.Parse(changed.ToJsonString()));
                changed = baseline.DeepClone();
                changed[forbidden.GetValue<string>()] = "forbidden";
                Assert.Throws<JsonException>(() => HostedOverlayJson.Parse(changed.ToJsonString()));
            }
        }
        foreach (var field in Corpus["invalidDeath"]!.AsObject())
            foreach (JsonNode? invalid in field.Value!.AsArray())
            {
                JsonNode changed = Corpus["valid"]![0]!.DeepClone();
                changed["death"]![field.Key] = invalid?.DeepClone();
                Assert.Throws<JsonException>(() => HostedOverlayJson.Parse(changed.ToJsonString()));
            }
        var envelope = HostedOverlayJson.Parse(Corpus["valid"]![6]!.ToJsonString());
        JsonNode serialized = JsonNode.Parse(HostedOverlayJson.Serialize(envelope))!;
        Assert.Equal(["v", "type", "death", "appearance"], serialized.AsObject().Select(p => p.Key));
        Assert.Equal(["revision", "value", "availability"], serialized["death"]!.AsObject().Select(p => p.Key));
        Assert.Equal(Corpus["valid"]![4]!["appearance"]!.AsObject().Select(p => p.Key), serialized["appearance"]!.AsObject().Select(p => p.Key));
    }

    [Fact]
    public void SemanticDiffIgnoresRevisionsAndKeepsChannelsIndependent()
    {
        var before = HostedOverlayJson.Parse(Corpus["valid"]![6]!.ToJsonString());
        var same = before with { Death = before.Death! with { Revision = "50" }, Appearance = before.Appearance! with { Revision = "60", Title = " Deaths ", TextColor = "#000000" } };
        Assert.Null(HostedOverlayDiff.Between(before, same));
        var deathOnly = HostedOverlayDiff.Between(before, same with { Death = same.Death! with { Value = "0" } });
        Assert.True(deathOnly!.HasDeath);
        Assert.Null(deathOnly.Appearance);
        var styleOnly = HostedOverlayDiff.Between(before, same with { Appearance = same.Appearance! with { Enabled = false } });
        Assert.False(styleOnly!.HasDeath);
        Assert.Null(styleOnly.Death);
        Assert.NotNull(styleOnly.Appearance);
        Assert.DoesNotContain("death", HostedOverlayJson.Serialize(styleOnly), StringComparison.Ordinal);
        var startup = new HostedOverlayEnvelope("update", false, null, before.Appearance);
        Assert.Null(HostedOverlayDiff.Between(before, startup));
        var combined = HostedOverlayDiff.Between(before, same with { Death = same.Death! with { Value = "0" }, Appearance = same.Appearance! with { Enabled = false } });
        Assert.True(combined!.HasDeath);
        Assert.NotNull(combined.Appearance);
        Assert.Equal(combined, HostedOverlayJson.Parse(HostedOverlayJson.Serialize(combined)));
        var unavailable = HostedOverlayDiff.Between(before, before with { Death = before.Death! with { Value = null, Availability = "unavailable" } });
        Assert.True(unavailable!.HasDeath);
        Assert.Null(unavailable.Death!.Value);
    }

    [Fact]
    public void SharedGoldenCorpusValidatesCompleteChannelsAndStrictSchema()
    {
        foreach (JsonNode? value in Corpus["valid"]!.AsArray())
        {
            var parsed = HostedOverlayJson.Parse(value!.ToJsonString());
            Assert.Equal(parsed, HostedOverlayJson.Parse(HostedOverlayJson.Serialize(parsed)));
        }
        foreach (JsonNode? value in Corpus["invalid"]!.AsArray())
            Assert.ThrowsAny<JsonException>(() => HostedOverlayJson.Parse(value!.GetValue<string>()));
        JsonObject baseline = Corpus["valid"]![4]!.AsObject();
        foreach (var field in Corpus["invalidAppearance"]!.AsObject())
            foreach (JsonNode? invalid in field.Value!.AsArray())
            {
                JsonNode changed = baseline.DeepClone();
                changed["appearance"]![field.Key] = invalid?.DeepClone();
                Assert.Throws<JsonException>(() => HostedOverlayJson.Parse(changed.ToJsonString()));
            }
        foreach (string field in new[] { "revision", "value" })
            foreach (JsonNode? invalid in Corpus["invalidDecimals"]!.AsArray())
            {
                JsonNode changed = Corpus["valid"]![0]!.DeepClone();
                changed["death"]![field] = invalid!.DeepClone();
                Assert.Throws<JsonException>(() => HostedOverlayJson.Parse(changed.ToJsonString()));
            }
    }

    [Fact]
    public void DeathUpdateRoundTripsWithoutLosingIntegerPrecisionOrAddingAppearance()
    {
        const string json = "{\"v\":1,\"type\":\"update\",\"death\":{\"revision\":\"9223372036854775807\",\"value\":\"9007199254740993\",\"availability\":\"available\"}}";
        Assert.Equal(json, HostedOverlayJson.Serialize(HostedOverlayJson.Parse(json)));
    }
}
