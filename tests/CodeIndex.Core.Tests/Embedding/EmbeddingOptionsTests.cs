using CodeIndex.Core.Embedding;
using Xunit;

namespace CodeIndex.Core.Tests.Embedding;

public sealed class EmbeddingOptionsTests
{
    [Fact]
    public void Validate_AcceptsTheCompiledInDefaults()
    {
        EmbeddingOptions options = new();

        // Must not throw.
        options.Validate();
    }

    [Fact]
    public void Validate_AcceptsAGenuineNonDefaultEndpointAndModel()
    {
        EmbeddingOptions options = new() { Endpoint = "http://example.internal:9999", Model = "nomic-embed-text" };

        // Must not throw.
        options.Validate();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_ThrowsWhenEndpointIsEmpty_NamingTheSetting(string endpoint)
    {
        // Pins down the exact shape reported in the field: Claude Code substitutes an unset
        // `${CODEINDEX_Embedding__Endpoint}` placeholder with an empty string rather than omitting
        // the key, which .NET's environment-variable configuration provider then binds as an
        // explicit override — clobbering the compiled-in default above with "". Before this
        // validation existed, that value flowed straight into `new Uri(Endpoint)` and failed with
        // an unhelpful "Invalid URI: The URI is empty." that never named Embedding:Endpoint.
        EmbeddingOptions options = new() { Endpoint = endpoint };

        ArgumentException ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Contains("Embedding:Endpoint", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("URI is empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_ThrowsWhenEndpointIsNotAWellFormedAbsoluteUri()
    {
        // Uri.TryCreate(..., UriKind.Absolute, ...) is permissive about the scheme (even
        // "localhost:11434" parses as an absolute URI with scheme "localhost"), so this needs a
        // string with no colon-delimited scheme segment at all to genuinely fail parsing.
        EmbeddingOptions options = new() { Endpoint = "not a uri at all, just text" };

        ArgumentException ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Contains("Embedding:Endpoint", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_ThrowsWhenModelIsEmpty_NamingTheSetting(string model)
    {
        EmbeddingOptions options = new() { Model = model };

        ArgumentException ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Contains("Embedding:Model", ex.Message, StringComparison.Ordinal);
    }
}
