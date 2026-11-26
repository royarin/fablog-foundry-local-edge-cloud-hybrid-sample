using FabLog.Core;

namespace FabLog.DemoTests;

/// <summary>
/// The span detector's guard rails against a small model that returns clauses
/// instead of values. A clause hides the note from the cloud and swallows the
/// rule hits inside it, so the "N values held back" count stops being true.
/// </summary>
public class RedactionTests
{
    const string Brief =
        "- The etch process on chamber B using recipe RX-7 at 94.2%, with a ramp time of 4.5 seconds is running at 3rd shift.\n" +
        "- The etch06 process chamber A performed a post-clean with a particle count of 9 per wafer, using recipe RX-9 with a ramp time of 5.2 seconds.";

    static Task<IReadOnlyList<IpSpan>> Detect(string text, params string[] modelSays) =>
        Redaction.DetectAsync(text,
            new FakeChat((_, _) => "[" + string.Join(",", modelSays.Select(s => $"\"{s}\"")) + "]"));

    [Fact]
    public void Spelled_out_seconds_are_masked_by_rules()
    {
        var spans = Redaction.DetectByRules(Brief);
        Assert.Contains(spans, s => s.Text == "4.5 seconds");
        Assert.Contains(spans, s => s.Text == "5.2 seconds");
    }

    [Theory]
    [InlineData("0.8 torr")]
    [InlineData("300 watts")]
    [InlineData("40 pct")]
    [InlineData("12 slm")]
    public void Lowercase_and_spelled_units_are_masked_by_rules(string value)
    {
        var spans = Redaction.DetectByRules($"etch02 chmbr B soak at {value} - back in spec");
        Assert.Contains(spans, s => s.Text == value);
    }

    [Theory]
    [InlineData("chmbr A")]
    [InlineData("chamber D")]
    [InlineData("ch C")]
    public async Task Chamber_references_from_the_model_are_rejected(string chamber)
    {
        var text = $"etch09 {chamber} unif 94.2% on Delta Burn-In";
        var spans = await Detect(text, chamber, "Delta Burn-In");

        Assert.DoesNotContain(spans, s => s.Text == chamber);
        Assert.Contains(spans, s => s.Text == "Delta Burn-In" && s.Source == "model");
    }

    [Fact]
    public async Task Whole_sentence_from_the_model_is_rejected()
    {
        var clause = "etch process on chamber B using recipe RX-7 at 94.2%, with a ramp time of 4.5 seconds";
        var spans = await Detect(Brief, clause);

        Assert.DoesNotContain(spans, s => s.Text == clause);
        Assert.Contains(spans, s => s.Text == "RX-7");
        Assert.Contains(spans, s => s.Text == "94.2%");
    }

    [Fact]
    public async Task Model_span_containing_a_tool_id_is_rejected()
    {
        var spans = await Detect(Brief, "etch06 process");
        Assert.DoesNotContain(spans, s => s.Text.Contains("etch06", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task No_span_is_nested_inside_another()
    {
        // The model proposes a short phrase that wraps a rule hit.
        var spans = await Detect(Brief, "recipe RX-7");
        foreach (var s in spans)
            Assert.DoesNotContain(spans, o => !ReferenceEquals(o, s) && o.Text.Length > s.Text.Length &&
                o.Text.Contains(s.Text, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Every_span_appears_as_a_placeholder_in_the_masked_brief()
    {
        var spans = await Detect(Brief, "post-clean");
        var masked = Redaction.Mask(Brief, spans);

        Assert.All(spans, s => Assert.Contains(s.Placeholder, masked));
        Assert.DoesNotContain("seconds", masked);
        Assert.Contains("etch06", masked);   // tool IDs stay visible
    }
}
