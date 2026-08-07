using System.Reflection;

// Prints one fully-qualified type name per line for every publicly-visible type across the six
// shipped assemblies, using "." between an enclosing type and a nested one -- the same convention
// docfx's generated file names use -- so build/Test-ApiReference.ps1 can diff this list against the
// API reference's own output without re-deriving visibility rules itself.

string[] assemblyNames =
[
    "SubZeroDev.Platform.Abstractions",
    "SubZeroDev.Platform.Core",
    "SubZeroDev.Platform.Hosting",
    "SubZeroDev.Platform.Observability",
    "SubZeroDev.Platform.Persistence",
    "SubZeroDev.Platform.Testing",
];

foreach (var assemblyName in assemblyNames)
{
    var assembly = Assembly.Load(assemblyName);

    foreach (var type in assembly.GetTypes())
    {
        if (!IsPubliclyVisible(type)) continue;
        if (type.IsSpecialName) continue;
        if (type.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is not null) continue;

        Console.WriteLine(type.FullName!.Replace('+', '.'));
    }
}

return 0;

static bool IsPubliclyVisible(Type type)
{
    if (type.IsNested)
    {
        return (type.IsNestedPublic || type.IsNestedFamily || type.IsNestedFamORAssem)
            && IsPubliclyVisible(type.DeclaringType!);
    }

    return type.IsPublic;
}
