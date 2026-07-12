using PinkUnicornProxy.Rewriting;

namespace PinkUnicornProxy.Tests.Rewriting;

public sealed class CorrectionDetectorTests
{
    [Theory]
    [InlineData(
        "No, it's not DNS. It's an expired TLS certificate.",
        "DNS",
        "It is an expired TLS certificate.",
        false)]
    [InlineData(
        "No it's not that; it is a race condition",
        "that",
        "It is a race condition.",
        true)]
    [InlineData(
        "No, not the database — it is the exhausted connection pool.",
        "the database",
        "It is the exhausted connection pool.",
        false)]
    [InlineData(
        "No, it isn't the cache.",
        "the cache",
        "Reassess the issue using the remaining evidence.",
        false)]
    [InlineData(
        "Correction: The certificate expired yesterday.",
        null,
        "The certificate expired yesterday.",
        true)]
    [InlineData(
        "[[forget: DNS]] [[truth: The TLS certificate has expired]]",
        "DNS",
        "The TLS certificate has expired.",
        false)]
    public void DetectsHighConfidenceCorrections(
        string text,
        string? rejected,
        string affirmative,
        bool isDeictic)
    {
        bool detected = CorrectionDetector.TryDetect(text, 16_384, out CorrectionIntent? intent);

        Assert.True(detected);
        Assert.NotNull(intent);
        Assert.Equal(rejected, intent.RejectedClaim);
        Assert.Equal(affirmative, intent.AffirmativeText);
        Assert.Equal(isDeictic, intent.IsDeictic);
    }

    [Theory]
    [InlineData("Do not delete production data.")]
    [InlineData("The service is not ready yet.")]
    [InlineData("Actually, I have another question.")]
    [InlineData("The string says: no, it's not DNS. It's TLS.")]
    [InlineData("```text\nNo, it's not DNS. It's TLS.\n```")]
    [InlineData("`[[forget: DNS]] [[truth: TLS]]`")]
    [InlineData("See https://example.test/[[forget:%20DNS]]")]
    [InlineData("Evidence first. [[forget: DNS]] [[truth: TLS]]")]
    [InlineData("No, it is not DNS. Please inspect the logs.")]
    public void IgnoresOrdinaryNegativesAndQuotedExamples(string text)
    {
        Assert.False(CorrectionDetector.TryDetect(text, 16_384, out _));
    }

    [Fact]
    public void IgnoresTextOverConfiguredLimit()
    {
        string text = "No, it's not DNS. It's TLS." + new string('x', 100);

        Assert.False(CorrectionDetector.TryDetect(text, 16, out _));
    }
}
