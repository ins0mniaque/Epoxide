using System.Linq.Expressions;

using Epoxide.ExpressionUtil;

namespace Epoxide.Tests
{
    // Represents an ExpressionFingerprint that is of the wrong type.
    internal sealed class DummyExpressionFingerprint : ExpressionFingerprint
    {
        public DummyExpressionFingerprint(ExpressionType nodeType, Type type)
            : base(nodeType, type)
        {
        }
    }
}