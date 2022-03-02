namespace Epoxide.Linq.Expressions;

// TODO: Reuse variables when expression fingerprint matches
// TODO: Rename
public class ExpressionStateMachineBuilderContext
{
    public        readonly MethodInfo result;
    public static readonly MethodInfo exception2 = typeof ( IExpressionStateMachine ).GetMethod ( nameof ( IExpressionStateMachine.SetException ) );
    public static readonly MethodInfo schedule   = typeof ( IExpressionStateMachine ).GetMethod ( nameof ( IExpressionStateMachine.Schedule ) );
    public static readonly MethodInfo waitFor    = typeof ( IExpressionStateMachine ).GetMethod ( nameof ( IExpressionStateMachine.Await ) );
    public static readonly MethodInfo read       = typeof ( IExpressionStateMachine ).GetMethod ( nameof ( IExpressionStateMachine.Get ) );
    public static readonly MethodInfo write      = typeof ( IExpressionStateMachine ).GetMethod ( nameof ( IExpressionStateMachine.Set ) );

    public ExpressionStateMachineBuilderContext ( LambdaExpression lambda )
    {
        var type = typeof ( IExpressionStateMachine < > ).MakeGenericType ( lambda.Body.Type );

        result = type.GetMethod ( nameof ( IExpressionStateMachine < object >.SetResult ) );

        StateMachine       = Expression.Parameter ( typeof ( IExpressionStateMachine < > ).MakeGenericType ( lambda.Body.Type ), "λ" );
        Parameters         = lambda.Parameters;
        WritableExpression = lambda.Body.ToWritable ( );

        if ( WritableExpression != null && WritableExpression.Expression != null && WritableExpression.Expression.NodeType == ExpressionType.Constant )
            WritableTarget = ( (ConstantExpression) WritableExpression.Expression ).Value;
    }

    public ParameterExpression StateMachine { get; }

    public IReadOnlyDictionary < ParameterExpression, int > Variables => variables;
    public IReadOnlyCollection < ParameterExpression >      Parameters { get; }

    public MemberExpression?    WritableExpression     { get; }
    public ParameterExpression? WritableTargetVariable { get; private set; }
    public object?              WritableTarget         { get; private set; }
    public int?                 WritableTargetId => WritableTargetVariable != null &&
                                                    variables.TryGetValue ( WritableTargetVariable, out var id ) ? id : null;

    private readonly Dictionary < ParameterExpression, int  > variables  = new Dictionary<ParameterExpression, int> ( );
    private readonly HashSet    < ParameterExpression >       parameters = new HashSet<ParameterExpression> ( );

    public int GetId ( ParameterExpression variable )
    {
        if ( ! variables.TryGetValue ( variable, out var id ) )
            variables [ variable ] = id = variables.Count + ( Parameters?.Count ?? 0 );

        return id;
    }

    public bool IsUsed     ( ParameterExpression parameter ) => parameters.Contains ( parameter );
    public void MarkAsUsed ( ParameterExpression parameter ) => parameters.Add      ( parameter );

    public Expression Read ( int id, ParameterExpression variable )
    {
        var typedRead = read.MakeGenericMethod ( variable.Type );

        return Expression.Call ( StateMachine, typedRead, Expression.Constant ( id ), variable );
    }

    public Expression Assign ( int id, ParameterExpression variable, Expression value )
    {
        var unconverted = value.Unconvert ( );

        if ( WritableExpression != null && WritableTarget == null && unconverted.NodeType == ExpressionType.MemberAccess )
        {
            var valueMember = (MemberExpression) unconverted;
            if ( valueMember.Member == WritableExpression.Member && valueMember.Expression.NodeType == ExpressionType.Parameter )
                WritableTargetVariable = (ParameterExpression) valueMember.Expression;
        }

        var typedRead  = read .MakeGenericMethod ( variable.Type );
        var typedWrite = write.MakeGenericMethod ( variable.Type );

        var readValue  = Expression.Call ( StateMachine, typedRead, Expression.Constant ( id ), variable );
        var writeValue = Expression.Call ( StateMachine, typedWrite, Expression.Constant ( id ), value );

        return Expression.Condition ( test:    readValue,
                                      ifTrue:  variable,
                                      ifFalse: writeValue );
    }

    public Expression SetException ( Expression exception )
    {
        // TODO: Static
        var capture = typeof ( BindingException ).GetMethod ( nameof ( BindingException.Capture ) );

        return Expression.Call ( StateMachine, exception2, Expression.Call ( capture, exception ) );
    }

    public Expression SetResult ( Expression value )
    {
        var resultType = StateMachine.Type.GetGenericArguments ( ) [ 0 ];
        if ( value.Type != resultType )
            value = Expression.Convert ( value, resultType );

        return Expression.Call ( StateMachine, result, value );
    }

    public Expression Schedule ( int id, Expression expression, MemberInfo member )
    {
        return Expression.Call ( StateMachine, schedule.MakeGenericMethod ( expression.Type ), Expression.Constant ( id ), expression, Expression.Constant ( member, typeof ( MemberInfo ) ) );
    }

    public Expression Await ( int id, Expression expression )
    {
        return Expression.Call ( StateMachine, waitFor.MakeGenericMethod ( expression.Type ), Expression.Constant ( id ), expression );
    }
}

public static class StateMachineBuilder
{
    public static Expression MakeStateMachine ( this Expression expression, ExpressionStateMachineBuilderContext context )
    {
        if ( expression.Type == typeof ( ExpressionState ) )
            return expression;

        return Expression.Condition ( test:    context.Await ( -1, expression ),
                                      ifTrue:  Expression.Constant ( ExpressionState.Awaiting ),
                                      ifFalse: context.SetResult ( expression ) );
    }

    public static Expression BindStateMachineParameters ( this Expression expression, ExpressionStateMachineBuilderContext context )
    {
        if ( context.Parameters == null || context.Parameters.Count == 0 )
            return expression;

        var parameters = context.Parameters.Select ( (p, i) => (Parameter: p, Assign: AssignParameter ( p, i )) )
                                           .Where  ( e => context.IsUsed ( e.Parameter ) )
                                           .ToList ( );

        if ( expression.NodeType != ExpressionType.Block )
        {
            return Expression.Block ( type:        expression.Type,
                                      variables:   parameters.Select ( e => e.Parameter ),
                                      expressions: parameters.Select ( e => e.Assign    ).Append ( expression ) );
        }

        var block = (BlockExpression) expression;

        return Expression.Block ( type:        block.Type,
                                  variables:   parameters.Select ( e => e.Parameter ).Concat ( block.Variables   ),
                                  expressions: parameters.Select ( e => e.Assign    ).Concat ( block.Expressions ) );

        BinaryExpression? AssignParameter ( ParameterExpression parameter, int id )
        {
            // TODO: MissingArgumentException
            var missing = Expression.Throw ( Expression.New ( typeof ( ArgumentException ) ), parameter.Type );

            return Expression.Assign ( parameter, Expression.Condition ( context.Read ( id, parameter ), parameter, missing ) );
        }
    }

    public static Expression AddStateMachineExceptionHandling ( this Expression expression, ExpressionStateMachineBuilderContext context )
    {
        var exception  = Expression.Parameter ( typeof ( Exception ), "exception" );
        var catchBlock = Expression.Catch     ( exception, context.SetException ( exception ) );

        return Expression.TryCatch ( expression, catchBlock );
    }

    public static Expression ToStateMachine ( this ParameterExpression parameter, ExpressionStateMachineBuilderContext context )
    {
        context.MarkAsUsed ( parameter );

        return parameter;
    }

    public static Expression ToStateMachine ( this MemberExpression member, Expression? expression, ExpressionStateMachineBuilderContext context )
    {
        return MakeSchedulable ( member, member.Expression, expression, context );
    }

    public static Expression ToStateMachine ( this MethodCallExpression method, Expression? @object, IEnumerable < Expression > arguments, ExpressionStateMachineBuilderContext context )
    {
        return MakeSchedulable ( method, method.Object, @object, method.Arguments, arguments, context );
    }

    public static Expression ToStateMachine ( this BinaryExpression binary, Expression? left, Expression? right, ExpressionStateMachineBuilderContext context )
    {
        var isCoalesceToNull = binary.NodeType == ExpressionType.Coalesce &&
                               right .NodeType == ExpressionType.Constant &&
                               ( (ConstantExpression) right ).Value == null;

        if ( isCoalesceToNull && left.NodeType == ExpressionType.Block )
        {
            var replaced = new List < ParameterExpression > ( );

            left = new ReversedExpressionReplacer ( RemoveFallbacks ).Visit ( left );

            Expression RemoveFallbacks ( Expression node )
            {
                if ( node.NodeType == ExpressionType.Conditional )
                {
                    var condition = (ConditionalExpression) node;

                    // TODO: Compare with stored ConstantExpression for Fallback
                    if ( condition.IfTrue.NodeType == ExpressionType.Constant && Equals ( ( (ConstantExpression) condition.IfTrue ).Value, ExpressionState.Fallback ) )
                    {
                        var assign           = ( (BinaryExpression) condition.Test ).Left;
                        var variable         = assign.NodeType == ExpressionType.Parameter ? (ParameterExpression) assign : (ParameterExpression) ( (BinaryExpression) assign ).Left;
                        var waitForCondition = (ConditionalExpression) condition.IfFalse;
                        var waitFor          = (MethodCallExpression)  waitForCondition.Test;

                        replaced.Add ( variable );

                        return Expression.Condition ( test:     Expression.Call ( waitFor.Object, waitFor.Method, waitFor.Arguments [ 0 ], assign ),
                                                      ifTrue:   waitForCondition.IfTrue,
                                                      ifFalse : waitForCondition.IfFalse );
                    }
                }

                return node;
            }

            // TODO: This needs force type change with MakeNullable, and RemoveNullable at usage
            //       Should probably propagate nulls before building the state machine
            left = new ExpressionReplacer ( CoalesceAccess ).Visit ( left );

            Expression CoalesceAccess ( Expression node )
            {
                if ( node.NodeType == ExpressionType.MemberAccess )
                {
                    var member = (MemberExpression) node;
                    if ( member.Expression != null && member.Expression.NodeType == ExpressionType.Parameter && replaced.Contains ( member.Expression ) )
                        return Expression.Condition ( test:    Expression.Equal ( member.Expression, Null ),
                                                      ifTrue:  node,
                                                      ifFalse: Expression.Constant ( null, node.Type ) );
                }

                if ( node.NodeType == ExpressionType.Call )
                {
                    var method = (MethodCallExpression) node;
                    if ( method.Object != null && method.Object.NodeType == ExpressionType.Parameter && replaced.Contains ( method.Object ) )
                        return Expression.Condition ( test:    Expression.Equal ( method.Object, Null ),
                                                      ifTrue:  node,
                                                      ifFalse: Expression.Constant ( null, node.Type ) );
                }

                return node;
            }
        
            return left;
        }

        return MakeSchedulable ( binary, binary.Left, left, new [ ] { binary.Right }, Enumerable.Repeat ( right, 1 ), context );
    }

    // TODO: Replace IsNullable with IsSchedulable
    // TODO: Rename
    private static Expression MakeSchedulable ( Expression access, Expression? instance, Expression? propagatedInstance, ExpressionStateMachineBuilderContext context )
    {
        if ( instance != null && propagatedInstance != null && ( instance.CanBeNull ( ) || propagatedInstance.Type == typeof ( ExpressionState ) ) )
            return MakeSingleSchedulable ( access, instance, propagatedInstance, context );

        return access;
    }

    private static Expression MakeSchedulable ( Expression access, Expression? instance, Expression? propagatedInstance, IReadOnlyCollection < Expression > arguments, IEnumerable < Expression > propagatedArguments, ExpressionStateMachineBuilderContext context )
    {
        var instances           = new List < Expression > ( );
        var propagatedInstances = new List < Expression > ( );

        if ( instance != null && propagatedInstance != null && ( instance.CanBeNull ( ) || propagatedInstance.Type == typeof ( ExpressionState ) ) )
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

            if ( argumentsEnumerator.Current.CanBeNull ( ) || propagatedArgumentsEnumerator.Current.Type == typeof ( ExpressionState ) )
            {
                instances          .Add ( argumentsEnumerator          .Current );
                propagatedInstances.Add ( propagatedArgumentsEnumerator.Current );
            }
        }

        return instances.Count == 1 ? MakeSingleSchedulable    ( access, instances [ 0 ], propagatedInstances [ 0 ], context ) :
               instances.Count >  1 ? MakeMultipleSchedulables ( access, instances,       propagatedInstances,       context ) :
               access;
    }

    private readonly static ConstantExpression Null = Expression.Constant ( null );

    // TODO: Clean up propagated instance detection (must return BindingStatus)
    private static Expression MakeSingleSchedulable ( Expression access, Expression instance, Expression propagatedInstance, ExpressionStateMachineBuilderContext context )
    {
        var variables = new List < (ParameterExpression Variable, BinaryExpression Assign) > ( );

        propagatedInstance.GetVariablesAndAssigns ( variables, null, context );

        var variable = variables.First ( ).Variable;

        access = new ExpressionReplacer ( ReplaceInstance ).Visit ( access ).MakeNullable ( );

        Expression ReplaceInstance ( Expression node )
        {
            if ( node == instance )
                return instance.IsNullableStruct ( ) ? variable : variable.RemoveNullable ( );

            return node;
        }

        var accessed     = access.GenerateVariable ( context.Variables.Keys );
        var accessedId   = context.GetId ( accessed );
        var assignAccess = Expression.Assign   ( accessed, context.Assign ( accessedId, accessed, access ) );

        var waitForAccess = Expression.Condition ( test:    context.Await  ( accessedId, assignAccess ),
                                                   ifTrue:  Expression.Constant ( ExpressionState.Awaiting ),
                                                   ifFalse: context.SetResult  ( accessed ) );

        var member    = GetAccessedMember ( access );
        var schedule  = Expression.Condition ( test:    variables.Select    ( v => context.Schedule ( accessedId, v.Variable, member ) )
                                                                 .Aggregate ( Expression.OrElse ),
                                               ifTrue:  Expression.Constant ( ExpressionState.Scheduled ),
                                               ifFalse: waitForAccess );

        var waitFor   = Expression.Condition ( test:    variables.Select    ( v => context.Await ( context.GetId ( v.Variable ), v.Variable ) )
                                                                 .Aggregate ( Expression.Or ),
                                               ifTrue:  Expression.Constant ( ExpressionState.Awaiting ),
                                               ifFalse: schedule );

        var nullTest  = Expression.Condition ( test:    variables.Select    ( v => Expression.Equal ( v.Assign, Null ) )
                                                                 .Aggregate ( Expression.OrElse ),
                                               ifTrue:  Expression.Constant ( ExpressionState.Fallback ),
                                               ifFalse: waitFor );

        if ( propagatedInstance.NodeType == ExpressionType.Block )
        {
            var block = (BlockExpression) new ExpressionReplacer ( ReplaceResult ).Visit ( propagatedInstance );
        
            Expression ReplaceResult ( Expression node )
            {
                if ( node.NodeType == ExpressionType.Conditional )
                {
                    // TODO: Clean up this test
                    var ifFalse = ( (ConditionalExpression) node ).IfFalse;
                    if ( ifFalse.NodeType == ExpressionType.Call && ( (MethodCallExpression) ifFalse ).Method.DeclaringType.IsGenericType && ( (MethodCallExpression) ifFalse ).Method.DeclaringType.GetGenericTypeDefinition ( ) == typeof ( IExpressionStateMachine < > ) )
                        return nullTest;
                }
        
                return node;
            }
        
            if ( block.Expressions.Count > 1 )
                throw new InvalidOperationException ( "Invalid propagated block" );
        
            return Expression.Block ( type:        nullTest.Type,
                                      variables:   block.Variables.Append ( accessed ),
                                      expressions: block.Expressions );
        }

        return Expression.Block ( type:        nullTest.Type,
                                  variables:   new [ ] { variable, accessed },
                                  expressions: new Expression [ ]
                                  {
                                      nullTest
                                  } );
    }

    private static Expression MakeMultipleSchedulables ( Expression access, List < Expression > instance, List < Expression > propagatedInstance, ExpressionStateMachineBuilderContext context )
    {
        var variables    = new List < (ParameterExpression Variable, BinaryExpression Assign) > ( );
        var schedules    = new List < Expression > ( );
        var replacements = new Dictionary < Expression, ParameterExpression > ( instance.Count );

        for ( var index = 0; index < instance.Count; index++ )
        {
            var firstVariableIndex = variables.Count;

            propagatedInstance [ index ].GetVariablesAndAssigns ( variables, schedules, context );

            replacements [ instance [ index ] ] = variables [ firstVariableIndex ].Variable;
        }

        if ( schedules.Count == 1 )
            schedules.Clear ( );

        access = new ExpressionReplacer ( Replace ).Visit ( access ).MakeNullable ( );

        Expression Replace ( Expression node )
        {
            if ( replacements.TryGetValue ( node, out var variable ) )
                return node.IsNullableStruct ( ) ? variable : variable.RemoveNullable ( );

            return node;
        }

        var accessed     = access.GenerateVariable ( context.Variables.Keys );
        var accessedId   = context.GetId ( accessed );
        var assignAccess = Expression.Assign   ( accessed, context.Assign ( accessedId, accessed, access ) );

        var waitForAccess = Expression.Condition ( test:    context.Await  ( accessedId, assignAccess ),
                                                   ifTrue:  Expression.Constant ( ExpressionState.Awaiting ),
                                                   ifFalse: context.SetResult  ( accessed ) );

        var member    = GetAccessedMember ( access );

        var schedule  = Expression.Condition ( test:    variables.Select    ( v => context.Schedule ( accessedId, v.Variable, member ) )
                                                                 .Aggregate ( Expression.OrElse ),
                                               ifTrue:  Expression.Constant ( ExpressionState.Scheduled ),
                                               ifFalse: waitForAccess );

        var waitFor   = Expression.Condition ( test:    variables.Select    ( v => context.Await ( context.GetId ( v.Variable ), v.Variable ) )
                                                                 .Aggregate ( Expression.Or ),
                                               ifTrue:  Expression.Constant ( ExpressionState.Awaiting ),
                                               ifFalse: schedule );

        var nullTest  = Expression.Condition ( test:    variables.Select    ( v => Expression.Equal ( v.Assign, Null ) )
                                                                 .Aggregate ( Expression.OrElse ),
                                               ifTrue:  Expression.Constant ( ExpressionState.Fallback ),
                                               ifFalse: waitFor );

        if ( propagatedInstance.Any ( p => p.NodeType == ExpressionType.Block ) )
        {
            var allVariables = new List < ParameterExpression > ( );
            var propagated   = (BlockExpression?) null;

            for ( var index = propagatedInstance.Count - 1; index >= 0; index-- )
            {
                if ( propagatedInstance [ index ].NodeType != ExpressionType.Block )
                {
                    allVariables.Insert ( 0, replacements [ instance [ index ] ] );
                    continue;
                }

                propagated = (BlockExpression) propagatedInstance [ index ];
                propagated = (BlockExpression) new ExpressionReplacer ( ReplaceResult ).Visit ( propagated );

                allVariables.InsertRange ( 0, propagated.Variables );

                Expression ReplaceResult ( Expression node )
                {
                    if ( schedules.Contains ( node ) )
                    {
                        var mergedSchedules = schedules.Aggregate ( Expression.OrElse );

                        schedules.Clear ( );

                        return mergedSchedules;
                    }

                    if ( node.NodeType == ExpressionType.Conditional )
                    {
                        // TODO: Clean up this test
                        var ifFalse = ( (ConditionalExpression) node ).IfFalse;
                        if ( ifFalse.NodeType == ExpressionType.Call && ( (MethodCallExpression) ifFalse ).Method.DeclaringType.IsGenericType && ( (MethodCallExpression) ifFalse ).Method.DeclaringType.GetGenericTypeDefinition ( ) == typeof ( IExpressionStateMachine < > ) )
                            return nullTest;
                    }

                    return node;
                }

                nullTest = (ConditionalExpression) propagated.Expressions [ 0 ];
            }

            allVariables.Add ( accessed );

            return Expression.Block ( type:        nullTest.Type,
                                      variables:   allVariables,
                                      expressions: propagated.Expressions );
        }

        return Expression.Block ( type:        nullTest.Type,
                                  variables:   variables.Select ( v => v.Variable ).Append ( accessed ),
                                  expressions: nullTest );
    }

    private static void GetVariablesAndAssigns ( this Expression propagatedInstance, List < (ParameterExpression, BinaryExpression) > variables, List < Expression >? schedules, ExpressionStateMachineBuilderContext context )
    {
        if ( propagatedInstance.NodeType != ExpressionType.Block )
        {
            var variable = propagatedInstance.GenerateVariable ( context.Variables.Keys );
            var assign   = Expression.Assign ( variable, context.Assign ( context.GetId ( variable ), variable, propagatedInstance ) );

            variables.Add ( (variable, assign) );

            return;
        }

        var resultFound = false;

        new ExpressionReplacer ( ExtractAssigns ).Visit ( propagatedInstance );

        Expression ExtractAssigns ( Expression node )
        {
            if ( schedules != null && node.NodeType == ExpressionType.Conditional )
            {
                // TODO: Clean up this test
                var ifFalse = ( (ConditionalExpression) node ).IfFalse;
                if ( ifFalse.NodeType == ExpressionType.Conditional )
                {
                    ifFalse = ( (ConditionalExpression) ifFalse ).IfFalse;
                    if ( ifFalse.NodeType == ExpressionType.Call && ( (MethodCallExpression) ifFalse ).Method.DeclaringType.IsGenericType && ( (MethodCallExpression) ifFalse ).Method.DeclaringType.GetGenericTypeDefinition ( ) == typeof ( IExpressionStateMachine < > ) )
                        schedules.Add ( ( (ConditionalExpression) node ).Test );
                }
            }

            if ( node.NodeType == ExpressionType.Conditional )
            {
                // TODO: Clean up this test
                var ifFalse = ( (ConditionalExpression) node ).IfFalse;
                if ( ifFalse.NodeType == ExpressionType.Call && ( (MethodCallExpression) ifFalse ).Method.DeclaringType.IsGenericType && ( (MethodCallExpression) ifFalse ).Method.DeclaringType.GetGenericTypeDefinition ( ) == typeof ( IExpressionStateMachine < > ) )
                    resultFound = true;
            }

            if ( resultFound && node.NodeType == ExpressionType.Assign )
            {
                var variable = (ParameterExpression) ( (BinaryExpression) node ).Left;
                var assign   = (BinaryExpression) node;

                variables.Add ( (variable, assign) );
            }

            return node;
        }

        if ( ! resultFound )
            throw new InvalidOperationException ( "Result expression not found" );
    }

    private static MemberInfo GetAccessedMember ( Expression access )
    {
        access = access.Unconvert ( );

        return access.NodeType == ExpressionType.MemberAccess ? ( (MemberExpression)     access ).Member :
               access.NodeType == ExpressionType.Call         ? ( (MethodCallExpression) access ).Method :
               access is BinaryExpression binary              ? binary.Method :
               throw new ArgumentException ( "Unknown access type", nameof ( access ) );
    }
}