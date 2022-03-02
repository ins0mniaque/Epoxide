using System.Runtime.CompilerServices;

namespace Epoxide.Linq.Expressions;

public static class ExpressionExtensions
{
    public static bool IsClosure ( this Expression node )
    {
        return Attribute.IsDefined ( node.Type, typeof ( CompilerGeneratedAttribute ) );
    }

    public static Expression MakeNullable ( this Expression node )
    {
        if ( node.IsNullable ( ) )
            return node;

        return Expression.Convert ( node, typeof ( Nullable < > ).MakeGenericType ( node.Type ) );
    }

    public static bool CanBeNull ( this Expression node )
    {
        // TODO: Handle types that cast non-null to null?
        var constant = node.Unconvert ( );
        if ( constant.NodeType == ExpressionType.Constant && ( (ConstantExpression) constant ).Value != null )
            return false;

        return IsNullable ( node ) && ! IsClosure ( node );
    }

    public static bool IsNullable ( this Expression node )
    {
        return ! node.Type.IsValueType || Nullable.GetUnderlyingType ( node.Type ) != null;
    }

    public static bool IsNullableStruct ( this Expression node )
    {
        return node.Type.IsValueType && Nullable.GetUnderlyingType ( node.Type ) != null;
    }

    public static Expression RemoveNullable ( this Expression node )
    {
        if ( node.IsNullableStruct ( ) )
            return Expression.Convert ( node, node.Type.GenericTypeArguments [ 0 ] );

        return node;
    }

    public static Expression Unconvert ( this Expression node )
    {
        while ( node.NodeType == ExpressionType.Convert )
            node = ( (UnaryExpression) node ).Operand;

        return node;
    }

    public static bool IsCollection ( this Expression node )
    {
        return node.Type.GetGenericInterfaceArguments ( typeof ( ICollection         < > ) ) != null ||
               node.Type.GetGenericInterfaceArguments ( typeof ( IReadOnlyCollection < > ) ) != null;
    }

    public static MemberExpression? ToWritable ( this Expression node )
    {
        node = node.Unconvert ( );
        if ( node.NodeType != ExpressionType.MemberAccess )
            return null;

        var memberAccess = (MemberExpression) node;
        if ( memberAccess.Member is PropertyInfo { CanWrite: true } or FieldInfo )
            return memberAccess;

        return null;
    }

    private static readonly HashSet < string > keywords = new ( )
    {
        "abstract", 
        "as", 
        "base", 
        "bool", 
        "break", 
        "byte", 
        "case", 
        "catch", 
        "char", 
        "checked", 
        "class", 
        "const", 
        "continue", 
        "decimal",
        "default", 
        "delegate", 
        "do", 
        "double", 
        "else", 
        "enum", 
        "event", 
        "explicit", 
        "extern", 
        "false", 
        "finally", 
        "fixed", 
        "float", 
        "for", 
        "foreach", 
        "goto", 
        "if", 
        "implicit", 
        "in", 
        "int", 
        "interface", 
        "internal", 
        "is", 
        "lock", 
        "long", 
        "namespace", 
        "new", 
        "null", 
        "object", 
        "operator", 
        "out", 
        "override", 
        "params", 
        "private", 
        "protected", 
        "public", 
        "readonly", 
        "ref", 
        "return", 
        "sbyte", 
        "sealed", 
        "short", 
        "sizeof", 
        "stackalloc", 
        "static", 
        "string", 
        "struct", 
        "switch", 
        "this", 
        "throw", 
        "true", 
        "try", 
        "typeof", 
        "uint", 
        "ulong", 
        "unchecked", 
        "unsafe", 
        "ushort", 
        "using", 
        "virtual", 
        "void", 
        "volatile", 
        "while"
    };

    public static ParameterExpression GenerateVariable ( this Expression node, IEnumerable < ParameterExpression >? existingVariables = null )
    {
        return Expression.Variable ( node.Type, node.GenerateVariableName ( existingVariables ) );
    }

    public static string GenerateVariableName ( this Expression node, IEnumerable < ParameterExpression >? existingVariables = null )
    {
        var name = ( Nullable.GetUnderlyingType ( node.Type ) ?? node.Type ).Name;
        if ( node.NodeType == ExpressionType.MemberAccess )
            name = ( (MemberExpression) node ).Member.Name;
        else if ( node.Type.IsInterface && name.Length > 1 && name [ 0 ] == 'I' )
            name = name.Substring ( 1 );

        if ( char.IsUpper ( name [ 0 ] ) )
            name = char.ToLowerInvariant ( name [ 0 ] ) + name.Substring ( 1 );

        var backtick = name.IndexOf ( '`' );
        if ( backtick >= 0 )
            name = name.Substring ( 0, backtick );

        if ( existingVariables != null && existingVariables.LastOrDefault ( variable => variable.Name.StartsWith ( name, StringComparison.Ordinal ) ) is { } match )
            name += int.TryParse ( match.Name.AsSpan ( name.Length ), out var index ) ? index + 1 : 2;

        if ( keywords.Contains ( name ) )
            name += "1";

        return name;
    }

    // TODO: Move to NullPropagator with extensions and self methods (non-propagated)
    // TODO: Rename RecursivePropagateNull?
    public static Expression PropagateNull ( this BinaryExpression binary, Expression left, Expression right )
    {
        if ( binary.NodeType == ExpressionType.Coalesce )
        {
            if ( binary.Left == left && binary.Right == right && left.CanBeNull ( ) && right.CanBeNull ( ) )
                return binary;

            return Expression.Coalesce ( left .MakeNullable ( ),
                                         right.MakeNullable ( ) );
        }

        if ( binary.Left == left && binary.Right == right )
            return binary;

        return Expression.MakeBinary ( binary.NodeType, left, right, binary.IsLiftedToNull, binary.Method, binary.Conversion );
    }

    public static Expression PropagateNull ( this ConditionalExpression condition, Expression ifTrue, Expression ifFalse )
    {
        if ( condition.IfTrue == ifTrue && condition.IfFalse == ifFalse && ifTrue.CanBeNull ( ) && ifFalse.CanBeNull ( ) )
            return condition;

        return Expression.Condition ( condition.Test, ifTrue.MakeNullable ( ), ifFalse.MakeNullable ( ) );
    }

    public static Expression PropagateNull ( this MemberExpression member, Expression? expression )
    {
        return PropagateNullIfNullable ( member, member.Expression, expression );
    }

    public static Expression PropagateNull ( this MethodCallExpression method, Expression? @object, IEnumerable < Expression > arguments )
    {
        return PropagateNullIfNullable ( method, method.Object, @object, method.Arguments, arguments );
    }

    private static Expression PropagateNullIfNullable ( Expression access, Expression? instance, Expression? propagatedInstance )
    {
        if ( instance != null && propagatedInstance != null && propagatedInstance.CanBeNull ( ) )
            return PropagateSingleNull ( access, instance, propagatedInstance );

        return access;
    }

    private static Expression PropagateNullIfNullable ( Expression access, Expression? instance, Expression? propagatedInstance, IReadOnlyCollection < Expression > arguments, IEnumerable < Expression > propagatedArguments )
    {
        var instances           = new List < Expression > ( );
        var propagatedInstances = new List < Expression > ( );

        if ( instance != null && propagatedInstance != null && propagatedInstance.CanBeNull ( ) )
        {
            instances          .Add ( instance );
            propagatedInstances.Add ( propagatedInstance );
        }

        using var argumentsEnumerator           = arguments          .GetEnumerator ( );
        using var propagatedArgumentsEnumerator = propagatedArguments.GetEnumerator ( );

        while ( argumentsEnumerator.MoveNext ( ) )
        {
            if ( ! propagatedArgumentsEnumerator.MoveNext ( ) )
                throw new ArgumentException ( "Less propagated arguments than arguments were provided", nameof ( propagatedArguments ) );

            if ( propagatedArgumentsEnumerator.Current.CanBeNull ( ) )
            {
                instances          .Add ( argumentsEnumerator          .Current );
                propagatedInstances.Add ( propagatedArgumentsEnumerator.Current );
            }
        }

        return instances.Count == 1 ? PropagateSingleNull   ( access, instances [ 0 ], propagatedInstances [ 0 ] ) :
               instances.Count >  1 ? PropagateMultipleNull ( access, instances,       propagatedInstances ) :
               access;
    }

    private readonly static ConstantExpression Null = Expression.Constant ( null );

    // TODO: Move assigns inside null test and merge blocks
    // TODO: Handle coalesce by replacing ifTrue constant
    private static Expression PropagateSingleNull ( Expression access, Expression instance, Expression propagatedInstance )
    {
        if ( instance == propagatedInstance )
        {
            access = access.MakeNullable ( );

            return Expression.Condition ( test:    Expression.Equal    ( instance, Null ),
                                          ifTrue:  Expression.Constant ( null, access.Type ),
                                          ifFalse: access );
        }

        var existing = propagatedInstance.GetVariables ( );
        var variable = propagatedInstance.GenerateVariable ( existing );
        var assign   = Expression.Assign ( variable, propagatedInstance );

        access = new ExpressionReplacer ( Replace ).Visit ( access ).MakeNullable ( );

        Expression Replace ( Expression node )
        {
            if ( node == instance )
                return instance.IsNullableStruct ( ) ? variable : variable.RemoveNullable ( );

            return node;
        }

        var test      = Expression.Equal ( variable, Null );
        var condition = Expression.Condition ( test:    test,
                                               ifTrue:  Expression.Constant ( null, access.Type ),
                                               ifFalse: access );

        return Expression.Block ( type:        access.Type,
                                  variables:   new [ ] { variable },
                                  expressions: new Expression [ ]
                                  {
                                      assign,
                                      condition
                                  } );
    }

    private static Expression PropagateMultipleNull ( Expression access, List < Expression > instance, List < Expression > propagatedInstance )
    {
        var variables   = new ParameterExpression [ instance.Count ];
        var expressions = new Expression          [ propagatedInstance.Count + 1 ];
        var existing    = propagatedInstance.SelectMany ( GetVariables );

        for ( var index = 0; index < variables.Length; index++ )
        {
            // TODO: Remove multiple array accesses
            variables   [ index ] = propagatedInstance [ index ].GenerateVariable ( existing );
            expressions [ index ] = Expression.Assign   ( variables [ index ], propagatedInstance [ index ] );
        }

        access = new ExpressionReplacer ( Replace ).Visit ( access ).MakeNullable ( );

        Expression Replace ( Expression node )
        {
            var index = instance.IndexOf ( node );
            if ( index >= 0 )
                return instance [ index ].IsNullableStruct ( ) ? variables [ index ] : variables [ index ].RemoveNullable ( );

            return node;
        }

        var test      = variables.Select    ( variable => Expression.Equal ( variable, Null ) )
                                 .Aggregate ( Expression.OrElse );
        var condition = Expression.Condition ( test:    test,
                                               ifTrue:  Expression.Constant ( null, access.Type ),
                                               ifFalse: access );

        expressions [ ^1 ] = condition;

        return Expression.Block ( type:        access.Type,
                                  variables:   variables,
                                  expressions: expressions );
    }

    private static IEnumerable < ParameterExpression > GetVariables ( this Expression node )
    {
        if ( node.NodeType == ExpressionType.Block )
            return ( (BlockExpression) node ).Variables;

        return Enumerable.Empty < ParameterExpression > ( );
    }
}