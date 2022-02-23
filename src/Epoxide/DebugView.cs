using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

[ assembly: DebuggerDisplay ( Epoxide.DebugView.DebuggerDisplay, Target = typeof ( BinaryExpression           ) ) ]
[ assembly: DebuggerDisplay ( Epoxide.DebugView.DebuggerDisplay, Target = typeof ( BlockExpression            ) ) ]
[ assembly: DebuggerDisplay ( Epoxide.DebugView.DebuggerDisplay, Target = typeof ( ConditionalExpression      ) ) ]
[ assembly: DebuggerDisplay ( Epoxide.DebugView.DebuggerDisplay, Target = typeof ( ConstantExpression         ) ) ]
[ assembly: DebuggerDisplay ( Epoxide.DebugView.DebuggerDisplay, Target = typeof ( DebugInfoExpression        ) ) ]
[ assembly: DebuggerDisplay ( Epoxide.DebugView.DebuggerDisplay, Target = typeof ( DefaultExpression          ) ) ]
[ assembly: DebuggerDisplay ( Epoxide.DebugView.DebuggerDisplay, Target = typeof ( DynamicExpression          ) ) ]
[ assembly: DebuggerDisplay ( Epoxide.DebugView.DebuggerDisplay, Target = typeof ( GotoExpression             ) ) ]
[ assembly: DebuggerDisplay ( Epoxide.DebugView.DebuggerDisplay, Target = typeof ( IndexExpression            ) ) ]
[ assembly: DebuggerDisplay ( Epoxide.DebugView.DebuggerDisplay, Target = typeof ( InvocationExpression       ) ) ]
[ assembly: DebuggerDisplay ( Epoxide.DebugView.DebuggerDisplay, Target = typeof ( LabelExpression            ) ) ]
[ assembly: DebuggerDisplay ( Epoxide.DebugView.DebuggerDisplay, Target = typeof ( LambdaExpression           ) ) ]
[ assembly: DebuggerDisplay ( Epoxide.DebugView.DebuggerDisplay, Target = typeof ( ListInitExpression         ) ) ]
[ assembly: DebuggerDisplay ( Epoxide.DebugView.DebuggerDisplay, Target = typeof ( LoopExpression             ) ) ]
[ assembly: DebuggerDisplay ( Epoxide.DebugView.DebuggerDisplay, Target = typeof ( MemberExpression           ) ) ]
[ assembly: DebuggerDisplay ( Epoxide.DebugView.DebuggerDisplay, Target = typeof ( MemberInitExpression       ) ) ]
[ assembly: DebuggerDisplay ( Epoxide.DebugView.DebuggerDisplay, Target = typeof ( MethodCallExpression       ) ) ]
[ assembly: DebuggerDisplay ( Epoxide.DebugView.DebuggerDisplay, Target = typeof ( NewArrayExpression         ) ) ]
[ assembly: DebuggerDisplay ( Epoxide.DebugView.DebuggerDisplay, Target = typeof ( NewExpression              ) ) ]
[ assembly: DebuggerDisplay ( Epoxide.DebugView.DebuggerDisplay, Target = typeof ( ParameterExpression        ) ) ]
[ assembly: DebuggerDisplay ( Epoxide.DebugView.DebuggerDisplay, Target = typeof ( RuntimeVariablesExpression ) ) ]
[ assembly: DebuggerDisplay ( Epoxide.DebugView.DebuggerDisplay, Target = typeof ( SwitchExpression           ) ) ]
[ assembly: DebuggerDisplay ( Epoxide.DebugView.DebuggerDisplay, Target = typeof ( TryExpression              ) ) ]
[ assembly: DebuggerDisplay ( Epoxide.DebugView.DebuggerDisplay, Target = typeof ( TypeBinaryExpression       ) ) ]
[ assembly: DebuggerDisplay ( Epoxide.DebugView.DebuggerDisplay, Target = typeof ( UnaryExpression            ) ) ]

[ assembly: DebuggerTypeProxy ( typeof ( Epoxide.ExpressionDebugView ), Target = typeof ( BinaryExpression           ) ) ]
[ assembly: DebuggerTypeProxy ( typeof ( Epoxide.ExpressionDebugView ), Target = typeof ( BlockExpression            ) ) ]
[ assembly: DebuggerTypeProxy ( typeof ( Epoxide.ExpressionDebugView ), Target = typeof ( ConditionalExpression      ) ) ]
[ assembly: DebuggerTypeProxy ( typeof ( Epoxide.ExpressionDebugView ), Target = typeof ( ConstantExpression         ) ) ]
[ assembly: DebuggerTypeProxy ( typeof ( Epoxide.ExpressionDebugView ), Target = typeof ( DebugInfoExpression        ) ) ]
[ assembly: DebuggerTypeProxy ( typeof ( Epoxide.ExpressionDebugView ), Target = typeof ( DefaultExpression          ) ) ]
[ assembly: DebuggerTypeProxy ( typeof ( Epoxide.ExpressionDebugView ), Target = typeof ( DynamicExpression          ) ) ]
[ assembly: DebuggerTypeProxy ( typeof ( Epoxide.ExpressionDebugView ), Target = typeof ( GotoExpression             ) ) ]
[ assembly: DebuggerTypeProxy ( typeof ( Epoxide.ExpressionDebugView ), Target = typeof ( IndexExpression            ) ) ]
[ assembly: DebuggerTypeProxy ( typeof ( Epoxide.ExpressionDebugView ), Target = typeof ( InvocationExpression       ) ) ]
[ assembly: DebuggerTypeProxy ( typeof ( Epoxide.ExpressionDebugView ), Target = typeof ( LabelExpression            ) ) ]
[ assembly: DebuggerTypeProxy ( typeof ( Epoxide.ExpressionDebugView ), Target = typeof ( LambdaExpression           ) ) ]
[ assembly: DebuggerTypeProxy ( typeof ( Epoxide.ExpressionDebugView ), Target = typeof ( ListInitExpression         ) ) ]
[ assembly: DebuggerTypeProxy ( typeof ( Epoxide.ExpressionDebugView ), Target = typeof ( LoopExpression             ) ) ]
[ assembly: DebuggerTypeProxy ( typeof ( Epoxide.ExpressionDebugView ), Target = typeof ( MemberExpression           ) ) ]
[ assembly: DebuggerTypeProxy ( typeof ( Epoxide.ExpressionDebugView ), Target = typeof ( MemberInitExpression       ) ) ]
[ assembly: DebuggerTypeProxy ( typeof ( Epoxide.ExpressionDebugView ), Target = typeof ( MethodCallExpression       ) ) ]
[ assembly: DebuggerTypeProxy ( typeof ( Epoxide.ExpressionDebugView ), Target = typeof ( NewArrayExpression         ) ) ]
[ assembly: DebuggerTypeProxy ( typeof ( Epoxide.ExpressionDebugView ), Target = typeof ( NewExpression              ) ) ]
[ assembly: DebuggerTypeProxy ( typeof ( Epoxide.ExpressionDebugView ), Target = typeof ( ParameterExpression        ) ) ]
[ assembly: DebuggerTypeProxy ( typeof ( Epoxide.ExpressionDebugView ), Target = typeof ( RuntimeVariablesExpression ) ) ]
[ assembly: DebuggerTypeProxy ( typeof ( Epoxide.ExpressionDebugView ), Target = typeof ( SwitchExpression           ) ) ]
[ assembly: DebuggerTypeProxy ( typeof ( Epoxide.ExpressionDebugView ), Target = typeof ( TryExpression              ) ) ]
[ assembly: DebuggerTypeProxy ( typeof ( Epoxide.ExpressionDebugView ), Target = typeof ( TypeBinaryExpression       ) ) ]
[ assembly: DebuggerTypeProxy ( typeof ( Epoxide.ExpressionDebugView ), Target = typeof ( UnaryExpression            ) ) ]

namespace Epoxide;

using static DebuggerBrowsableState;
using static DebugView;

public class ExpressionDebugView
{
    public ExpressionDebugView ( Expression expression )
    {
        Expression = expression;
    }

    public string DebugView => Visualize ( Expression );

    [ DebuggerBrowsable ( Never ) ]
    public Expression Expression { get; }

    [ DebuggerBrowsable ( RootHidden ) ]
    public object [ ] Properties => GetProperties ( ).ToArray ( );

    private IEnumerable < Entry > GetProperties ( ) => Expression switch
    {
        LambdaExpression     lambda   => GetProperties ( lambda     ),
        MethodCallExpression method   => GetProperties ( method     ),
        MemberExpression     member   => GetProperties ( member     ),
        BinaryExpression     binary   => GetProperties ( binary     ),
        UnaryExpression      unary    => GetProperties ( unary      ),
        ConstantExpression   constant => GetProperties ( constant   ),
        _                             => GetProperties ( Expression )
    };

    private static IEnumerable < Entry > GetProperties ( LambdaExpression lambda )
    {
        yield return new Entry ( "NodeType", lambda.NodeType );

        var index = 0;
        foreach ( var parameter in lambda.Parameters )
            yield return new Entry ( "Parameter " + index++, parameter, Display ( (MemberInfo) parameter.Type ) + " " + Display ( parameter ) );

        yield return new Entry ( "Body",       lambda.Body );
        yield return new Entry ( "ReturnType", Display ( lambda.ReturnType ) );
    }

    private static IEnumerable < Entry > GetProperties ( MethodCallExpression method )
    {
        yield return new Entry ( "NodeType", method.NodeType );
        yield return new Entry ( "Object",   method.Object );
        yield return new Entry ( "Method",   Display ( method.Method ) );

        var index = 0;
        foreach ( var argument in method.Arguments )
            yield return new Entry ( "Argument " + index++, argument );

        yield return new Entry ( "ReturnType", Display ( method.Type ) );
    }

    private static IEnumerable < Entry > GetProperties ( MemberExpression member )
    {
        yield return new Entry ( "NodeType", member.NodeType   );
        yield return new Entry ( "Object",   member.Expression );

        yield return new Entry ( member.Member.MemberType.ToString ( ), member.Member, Display ( member.Member ) );

        yield return new Entry ( "ReturnType", Display ( member.Type ) );
    }

    private static IEnumerable < Entry > GetProperties ( BinaryExpression binary )
    {
        yield return new Entry ( "NodeType",   binary.NodeType );
        yield return new Entry ( "Left",       binary.Left );
        yield return new Entry ( "Right",      binary.Right );
        yield return new Entry ( "ReturnType", Display ( binary.Type ) );
    }

    private static IEnumerable < Entry > GetProperties ( UnaryExpression unary )
    {
        yield return new Entry ( "NodeType",   unary.NodeType );
        yield return new Entry ( "Operand",    unary.Operand );
        yield return new Entry ( "ReturnType", Display ( unary.Type ) );
    }

    private static IEnumerable < Entry > GetProperties ( ConstantExpression constant )
    {
        yield return new Entry ( "NodeType",   constant.NodeType );
        yield return new Entry ( "Value",      constant.Value );
        yield return new Entry ( "ReturnType", Display ( constant.Type ) );
    }

    private static IEnumerable < Entry > GetProperties ( Expression expression )
    {
        yield return new Entry ( "NodeType", expression.NodeType );

        foreach ( var property in expression.GetType ( ).GetProperties ( ) )
        {
            if ( property.PropertyType == typeof ( Expression ) )
                yield return new Entry ( property.Name, property.GetValue ( expression, null ) );

            if ( property.PropertyType == typeof ( ReadOnlyCollection < Expression > ) )
            {
                var index = 0;
                foreach ( var subExpression in (ReadOnlyCollection < Expression >) property.GetValue ( expression, null ) )
                    yield return new Entry ( property.Name + " " + index++, subExpression );
            }
        }

        yield return new Entry ( "ReturnType", Display ( (MemberInfo) expression.Type ) );
    }

    [ DebuggerDisplay ( "{Value,nq}", Name = "{Name,nq}" ) ]
    private class Entry
    {
        public Entry ( string name, object value, object? properties = null )
        {
            Name       = name;
            Value      = value;
            Properties = properties ?? value;
        }

        [ DebuggerBrowsable ( Never ) ]
        public string Name { get; }

        [ DebuggerBrowsable ( Never ) ]
        public object Value { get; }

        [ DebuggerBrowsable ( RootHidden ) ]
        public object Properties { get; }
    }
}

public static class DebugView
{
    public const string DebuggerDisplay       = "{Epoxide.DebugView.Display(Epoxide.DebugView.Display(this)),nq}";
    public const int    DebuggerDisplayLength = 120;

    public static string Display ( string display )
    {
        if ( display.Length > DebuggerDisplayLength )
            display = display.Substring ( 0, DebuggerDisplayLength ) + "...";

        return display;
    }

    public static string Display ( Type type )
    {
        return Nullable.GetUnderlyingType ( type ) is { } valueType ? Display ( valueType ) + "?" :
               type == typeof ( int )     ? "int" :
               type == typeof ( short )   ? "short":
               type == typeof ( byte )    ? "byte":
               type == typeof ( bool )    ? "bool":
               type == typeof ( long )    ? "long":
               type == typeof ( float )   ? "float":
               type == typeof ( double )  ? "double":
               type == typeof ( decimal ) ? "decimal":
               type == typeof ( string )  ? "string":
               type.IsGenericType         ? type.Name.Split ( '`' ) [ 0 ] + "<" + string.Join ( ", ", type.GetGenericArguments ( ).Select ( Display ) ) + ">" :
                                            type.Name;
    }

    public static string Display ( MemberInfo member ) => member switch
    {
        ConstructorInfo ctor     => Display ( ctor    .DeclaringType ) + "(" + string.Join ( ", ", ctor.GetParameters ( ).Select ( Display ) ) + ")",
        FieldInfo       field    => Display ( field   .FieldType     ) + " " + Display ( field   .DeclaringType ) + "." + field   .Name,
        PropertyInfo    property => Display ( property.PropertyType  ) + " " + Display ( property.DeclaringType ) + "." + property.Name,
        MethodInfo      method   => Display ( method  .ReturnType    ) + " " + Display ( method  .DeclaringType ) + "." + method  .Name +
                                    ( method.IsGenericMethod ? "<" + string.Join ( ", ", method.GetGenericArguments ( ).Select ( Display ) ) + ">" : "" ) +
                                    "(" + string.Join ( ", ", method.GetParameters ( ).Select ( Display ) ) + ")",
        _                        => member.ToString ( )
    };

    public static string Display ( ParameterInfo parameter )
    {
        return Display ( parameter.ParameterType ) + " " + parameter.Name;
    }

    public static string Display ( Expression expression )
    {
        return ReadableExpressions.IsAvailable ? ReadableExpressions.Display ( expression ) :
                                                 expression.ToString ( );
    }

    public static string Visualize ( Expression expression )
    {
        return ReadableExpressions.IsAvailable ? ReadableExpressions.Visualize ( expression ) :
               DebugViewWriter    .IsAvailable ? DebugViewWriter    .Visualize ( expression ) :
                                                 DebugViewWriter    .Warning;
    }

    private static class ReadableExpressions
    {
        private static readonly MethodInfo? toReadableString;

        static ReadableExpressions ( )
        {
            var assembly = GetOrLoadAssembly ( "AgileObjects.ReadableExpressions" );

            var expression = assembly?.GetType ( "AgileObjects.ReadableExpressions.ExpressionExtensions" );
            var settings   = assembly?.GetType ( "AgileObjects.ReadableExpressions.ITranslationSettings" );
            var configure  = settings != null ? typeof ( Func < , > ).MakeGenericType ( settings, settings ) : null;

            toReadableString = expression?.GetMethod ( "ToReadableString", new [ ] { typeof ( Expression ), configure } );
        }

        public static bool IsAvailable => toReadableString != null;

        public static string Display ( Expression expression )
        {
            return Regex.Replace ( (string) toReadableString.Invoke ( null, new object? [ ] { expression, null } ), @"\r?\n\s*", "" );
        }

        public static string Visualize ( Expression expression )
        {
            return (string) toReadableString.Invoke ( null, new object? [ ] { expression, null } );
        }

        private static Assembly? GetOrLoadAssembly ( string assemblyName )
        {
            try
            {
                return AppDomain.CurrentDomain.GetAssemblies ( ).FirstOrDefault ( assembly => assembly.FullName == assemblyName ) ??
                       Assembly.Load ( assemblyName );
            }
            catch ( FileNotFoundException )
            {
                return null;
            }
        }
    }

    private static class DebugViewWriter
    {
        private static readonly MethodInfo? writeTo;

        static DebugViewWriter ( )
        {
            var assembly        = typeof ( Expression ).Assembly;
            var debugViewWriter = assembly.GetType ( "System.Linq.Expressions.DebugViewWriter" ) ??
                                  assembly.GetType ( "Microsoft.Scripting.Ast.DebugViewWriter" );

            writeTo = debugViewWriter?.GetMethod ( "WriteTo",
                                                   BindingFlags.NonPublic | BindingFlags.Static,
                                                   null,
                                                   new [ ] { typeof ( Expression ), typeof ( TextWriter ) },
                                                   null );
        }

        public static bool IsAvailable => writeTo != null;

        public static string? warning;
        public static string  Warning => warning ??= NotAvailable ( );

        public static string Visualize ( Expression expression )
        {
            using var writer = new StringWriter ( );

            Warn ( writer, "This was generated by the default debug view generator." );

            writer.WriteLine ( );

            writeTo.Invoke ( null, new object [ ] { expression, writer } );

            return writer.ToString ( );
        }

        private static string NotAvailable ( )
        {
            using var writer = new StringWriter ( );

            Warn ( writer, "The default debug view generator is not available." );

            return writer.ToString ( );
        }

        private static void Warn ( StringWriter writer, string message )
        {
            writer.WriteLine ( "// WARNING: " + message );
            writer.WriteLine ( "//" );
            writer.WriteLine ( "// To use AgileObjects.ReadableExpressions as debug view, include this in your project file:" );
            writer.WriteLine ( "//" );
            writer.WriteLine ( "//  <ItemGroup Condition=\"'$(Configuration)' == 'Debug'\">" );
            writer.WriteLine ( "//    <PackageReference Include=\"AgileObjects.ReadableExpressions\" Version=\"3.2.0\" />" );
            writer.WriteLine ( "//  </ItemGroup>" );
            writer.WriteLine ( "//" );
            writer.WriteLine ( "// For more information, see https://github.com/ins0mniaque/Epoxide and https://github.com/agileobjects/ReadableExpressions." );
        }
    }
}