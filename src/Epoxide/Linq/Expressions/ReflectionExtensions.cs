namespace Epoxide.Linq.Expressions;

public static class ReflectionExtensions
{
    public static Type [ ]? GetGenericInterfaceArguments ( this Type type, Type genericInterface )
    {
        foreach ( var @interface in type.GetInterfaces ( ) )
            if ( @interface.IsGenericType && @interface.GetGenericTypeDefinition ( ) == genericInterface )
                return @interface.GetGenericArguments ( );

        return null;
    }

    public static void SetValue ( this MemberInfo member, object target, object? value )
    {
        if      ( member is PropertyInfo property ) property.SetValue ( target, value, null );
        else if ( member is FieldInfo    field    ) field   .SetValue ( target, value );
        else                                        throw new InvalidOperationException ( "Cannot set value of " + member.MemberType + " " + member.Name );
    }
}