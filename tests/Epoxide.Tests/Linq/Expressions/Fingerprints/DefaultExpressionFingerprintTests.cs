namespace Epoxide.Linq.Expressions.Fingerprints;

public class DefaultExpressionFingerprintTests
{
    [Fact]
    public void Properties()
    {
        // Arrange
        ExpressionType expectedNodeType = ExpressionType.Default;
        Type expectedType = typeof(object);

        // Act
        DefaultExpressionFingerprint fingerprint = new DefaultExpressionFingerprint(expectedNodeType, expectedType);

        // Assert
        Assert.Equal(expectedNodeType, fingerprint.NodeType);
        Assert.Equal(expectedType, fingerprint.Type);
    }

    [Fact]
    public void Comparison_Equality()
    {
        // Arrange
        ExpressionType nodeType = ExpressionType.Default;
        Type type = typeof(object);

        // Act
        DefaultExpressionFingerprint fingerprint1 = new DefaultExpressionFingerprint(nodeType, type);
        DefaultExpressionFingerprint fingerprint2 = new DefaultExpressionFingerprint(nodeType, type);

        // Assert
        Assert.Equal(fingerprint1, fingerprint2);
        Assert.Equal(fingerprint1.GetHashCode(), fingerprint2.GetHashCode());
    }

    [Fact]
    public void Comparison_Inequality_FingerprintType()
    {
        // Arrange
        ExpressionType nodeType = ExpressionType.Default;
        Type type = typeof(object);

        // Act
        DefaultExpressionFingerprint fingerprint1 = new DefaultExpressionFingerprint(nodeType, type);
        DummyExpressionFingerprint fingerprint2 = new DummyExpressionFingerprint(nodeType, type);

        // Assert
        Assert.NotEqual<ExpressionFingerprint>(fingerprint1, fingerprint2);
    }

    [Fact]
    public void Comparison_Inequality_NodeType()
    {
        // Arrange
        ExpressionType nodeType = ExpressionType.Default;
        Type type = typeof(object);

        // Act
        DefaultExpressionFingerprint fingerprint1 = new DefaultExpressionFingerprint(nodeType, type);
        DefaultExpressionFingerprint fingerprint2 = new DefaultExpressionFingerprint(nodeType, typeof(string));

        // Assert
        Assert.NotEqual(fingerprint1, fingerprint2);
    }
}
