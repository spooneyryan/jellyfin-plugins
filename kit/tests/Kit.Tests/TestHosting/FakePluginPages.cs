using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;

namespace Jellyfin.Plugin.Kit.Tests.TestHosting;

/// <summary>
/// Fake Plugin Pages assemblies for the bridge tests ("PluginPagesBridge with fake 3.x and 2.4.x assemblies loaded into
/// a collectible ALC"): emitted with <see cref="PersistedAssemblyBuilder"/> and loaded into their own collectible
/// <see cref="AssemblyLoadContext"/>, with the type names and member shapes of the real ones:
/// <list type="bullet">
/// <item><description>2.4.x: <c>Jellyfin.Plugin.PluginPages.Library.PluginPage { string Id, Url, DisplayText, Icon }</c>,
/// <c>…Library.IPluginPagesManager.RegisterPluginPage(PluginPage)</c> and a manager class whose public field
/// <c>Pages</c> (a <c>List&lt;object&gt;</c>) records every registered page;</description></item>
/// <item><description>3.x: <c>Jellyfin.Plugin.PluginPages.PluginInterface.RegisterPage(JObject)</c>, where the fake
/// <c>JObject</c> has a static <c>Parse(string)</c> and the static field <c>PluginInterface.Last</c> keeps the last JSON
/// text registered.</description></item>
/// </list>
/// </summary>
internal sealed class FakePluginPages : IDisposable
{
    public const string ManagerTypeName = "Jellyfin.Plugin.PluginPages.Manager.PluginPagesManager";
    public const string InterfaceTypeName = "Jellyfin.Plugin.PluginPages.Library.IPluginPagesManager";

    private readonly AssemblyLoadContext _context;

    private FakePluginPages(AssemblyLoadContext context, Assembly assembly)
    {
        _context = context;
        Assembly = assembly;
    }

    public Assembly Assembly { get; }

    /// <summary>
    /// Plugin Pages 2.4.x, optionally with a registration method that throws (as for an invalid page), or with the
    /// PluginPage type renamed (an unexpected interface).
    /// </summary>
    public static FakePluginPages Version24(string assemblyName = "Jellyfin.Plugin.PluginPages", bool throwing = false, string pageTypeName = "Jellyfin.Plugin.PluginPages.Library.PluginPage", bool getPages = true)
    {
        var builder = new PersistedAssemblyBuilder(new AssemblyName(assemblyName) { Version = new Version(2, 4, 7, 0) }, typeof(object).Assembly);
        var module = builder.DefineDynamicModule(assemblyName);

        var page = module.DefineType(pageTypeName, TypeAttributes.Public | TypeAttributes.Class);
        page.DefineDefaultConstructor(MethodAttributes.Public);
        foreach (var name in new[] { "Id", "Url", "DisplayText", "Icon" })
        {
            AddStringProperty(page, name);
        }

        var manager = module.DefineType(InterfaceTypeName, TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        var abstractRegister = manager.DefineMethod(
            "RegisterPluginPage",
            MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
            typeof(void),
            [page]);

        var implementation = module.DefineType(ManagerTypeName, TypeAttributes.Public | TypeAttributes.Class, typeof(object), [manager]);
        var pages = implementation.DefineField("Pages", typeof(List<object>), FieldAttributes.Public);
        var constructor = implementation.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
        var il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Newobj, typeof(List<object>).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stfld, pages);
        il.Emit(OpCodes.Ret);

        var register = implementation.DefineMethod(
            "RegisterPluginPage",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
            typeof(void),
            [page]);
        il = register.GetILGenerator();
        if (throwing)
        {
            il.Emit(OpCodes.Ldstr, "Plugin Pages refused the page.");
            il.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor([typeof(string)])!);
            il.Emit(OpCodes.Throw);
        }
        else
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, pages);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, typeof(List<object>).GetMethod(nameof(List<object>.Add))!);
            il.Emit(OpCodes.Ret);
        }

        implementation.DefineMethodOverride(register, abstractRegister);

        // 2.4.x's GetPages(): the manager's own list (the Jellyfin Enhanced checklist reads it); left out to
        // fake an interface the kit doesn't know.
        if (getPages)
        {
            var abstractGetPages = manager.DefineMethod(
                "GetPages",
                MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                typeof(List<object>),
                Type.EmptyTypes);
            var getPagesMethod = implementation.DefineMethod(
                "GetPages",
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                typeof(List<object>),
                Type.EmptyTypes);
            il = getPagesMethod.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, pages);
            il.Emit(OpCodes.Ret);
            implementation.DefineMethodOverride(getPagesMethod, abstractGetPages);
        }

        page.CreateType();
        manager.CreateType();
        implementation.CreateType();
        return Load(builder, assemblyName);
    }

    /// <summary>
    /// Plugin Pages 3.x: the static <c>PluginInterface.RegisterPage(JObject)</c>.
    /// </summary>
    public static FakePluginPages Version3()
    {
        const string Name = "Jellyfin.Plugin.PluginPages";
        var builder = new PersistedAssemblyBuilder(new AssemblyName(Name) { Version = new Version(3, 0, 1, 0) }, typeof(object).Assembly);
        var module = builder.DefineDynamicModule(Name);

        var json = module.DefineType("Jellyfin.Plugin.PluginPages.FakeJObject", TypeAttributes.Public | TypeAttributes.Class);
        var text = json.DefineField("Text", typeof(string), FieldAttributes.Public);
        var constructor = json.DefineDefaultConstructor(MethodAttributes.Public);
        var parse = json.DefineMethod("Parse", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, json, [typeof(string)]);
        var il = parse.GetILGenerator();
        il.Emit(OpCodes.Newobj, constructor);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stfld, text);
        il.Emit(OpCodes.Ret);

        var pluginInterface = module.DefineType(
            "Jellyfin.Plugin.PluginPages.PluginInterface",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var last = pluginInterface.DefineField("Last", typeof(string), FieldAttributes.Public | FieldAttributes.Static);
        var register = pluginInterface.DefineMethod("RegisterPage", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, typeof(void), [json]);
        il = register.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, text);
        il.Emit(OpCodes.Stsfld, last);
        il.Emit(OpCodes.Ret);

        json.CreateType();
        pluginInterface.CreateType();
        return Load(builder, Name);
    }

    /// <summary>
    /// Creates the 2.4.x manager, as Plugin Pages' DI registration would.
    /// </summary>
    public object CreateManager() => Activator.CreateInstance(Assembly.GetType(ManagerTypeName, throwOnError: true)!)!;

    public Type ManagerInterface => Assembly.GetType(InterfaceTypeName, throwOnError: true)!;

    /// <summary>
    /// The pages a 2.4.x manager recorded, as (Id, Url, DisplayText, Icon).
    /// </summary>
    public static IReadOnlyList<(string? Id, string? Url, string? DisplayText, string? Icon)> Registered(object manager)
    {
        var list = (List<object>)manager.GetType().GetField("Pages")!.GetValue(manager)!;
        return list.ConvertAll(p => (Read(p, "Id"), Read(p, "Url"), Read(p, "DisplayText"), Read(p, "Icon")));
    }

    /// <summary>
    /// The JSON text the 3.x fake received last.
    /// </summary>
    public string? LastRegisteredJson => (string?)Assembly.GetType("Jellyfin.Plugin.PluginPages.PluginInterface", throwOnError: true)!.GetField("Last")!.GetValue(null);

    public void Dispose() => _context.Unload();

    private static string? Read(object page, string name) => (string?)page.GetType().GetProperty(name)!.GetValue(page);

    private static void AddStringProperty(TypeBuilder type, string name)
    {
        var field = type.DefineField("_" + name, typeof(string), FieldAttributes.Private);
        const MethodAttributes Accessor = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig;
        var getter = type.DefineMethod("get_" + name, Accessor, typeof(string), Type.EmptyTypes);
        var il = getter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Ret);
        var setter = type.DefineMethod("set_" + name, Accessor, typeof(void), [typeof(string)]);
        il = setter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, field);
        il.Emit(OpCodes.Ret);
        var property = type.DefineProperty(name, PropertyAttributes.None, typeof(string), null);
        property.SetGetMethod(getter);
        property.SetSetMethod(setter);
    }

    private static FakePluginPages Load(PersistedAssemblyBuilder builder, string name)
    {
        using var stream = new MemoryStream();
        builder.Save(stream);
        stream.Position = 0;
        var context = new AssemblyLoadContext("fake-" + name + "-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        return new FakePluginPages(context, context.LoadFromStream(stream));
    }
}
