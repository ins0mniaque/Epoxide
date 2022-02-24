namespace Epoxide.Linq.Expressions;

public static class ReflectionExtensions
{
    public static Type [ ]? GetGenericInterfaceArguments ( this Type type, Type genericInterface )
    {
        if ( type.IsInterface && type.GetGenericTypeDefinition ( ) == genericInterface )
            return type.GetGenericArguments ( );

        foreach ( var @interface in type.GetInterfaces ( ) )
            if ( @interface.IsGenericType && @interface.GetGenericTypeDefinition ( ) == genericInterface )
                return @interface.GetGenericArguments ( );

        return null;
    }
}