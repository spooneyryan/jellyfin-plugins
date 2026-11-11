using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace Jellyfin.Plugin.Kit.TestSupport.Api;

/// <summary>
/// Lists the types a compiled method body names: the types, fields and methods its IL refers to (with their declaring,
/// parameter, return and generic argument types), its local variables and its catch clauses. Used by
/// <see cref="PublicBoundaryChecks"/> to see what a controller's code really touches, which its signatures don't show.
/// </summary>
internal static class MethodBodyReferences
{
    private static readonly OpCode?[] _oneByte = new OpCode?[0x100];
    private static readonly OpCode?[] _twoByte = new OpCode?[0x100];

    static MethodBodyReferences()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode code)
            {
                continue;
            }

            var value = (ushort)code.Value;
            if (code.Size == 1)
            {
                _oneByte[value] = code;
            }
            else
            {
                _twoByte[value & 0xFF] = code;
            }
        }
    }

    /// <summary>
    /// Lists every method, constructor and type initializer declared by a type and by all its nested types (compiler
    /// generated ones included: async state machines, iterator classes, lambda closures).
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The methods, the type's own first.</returns>
    public static IEnumerable<MethodBase> DeclaredMethods(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var method in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
        {
            yield return method;
        }

        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (var method in DeclaredMethods(nested))
            {
                yield return method;
            }
        }
    }

    /// <summary>
    /// Lists the fields declared by a type and by all its nested types (the state of async methods and closures).
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The fields.</returns>
    public static IEnumerable<FieldInfo> DeclaredFields(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        return type.GetFields(All).Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(DeclaredFields));
    }

    /// <summary>
    /// Lists the types a method's body names, each with every generic argument and element type it is built from.
    /// </summary>
    /// <param name="method">The method; one without a body (abstract, extern) names nothing.</param>
    /// <returns>The types, possibly repeated.</returns>
    public static IEnumerable<Type> FindTypes(MethodBase method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return FindDirectTypes(method).SelectMany(Expand);
    }

    /// <summary>
    /// Gets a type with the generic arguments and element types it is built from, recursively; generic parameters are left
    /// out.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The types.</returns>
    public static IEnumerable<Type> Expand(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.IsGenericParameter)
        {
            yield break;
        }

        if (type.HasElementType)
        {
            foreach (var element in Expand(type.GetElementType()!))
            {
                yield return element;
            }

            yield break;
        }

        yield return type;
        if (type.IsConstructedGenericType)
        {
            yield return type.GetGenericTypeDefinition();
            foreach (var argument in type.GetGenericArguments().SelectMany(Expand))
            {
                yield return argument;
            }
        }
    }

    /// <summary>
    /// Lists the members a method's body refers to in its IL: the methods and constructors it calls or loads, the fields
    /// it reads or writes, and the types it names (casts, <c>typeof</c>, allocations), as resolved in the method's context.
    /// </summary>
    /// <param name="method">The method; one without a body (abstract, extern) refers to nothing.</param>
    /// <returns>The members, possibly repeated, in IL order.</returns>
    public static IEnumerable<MemberInfo> FindMembers(MethodBase method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
        var typeArguments = method.DeclaringType is { IsGenericType: true } declaring ? declaring.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var position = 0;
        while (position < il.Length)
        {
            var first = il[position++];
            var code = first == 0xFE ? _twoByte[il[position++]] : _oneByte[first];
            if (code is not { } opCode)
            {
                throw new InvalidOperationException($"Unknown IL opcode 0x{first:X2} at {position - 1} in {method.DeclaringType?.FullName}.{method.Name}.");
            }

            switch (opCode.OperandType)
            {
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                    if (method.Module.ResolveMember(BitConverter.ToInt32(il, position), typeArguments, methodArguments) is { } member)
                    {
                        yield return member;
                    }

                    position += 4;
                    break;
                case OperandType.InlineSwitch:
                    position += 4 + (4 * BitConverter.ToInt32(il, position));
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    position += 8;
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    position += 1;
                    break;
                case OperandType.InlineVar:
                    position += 2;
                    break;
                case OperandType.InlineNone:
                    break;
                default:
                    // InlineBrTarget, InlineI, InlineSig, InlineString, ShortInlineR.
                    position += 4;
                    break;
            }
        }
    }

    private static IEnumerable<Type> FindDirectTypes(MethodBase method)
    {
        var body = method.GetMethodBody();
        if (body is null)
        {
            yield break;
        }

        foreach (var local in body.LocalVariables)
        {
            yield return local.LocalType;
        }

        foreach (var clause in body.ExceptionHandlingClauses)
        {
            if (clause.Flags == ExceptionHandlingClauseOptions.Clause && clause.CatchType is { } catchType)
            {
                yield return catchType;
            }
        }

        foreach (var member in FindMembers(method))
        {
            foreach (var type in TypesOf(member))
            {
                yield return type;
            }
        }
    }

    private static IEnumerable<Type> TypesOf(MemberInfo? member)
    {
        switch (member)
        {
            case Type type:
                yield return type;
                break;
            case FieldInfo field:
                yield return field.DeclaringType!;
                yield return field.FieldType;
                break;
            case MethodBase called:
                if (called.DeclaringType is { } declaring)
                {
                    yield return declaring;
                }

                foreach (var parameter in called.GetParameters())
                {
                    yield return parameter.ParameterType;
                }

                if (called is MethodInfo info)
                {
                    yield return info.ReturnType;
                    if (info.IsConstructedGenericMethod)
                    {
                        foreach (var argument in info.GetGenericArguments())
                        {
                            yield return argument;
                        }
                    }
                }

                break;
        }
    }
}
