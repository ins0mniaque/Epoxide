namespace Epoxide.Linq.Expressions;

// TODO: Emit code to set value
public static class DynamicTypeAccessor
{
    public static Action < object, object? > CompileSetter ( this MemberInfo member )
    {
        return CompileSetter ( member, typeof ( object ) );
    }

    public static Action < object, object? > CompileSetter ( this MemberInfo member, Type valueType )
    {
        return (target, value) => Write ( target, member, value );
    }

    public static bool CanSetFrom ( this MemberInfo member, Type valueType )
    {
        if      ( member is PropertyInfo property ) return CanCast ( valueType, property.PropertyType );
        else if ( member is FieldInfo    field    ) return CanCast ( valueType, field   .FieldType    );
        else                                        return false;
    }

    private static void Write ( object target, MemberInfo member, object? value )
    {
        if ( member is PropertyInfo property )
        {
            value = Cast ( value, property.PropertyType );

            if ( value == null && property.PropertyType.IsValueType && Nullable.GetUnderlyingType ( property.PropertyType ) == null )
                throw new InvalidCastException ( );

            property.SetValue ( target, value, null );
        }
        else if ( member is FieldInfo field )
        {
            value = Cast ( value, field.FieldType );

            if ( value == null && field.FieldType.IsValueType && Nullable.GetUnderlyingType ( field.FieldType ) == null )
                throw new InvalidCastException ( );

            field.SetValue ( target, value );
        }
        else
            throw new InvalidOperationException ( "Cannot set value of " + member.MemberType + " " + member.Name );
    }

    private static object? Cast ( object? source, Type destType )
    {
        if ( source == null )
            return null;

        var srcType = source.GetType ( );
        if ( destType.IsAssignableFrom ( srcType ) )
            return source;

        // TODO: Only allow valid numeric casts 
        if ( ( srcType .IsPrimitive || srcType .IsEnum ) &&
             ( destType.IsPrimitive || destType.IsEnum ) )
            return destType.IsEnum ? Enum.ToObject ( destType, source ) : source;

        var types = new [ ] { srcType };
        var cast  = destType.GetMethod ( "op_Implicit", types ) ??
                    destType.GetMethod ( "op_Explicit", types ) ??
                    srcType .GetMethod ( "op_Implicit", types ) ??
                    srcType .GetMethod ( "op_Explicit", types );

        if ( cast != null && cast.ReturnType == destType )
            return cast.Invoke ( null, new [ ] { source } );

        if ( destType == typeof ( string ) )
            return source.ToString ( );

        throw new InvalidCastException ( $"Invalid cast from '{ DebugView.Display ( srcType ) }' to '{ destType }'." );
    }

    private static bool CanCast ( Type srcType, Type destType )
    {
        if ( destType == typeof ( string ) )
            return true;

        if ( destType.IsAssignableFrom ( srcType ) )
            return true;

        // TODO: Only allow valid numeric casts 
        if ( ( srcType .IsPrimitive || srcType .IsEnum ) &&
             ( destType.IsPrimitive || destType.IsEnum ) )
            return true;

        var types = new [ ] { srcType };
        var cast  = destType.GetMethod ( "op_Implicit", types ) ??
                    destType.GetMethod ( "op_Explicit", types ) ??
                    srcType .GetMethod ( "op_Implicit", types ) ??
                    srcType .GetMethod ( "op_Explicit", types );

        return cast != null && cast.ReturnType == destType;
    }
}