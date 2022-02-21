using System.Linq.Expressions;
using System.Text;

namespace Epoxide.Linq.Expressions;

public class CachedExpressionCompilerTests
{
    [Fact]
    public void Compiler_CompileFromConstLookup()
    {
        // Arrange
        Expression<Func<string, int>> expr = model => 42;

        // Act
        var func = CachedExpressionCompiler.Compile(expr);
        int result = func("any model");

        // Assert
        Assert.Equal(42, result);
    }

    [Fact]
    public void Compiler_CompileFromFingerprint()
    {
        // Arrange
        Expression<Func<string, int>> expr = s => 20 * s.Length;

        // Act
        var func = CachedExpressionCompiler.Compile(expr);
        int result = func("hello");

        // Assert
        Assert.Equal(100, result);
    }

    [Fact]
    public void Compiler_CompileFromIdentityFunc()
    {
        // Arrange
        Expression<Func<string, string>> expr = model => model;

        // Act
        var func = CachedExpressionCompiler.Compile(expr);
        string result = func("hello");

        // Assert
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Compiler_CompileFromMemberAccess_CapturedLocal()
    {
        // Arrange
        string capturedLocal = "goodbye";
        Expression<Func<string, string>> expr = _ => capturedLocal;

        // Act
        var func = CachedExpressionCompiler.Compile(expr);
        string result = func("hello");

        // Assert
        Assert.Equal("goodbye", result);
    }

    [Fact]
    public void Compiler_CompileFromMemberAccess_ParameterInstanceMember()
    {
        // Arrange
        Expression<Func<string, int>> expr = s => s.Length;

        // Act
        var func = CachedExpressionCompiler.Compile(expr);
        int result = func("hello");

        // Assert
        Assert.Equal(5, result);
    }

    [Fact]
    public void Compiler_CompileFromMemberAccess_StaticMember()
    {
        // Arrange
        Expression<Func<string, string>> expr = _ => String.Empty;

        // Act
        var func = CachedExpressionCompiler.Compile(expr);
        string result = func("hello");

        // Assert
        Assert.Equal("", result);
    }

    [Fact]
    public void Compiler_CompileSlow()
    {
        // Arrange
        Expression<Func<string, string>> expr = s => new StringBuilder(s).ToString();

        // Act
        var func = CachedExpressionCompiler.Compile(expr);
        string result = func("hello");

        // Assert
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Compile()
    {
        // Arrange
        Expression<Func<string, string>> expr = s => new StringBuilder(s).ToString();

        // Act
        var func = CachedExpressionCompiler.Compile(expr);
        string result = func("hello");

        // Assert
        Assert.Equal("hello", result);
    }
}
