/// <summary>
/// Schema ids that stay unique across the whole document: generic arguments are folded into the name (as
/// Swashbuckle's default does) and controller-nested DTOs are qualified by their declaring type, so a nested
/// DTO sharing a name with one in Jiten.Api.Dtos no longer aborts generation of the entire document.
/// </summary>
public static class SwaggerSchemaId
{
    public static string For(Type type)
    {
        var name = type.Name;

        if (type.IsGenericType)
        {
            var tick = name.IndexOf('`');
            if (tick >= 0)
                name = name[..tick];
            name += string.Concat(type.GetGenericArguments().Select(For));
        }

        return type.IsNested && type.DeclaringType != null ? type.DeclaringType.Name + name : name;
    }
}
