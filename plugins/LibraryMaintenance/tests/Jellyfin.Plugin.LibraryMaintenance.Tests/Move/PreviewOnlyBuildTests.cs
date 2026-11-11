using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Jellyfin.Plugin.LibraryMaintenance.Move;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move;

/// <summary>
/// The plan-only build (<c>MOVE_PREVIEW_ONLY</c>) has no call that writes
/// a file, read from its IL. Every method of the Move namespaces and of the Move controller, and every method of the
/// assembly they call (lambdas, async state machines and local functions included, and every implementation of an
/// interface of the assembly they call through), is decoded; a call to a .NET or Jellyfin member that writes, moves or
/// deletes a file or folder, or changes Jellyfin's library, is a finding, and so is a call the check can't tell is safe
/// (a <c>FileStream</c> without constant read-only arguments, a Win32 function off the list). The build that will move
/// is read too, so the check is seen finding its one write (the job-start probe file). The test project builds the
/// other of the two builds into <c>OtherBuild\</c> (its csproj); <c>JFLM_PREVIEW_ONLY_DLL</c> names one more plan-only
/// DLL to read (a release's).
/// </summary>
public sealed class PreviewOnlyBuildTests
{
    private const string PluginFile = "Jellyfin.Plugin.LibraryMaintenance.dll";

    private static string ReferencedBuild => typeof(MoveFeature).Assembly.Location;

    private static string OtherBuild => Path.Combine(AppContext.BaseDirectory, "OtherBuild", PluginFile);

    private static string PreviewOnlyBuild => MoveFeature.IsPreviewOnlyBuild ? ReferencedBuild : OtherBuild;

    private static string MoveBuild => MoveFeature.IsPreviewOnlyBuild ? OtherBuild : ReferencedBuild;

    private static IEnumerable<string> PreviewOnlyBuilds()
    {
        yield return PreviewOnlyBuild;
        if (Environment.GetEnvironmentVariable("JFLM_PREVIEW_ONLY_DLL") is { Length: > 0 } extra)
        {
            yield return extra;
        }
    }

    [Fact]
    public void BothBuilds_AreThere_AndSayWhichTheyAre()
    {
        foreach (var path in PreviewOnlyBuilds())
        {
            using var preview = new IlReader(path);
            Assert.Equal(new byte[] { 0x17, 0x2A }, preview.Body("Jellyfin.Plugin.LibraryMaintenance.Move", "MoveFeature", "get_IsPreviewOnlyBuild"));
            Assert.Equal(new[] { "Access" }, preview.EnumNames("Jellyfin.Plugin.LibraryMaintenance.Move.Preflight", "DestinationWriteCheck"));
            Assert.DoesNotContain("CanWriteProbeFile", preview.MethodNames("Jellyfin.Plugin.LibraryMaintenance.Move.Preflight", "MovePreflight"));
            Assert.DoesNotContain(preview.MethodNames("Jellyfin.Plugin.LibraryMaintenance.Api", "LibraryMaintenanceMoveController"), m => m.Contains("Job", StringComparison.OrdinalIgnoreCase));
        }

        using var move = new IlReader(MoveBuild);
        Assert.Equal(new byte[] { 0x16, 0x2A }, move.Body("Jellyfin.Plugin.LibraryMaintenance.Move", "MoveFeature", "get_IsPreviewOnlyBuild"));
        Assert.Equal(new[] { "Access", "ProbeFile" }, move.EnumNames("Jellyfin.Plugin.LibraryMaintenance.Move.Preflight", "DestinationWriteCheck"));
        Assert.Contains("CanWriteProbeFile", move.MethodNames("Jellyfin.Plugin.LibraryMaintenance.Move.Preflight", "MovePreflight"));
    }

    [Fact]
    public void ThePlanOnlyBuild_HasNoCallThatWritesAFile_InMove()
    {
        foreach (var path in PreviewOnlyBuilds())
        {
            using var reader = new IlReader(path);
            var scan = reader.ScanMove();
            Assert.True(scan.Findings.Count == 0, path + ":\n" + string.Join("\n", scan.Findings));

            // It read what it should: the planner, the preflight's lock probe and write check, and the controller.
            Assert.Contains(scan.Read, m => m.EndsWith("FileLockProbe::IsOpenPortable", StringComparison.Ordinal));
            Assert.Contains(scan.Read, m => m.EndsWith("FolderWriteAccess::CanAdd", StringComparison.Ordinal));
            Assert.Contains(scan.Read, m => m.EndsWith("LibraryMaintenanceMoveController::PostLibraryMaintenanceMovePlan", StringComparison.Ordinal));
            Assert.True(scan.Read.Count > 500, "only " + scan.Read.Count + " methods read");
        }
    }

    [Fact]
    public void TheCheck_FindsTheProbeFileWrite_InTheBuildThatWillMove()
    {
        using var reader = new IlReader(MoveBuild);
        var finding = Assert.Single(reader.ScanMove().Findings);
        Assert.Contains("MovePreflight::CanWriteProbeFile", finding, StringComparison.Ordinal);
        Assert.Contains("System.IO.FileStream::.ctor", finding, StringComparison.Ordinal);
    }

    /// <summary>The result of <see cref="IlReader.ScanMove"/>.</summary>
    /// <param name="Findings">What writes, or can't be told not to.</param>
    /// <param name="Read">Every method read.</param>
    internal sealed record Scan(IReadOnlyList<string> Findings, IReadOnlyCollection<string> Read);

    /// <summary>One IL instruction.</summary>
    /// <param name="Op">The opcode.</param>
    /// <param name="Operand">Its operand (a token, a constant, a branch), 0 when it has none.</param>
    internal readonly record struct Instruction(OpCode Op, long Operand);

    /// <summary>The plugin's metadata and IL, through System.Reflection.Metadata (the file is never loaded to run).</summary>
    internal sealed class IlReader : IDisposable
    {
        private const string Root = "Jellyfin.Plugin.LibraryMaintenance";

        // What Move's planning may call in Win32: the volume calls and CreateFileW (the P/Invoke surface Move may use).
        private static readonly HashSet<string> Win32Allowed = new(StringComparer.Ordinal)
        {
            "GetVolumePathNameW", "GetVolumeNameForVolumeMountPointW", "GetDiskFreeSpaceExW", "SetThreadErrorMode", "CreateFileW", "GetVolumeInformationW",
        };

        // What only reads, of the types that also write (anything else of them is a finding).
        private static readonly Dictionary<string, HashSet<string>> ReadOnlyMembers = new(StringComparer.Ordinal)
        {
            ["System.IO.File"] = Names("Exists", "ReadAllText", "ReadAllBytes", "ReadAllLines", "ReadLines", "OpenRead", "OpenText", "GetAttributes", "GetCreationTime", "GetCreationTimeUtc", "GetLastWriteTime", "GetLastWriteTimeUtc", "GetLastAccessTime", "GetLastAccessTimeUtc", "ResolveLinkTarget", "ReadAllTextAsync", "ReadAllBytesAsync", "ReadAllLinesAsync", "ReadLinesAsync"),
            ["System.IO.Directory"] = Names("Exists", "EnumerateFiles", "EnumerateDirectories", "EnumerateFileSystemEntries", "GetFiles", "GetDirectories", "GetFileSystemEntries", "GetParent", "GetDirectoryRoot", "GetCreationTime", "GetCreationTimeUtc", "GetLastWriteTime", "GetLastWriteTimeUtc", "GetLastAccessTime", "GetLastAccessTimeUtc", "ResolveLinkTarget"),
            ["System.IO.FileSystemInfo"] = Names("Refresh", "ResolveLinkTarget", "ToString"),
            ["System.IO.FileInfo"] = Names(".ctor", "OpenRead", "OpenText", "Refresh", "ResolveLinkTarget", "ToString"),
            ["System.IO.DirectoryInfo"] = Names(".ctor", "EnumerateFiles", "EnumerateDirectories", "EnumerateFileSystemInfos", "GetFiles", "GetDirectories", "GetFileSystemInfos", "Refresh", "ResolveLinkTarget", "ToString"),
            ["System.IO.FileStream"] = Names("Read", "ReadAsync", "ReadByte", "Seek", "Dispose", "DisposeAsync", "Close", "CopyTo", "CopyToAsync"),
            ["System.IO.RandomAccess"] = Names("Read", "ReadAsync", "GetLength"),
        };

        // Types every use of which is a finding.
        private static readonly HashSet<string> Forbidden = new(StringComparer.Ordinal)
        {
            "System.IO.StreamWriter", "System.IO.FileStreamOptions", "System.IO.BinaryWriter", "System.IO.Compression.ZipFile", "System.IO.Compression.ZipFileExtensions",
            "System.IO.MemoryMappedFiles.MemoryMappedFile", "System.Runtime.InteropServices.NativeLibrary", "System.Diagnostics.Process",
        };

        // Calls that run code the check can't follow.
        private static readonly HashSet<string> Unfollowable = new(StringComparer.Ordinal)
        {
            "System.Reflection.MethodBase::Invoke", "System.Reflection.MethodInfo::Invoke", "System.Reflection.ConstructorInfo::Invoke", "System.Delegate::DynamicInvoke",
            "System.Runtime.InteropServices.Marshal::GetDelegateForFunctionPointer", "System.Activator::CreateInstance",
        };

        // A Jellyfin service's members that change the library, its items, files or settings (ILibraryManager, IFileSystem,
        // IItemRepository, IProviderManager, ILibraryMonitor, …): by their names' starts.
        private static readonly string[] JellyfinWrites =
        [
            "Add", "Create", "Delete", "Move", "Remove", "Save", "Set", "Swap", "Update", "Write", "Queue", "Refresh", "Report", "Register", "Run", "Validate", "Restart", "Shutdown",
        ];

        private static readonly Dictionary<ushort, OpCode> OpCodesByValue = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (OpCode)f.GetValue(null)!)
            .ToDictionary(o => unchecked((ushort)o.Value));

        private readonly FileStream _stream;
        private readonly PEReader _pe;
        private readonly MetadataReader _md;

        public IlReader(string path)
        {
            Assert.True(File.Exists(path), "No build at " + path + " (the test project's csproj builds it).");
            _stream = File.OpenRead(path);
            _pe = new PEReader(_stream);
            _md = _pe.GetMetadataReader();
        }

        public void Dispose()
        {
            _pe.Dispose();
            _stream.Dispose();
        }

        public byte[] Body(string ns, string type, string method)
        {
            var def = _md.GetMethodDefinition(Type(ns, type).GetMethods().Single(m => _md.GetString(_md.GetMethodDefinition(m).Name) == method));
            return _pe.GetMethodBody(def.RelativeVirtualAddress).GetILBytes()!;
        }

        public string[] EnumNames(string ns, string type)
            => Type(ns, type).GetFields().Select(f => _md.GetFieldDefinition(f)).Where(f => (f.Attributes & FieldAttributes.Static) != 0).Select(f => _md.GetString(f.Name)).ToArray();

        public string[] MethodNames(string ns, string type)
            => Type(ns, type).GetMethods().Select(m => _md.GetString(_md.GetMethodDefinition(m).Name)).ToArray();

        // Reads Move: the roots, then every method of the assembly they reach.
        public Scan ScanMove()
        {
            var findings = new List<string>();
            var seen = new HashSet<MethodDefinitionHandle>();
            var queue = new Queue<MethodDefinitionHandle>();
            void Reach(MethodDefinitionHandle handle)
            {
                if (seen.Add(handle))
                {
                    queue.Enqueue(handle);
                }
            }

            foreach (var handle in _md.TypeDefinitions)
            {
                var outer = _md.GetTypeDefinition(Outermost(handle));
                var ns = _md.GetString(outer.Namespace);
                if (ns == Root + ".Move" || ns.StartsWith(Root + ".Move.", StringComparison.Ordinal)
                    || (ns == Root + ".Api" && _md.GetString(outer.Name) == "LibraryMaintenanceMoveController"))
                {
                    foreach (var method in _md.GetTypeDefinition(handle).GetMethods())
                    {
                        Reach(method);
                    }
                }
            }

            var read = new List<string>();
            while (queue.Count > 0)
            {
                var handle = queue.Dequeue();
                var def = _md.GetMethodDefinition(handle);
                var name = Name(handle);
                read.Add(name);
                if ((def.Attributes & MethodAttributes.PinvokeImpl) != 0)
                {
                    var entry = _md.GetString(def.GetImport().Name);
                    if (!Win32Allowed.Contains(entry))
                    {
                        findings.Add(name + " is the Win32 " + entry + ", not on the list");
                    }

                    continue;
                }

                if (def.RelativeVirtualAddress == 0)
                {
                    // An interface's or abstract method: what implements it is read instead (below).
                    continue;
                }

                var code = Decode(_pe.GetMethodBody(def.RelativeVirtualAddress).GetILBytes()!);
                for (var i = 0; i < code.Count; i++)
                {
                    var op = code[i].Op;
                    if (op == OpCodes.Calli)
                    {
                        findings.Add(name + " calls through a function pointer (calli)");
                        continue;
                    }

                    if (op != OpCodes.Call && op != OpCodes.Callvirt && op != OpCodes.Newobj && op != OpCodes.Ldftn && op != OpCodes.Ldvirtftn && op != OpCodes.Jmp)
                    {
                        continue;
                    }

                    var target = MetadataTokens.EntityHandle((int)code[i].Operand);
                    if (target.Kind == HandleKind.MethodSpecification)
                    {
                        target = _md.GetMethodSpecification((MethodSpecificationHandle)target).Method;
                    }

                    if (target.Kind == HandleKind.MethodDefinition)
                    {
                        var callee = (MethodDefinitionHandle)target;
                        var calleeDef = _md.GetMethodDefinition(callee);
                        if ((calleeDef.Attributes & MethodAttributes.PinvokeImpl) != 0 && _md.GetString(calleeDef.GetImport().Name) == "CreateFileW")
                        {
                            CheckCreateFile(code, i, name, findings);
                        }

                        Reach(callee);
                        if (calleeDef.RelativeVirtualAddress == 0)
                        {
                            foreach (var implementation in Implementations(callee))
                            {
                                Reach(implementation);
                            }
                        }

                        continue;
                    }

                    if (target.Kind != HandleKind.MemberReference)
                    {
                        findings.Add(name + " calls a " + target.Kind + " the check can't read");
                        continue;
                    }

                    var reference = _md.GetMemberReference((MemberReferenceHandle)target);
                    var member = _md.GetString(reference.Name);
                    var (type, own) = Parent(reference.Parent);
                    if (own is { } generic)
                    {
                        // A method of a generic type of the assembly: every method of that name.
                        foreach (var method in _md.GetTypeDefinition(generic).GetMethods().Where(m => _md.GetString(_md.GetMethodDefinition(m).Name) == member))
                        {
                            Reach(method);
                        }

                        continue;
                    }

                    if (Check(type, member, reference, code, i) is { } why)
                    {
                        findings.Add(name + " calls " + type + "::" + member + ": " + why);
                    }
                }
            }

            return new Scan(findings, read);
        }

        private static HashSet<string> Names(params string[] names) => new(names, StringComparer.Ordinal);

        // Why a call to a member of another assembly is a finding, or null.
        private string? Check(string type, string member, MemberReference reference, List<Instruction> code, int at)
        {
            if (Forbidden.Contains(type) || Unfollowable.Contains(type + "::" + member))
            {
                return "never in the plan-only build";
            }

            if (type == "System.IO.FileStream" && member == ".ctor")
            {
                return FileStreamIsReadOnly(reference, code, at) ? null : "not opened read-only with constant arguments";
            }

            if (ReadOnlyMembers.TryGetValue(type, out var allowed))
            {
                return allowed.Contains(member) || (member.StartsWith("get_", StringComparison.Ordinal) && type != "System.IO.File" && type != "System.IO.Directory") ? null : "it writes, or may";
            }

            var simple = type[(type.LastIndexOf('.') + 1)..];
            var isJellyfin = type.StartsWith("MediaBrowser.", StringComparison.Ordinal) || type.StartsWith("Jellyfin.", StringComparison.Ordinal) || type.StartsWith("Emby.", StringComparison.Ordinal);
            var isService = simple.Length > 1 && simple[0] == 'I' && char.IsUpper(simple[1]);
            if (isJellyfin && isService && JellyfinWrites.Any(p => member.StartsWith(p, StringComparison.Ordinal)) && !member.StartsWith("get_", StringComparison.Ordinal))
            {
                return "a Jellyfin service's change";
            }

            if (isJellyfin && (member.StartsWith("UpdateToRepository", StringComparison.Ordinal) || member == "ChangedExternally" || member.StartsWith("RefreshMetadata", StringComparison.Ordinal) || member.StartsWith("Delete", StringComparison.Ordinal)))
            {
                return "it changes a library item";
            }

            return null;
        }

        // new FileStream(path, FileMode.Open, FileAccess.Read[, share[, size[, options or async]]]) or (handle, FileAccess.Read, …).
        private bool FileStreamIsReadOnly(MemberReference reference, List<Instruction> code, int at)
        {
            var parameters = reference.DecodeMethodSignature(new TypeNames(), null).ParameterTypes;
            var args = Arguments(code, at, parameters.Length - 1);
            if (args is null)
            {
                return false;
            }

            if (parameters.Length >= 3 && parameters[0] == "String" && parameters[1] == "FileMode" && parameters[2] == "FileAccess")
            {
                return Constant(args[0]) == 3 && Constant(args[1]) == 1;
            }

            return parameters.Length >= 2 && parameters[0] == "SafeFileHandle" && parameters[1] == "FileAccess" && Constant(args[0]) == 1;
        }

        // CreateFileW(name, access, share, security, creationDisposition, flags, template): OPEN_EXISTING (it never creates
        // or truncates), without FILE_FLAG_DELETE_ON_CLOSE, as constants.
        private void CheckCreateFile(List<Instruction> code, int at, string caller, List<string> findings)
        {
            var args = Arguments(code, at, 3);
            if (args is null || Constant(args[0]) != 3 || Constant(args[1]) is not { } flags || (flags & 0x04000000) != 0)
            {
                findings.Add(caller + " calls CreateFileW without a constant OPEN_EXISTING and flags without FILE_FLAG_DELETE_ON_CLOSE");
            }
        }

        // The instructions that push a call's last arguments, each one simple expression (a constant, a static field, an
        // argument or local, a constant converted to a native int), or null when they aren't that simple.
        private static List<List<Instruction>>? Arguments(List<Instruction> code, int at, int count)
        {
            var args = new List<List<Instruction>>();
            var i = at - 1;
            while (args.Count < count)
            {
                if (i < 0)
                {
                    return null;
                }

                var op = code[i].Op;
                if ((op == OpCodes.Conv_I || op == OpCodes.Conv_U || op == OpCodes.Conv_I8 || op == OpCodes.Conv_U8) && i > 0 && IsConstant(code[i - 1].Op))
                {
                    args.Insert(0, [code[i - 1], code[i]]);
                    i -= 2;
                }
                else if (IsConstant(op) || op == OpCodes.Ldsfld || op == OpCodes.Ldnull || op.Name!.StartsWith("ldarg", StringComparison.Ordinal) || op.Name!.StartsWith("ldloc", StringComparison.Ordinal))
                {
                    args.Insert(0, [code[i]]);
                    i--;
                }
                else
                {
                    return null;
                }
            }

            return args;
        }

        private static bool IsConstant(OpCode op) => op.Name!.StartsWith("ldc.i4", StringComparison.Ordinal) || op == OpCodes.Ldc_I8;

        private static long? Constant(List<Instruction> arg)
        {
            var op = arg[0].Op;
            if (!IsConstant(op))
            {
                return null;
            }

            var value = op == OpCodes.Ldc_I4_M1 ? -1 : op.Value is >= 0x16 and <= 0x1E ? op.Value - 0x16 : arg[0].Operand;
            return value & 0xFFFFFFFFL;
        }

        private List<Instruction> Decode(byte[] il)
        {
            var code = new List<Instruction>();
            var p = 0;
            while (p < il.Length)
            {
                ushort value = il[p++];
                if (value == 0xFE)
                {
                    value = (ushort)(0xFE00 | il[p++]);
                }

                var op = OpCodesByValue[value];
                long operand = 0;
                switch (op.OperandType)
                {
                    case OperandType.InlineNone:
                        break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        operand = op == OpCodes.Ldc_I4_S ? (sbyte)il[p] : il[p];
                        p += 1;
                        break;
                    case OperandType.InlineVar:
                        operand = BitConverter.ToUInt16(il, p);
                        p += 2;
                        break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        operand = BitConverter.ToInt64(il, p);
                        p += 8;
                        break;
                    case OperandType.InlineSwitch:
                        p += 4 + (4 * BitConverter.ToInt32(il, p));
                        break;
                    default:
                        operand = BitConverter.ToInt32(il, p);
                        p += 4;
                        break;
                }

                code.Add(new Instruction(op, operand));
            }

            return code;
        }

        private TypeDefinition Type(string ns, string name)
            => _md.GetTypeDefinition(_md.TypeDefinitions.Single(t => _md.GetString(_md.GetTypeDefinition(t).Namespace) == ns && _md.GetString(_md.GetTypeDefinition(t).Name) == name));

        private TypeDefinitionHandle Outermost(TypeDefinitionHandle handle)
        {
            while (_md.GetTypeDefinition(handle).GetDeclaringType() is { IsNil: false } outer)
            {
                handle = outer;
            }

            return handle;
        }

        private string TypeName(TypeDefinitionHandle handle)
        {
            var type = _md.GetTypeDefinition(handle);
            var outer = type.GetDeclaringType();
            return outer.IsNil ? _md.GetString(type.Namespace) + "." + _md.GetString(type.Name) : TypeName(outer) + "/" + _md.GetString(type.Name);
        }

        private string Name(MethodDefinitionHandle handle)
        {
            var def = _md.GetMethodDefinition(handle);
            return TypeName(def.GetDeclaringType()) + "::" + _md.GetString(def.Name);
        }

        // The methods of the assembly with an interface's or abstract method's name and parameter count, outside its type:
        // more than implement it, never fewer.
        private IEnumerable<MethodDefinitionHandle> Implementations(MethodDefinitionHandle handle)
        {
            var def = _md.GetMethodDefinition(handle);
            var name = _md.GetString(def.Name);
            var count = def.GetParameters().Count;
            foreach (var type in _md.TypeDefinitions.Where(t => t != def.GetDeclaringType()))
            {
                foreach (var method in _md.GetTypeDefinition(type).GetMethods())
                {
                    var other = _md.GetMethodDefinition(method);
                    var otherName = _md.GetString(other.Name);
                    if ((otherName == name || otherName.EndsWith("." + name, StringComparison.Ordinal)) && other.GetParameters().Count == count)
                    {
                        yield return method;
                    }
                }
            }
        }

        // A member reference's type: its full name, and the type definition when it is a generic type of this assembly.
        private (string Name, TypeDefinitionHandle? Own) Parent(EntityHandle parent)
        {
            switch (parent.Kind)
            {
                case HandleKind.TypeReference:
                    return (ReferenceName((TypeReferenceHandle)parent), null);
                case HandleKind.TypeDefinition:
                    return (TypeName((TypeDefinitionHandle)parent), (TypeDefinitionHandle)parent);
                case HandleKind.TypeSpecification:
                    var blob = _md.GetBlobReader(_md.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
                    if (blob.ReadSignatureTypeCode() == SignatureTypeCode.GenericTypeInstance)
                    {
                        blob.ReadSignatureTypeCode();
                        var generic = blob.ReadTypeHandle();
                        return generic.Kind == HandleKind.TypeDefinition
                            ? (TypeName((TypeDefinitionHandle)generic), (TypeDefinitionHandle)generic)
                            : (ReferenceName((TypeReferenceHandle)generic), null);
                    }

                    return ("(a type specification)", null);
                default:
                    return ("(a " + parent.Kind + ")", null);
            }
        }

        private string ReferenceName(TypeReferenceHandle handle)
        {
            var type = _md.GetTypeReference(handle);
            var name = _md.GetString(type.Name);
            var tick = name.IndexOf('`', StringComparison.Ordinal);
            name = tick > 0 ? name[..tick] : name;
            return type.ResolutionScope.Kind == HandleKind.TypeReference
                ? ReferenceName((TypeReferenceHandle)type.ResolutionScope) + "/" + name
                : _md.GetString(type.Namespace) + "." + name;
        }
    }

    // Type names in a signature, just enough to tell FileStream's constructors apart.
    private sealed class TypeNames : ISignatureTypeProvider<string, object?>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeDefinition(handle).Name);

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeReference(handle).Name);

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => "(spec)";

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[,]";

        public string GetByReferenceType(string elementType) => elementType + "&";

        public string GetPointerType(string elementType) => elementType + "*";

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";

        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;

        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;

        public string GetFunctionPointerType(MethodSignature<string> signature) => "(fnptr)";

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetPinnedType(string elementType) => elementType;
    }
}
