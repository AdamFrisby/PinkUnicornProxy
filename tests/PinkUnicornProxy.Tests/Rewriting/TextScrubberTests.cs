using PinkUnicornProxy.Rewriting;

namespace PinkUnicornProxy.Tests.Rewriting;

public sealed class TextScrubberTests
{
    [Fact]
    public void RemovesOnlyTheSentenceContainingTheRejectedClaim()
    {
        ScrubResult result = TextScrubber.RemoveClaim(
            "The database is healthy. DNS is the likely cause. Keep checking the certificate.",
            "DNS");

        Assert.True(result.Changed);
        Assert.Equal("The database is healthy. Keep checking the certificate.", result.Text);
        Assert.False(result.ProtectedMatchFound);
    }

    [Fact]
    public void DoesNotMatchInsideInlineCodeOrUrls()
    {
        ScrubResult result = TextScrubber.RemoveClaim(
            "Inspect `DNS` and https://example.test/DNS for the literal token.",
            "DNS");

        Assert.False(result.Changed);
        Assert.True(result.ProtectedMatchFound);
    }

    [Fact]
    public void TreatsFencedCodeAsProtected()
    {
        ScrubResult result = TextScrubber.RemoveClaim("```text\nDNS\n```", "DNS");

        Assert.False(result.Changed);
        Assert.True(result.ProtectedMatchFound);
    }

    [Fact]
    public void TreatsTildeFencedCodeAsProtected()
    {
        ScrubResult result = TextScrubber.RemoveClaim("~~~text\nDNS\n~~~", "DNS");

        Assert.False(result.Changed);
        Assert.True(result.ProtectedMatchFound);
    }

    [Theory]
    [InlineData("Inspect ``DNS ` literal`` now.")]
    [InlineData("    DNS")]
    [InlineData("        DNS")]
    [InlineData("  \tDNS")]
    [InlineData("Inspect ftp://example.test/DNS now.")]
    [InlineData("Inspect [the runbook](/DNS) now.")]
    public void TreatsOtherMarkedCodeAndUrlFormsAsProtected(string text)
    {
        ScrubResult result = TextScrubber.RemoveClaim(text, "DNS");

        Assert.False(result.Changed);
        Assert.True(result.ProtectedMatchFound);
    }

    [Fact]
    public void DoesNotTreatRejectedClaimAsAWordPrefix()
    {
        ScrubResult result = TextScrubber.RemoveClaim("DNSSEC validation failed.", "DNS");

        Assert.False(result.Changed);
        Assert.False(result.ProtectedMatchFound);
    }

    [Fact]
    public void RefusesToDeleteSentenceContainingProtectedEvidence()
    {
        ScrubResult result = TextScrubber.RemoveClaim(
            "DNS is wrong; inspect `dig DNS` and https://example.test/evidence.",
            "DNS");

        Assert.False(result.Changed);
        Assert.True(result.ProtectedMatchFound);
    }

    [Fact]
    public void MatchesWhitespaceInsideARejectedPhraseAcrossLines()
    {
        ScrubResult result = TextScrubber.RemoveClaim(
            "The connection\npool is the cause.",
            "connection pool");

        Assert.True(result.Changed);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public void PreservesUnrelatedLineAfterRemovingAClaim()
    {
        ScrubResult result = TextScrubber.RemoveClaim(
            "DNS is the cause\nKeep checking the certificate",
            "DNS");

        Assert.True(result.Changed);
        Assert.Equal("Keep checking the certificate", result.Text);
    }
}
