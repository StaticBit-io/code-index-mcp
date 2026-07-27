using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using CodeIndex.Core.Sources;
using Xunit;

namespace CodeIndex.Core.Tests.Architecture;

/// <summary>
/// Verifies, by inspecting the IL of the built <c>CodeIndex.Core</c> assembly, that
/// <see cref="ISourceProvider"/> is the only route from this project into the real
/// filesystem. A direct <c>File.ReadAllText</c> or <c>Directory.EnumerateFiles</c> call
/// compiles and runs perfectly well from anywhere — nothing about the type system stops it —
/// so a code review is the only other way to catch a regression here, and a review does not
/// scale over time. Walking every <c>call</c>/<c>callvirt</c> instruction in every method body
/// and resolving its target type is what catches this reliably.
/// </summary>
/// <remarks>
/// Two locations are exempt, both deliberately, both narrow:
/// <list type="bullet">
/// <item><description>
/// The whole <c>CodeIndex.Core.Sources</c> namespace: <see cref="FileSystemSourceProvider"/>
/// lives here and *is* the sanctioned implementation of <see cref="ISourceProvider"/> that
/// actually touches disk — that is the entire point of the abstraction. Anything else in this
/// namespace (e.g. <see cref="SourceLines"/>) is provider plumbing, not a leak.
/// </description></item>
/// <item><description>
/// <c>CodeIndex.Core.Storage.IndexStore</c> by exact type name — the single sanctioned
/// exception outside <c>Sources</c>. It owns the on-disk index cache (<c>manifest.json</c>,
/// <c>vectors.bin</c>), which lives outside the indexed project entirely: that cache is not
/// project source code, so it is not subject to <see cref="ISourceProvider"/>'s remit, and
/// there would be no way to persist or load it at all without touching the real filesystem
/// somewhere.
/// </description></item>
/// </list>
/// Everywhere else — <c>Chunking</c>, <c>Indexing</c>, <c>Search</c>, <c>Embedding</c> — must
/// reach project sources exclusively through the <see cref="ISourceProvider"/> injected into it.
/// </remarks>
public sealed class SourceIsolationTests
{
    private static readonly HashSet<string> ForbiddenTypeNames = new(StringComparer.Ordinal)
    {
        "File", "Directory", "FileInfo", "DirectoryInfo",
    };

    private const string ExemptNamespace = "CodeIndex.Core.Sources";
    private const string ExemptFullTypeName = "CodeIndex.Core.Storage.IndexStore";

    [Fact]
    public void CoreAssembly_TouchesFileSystemOnlyThroughSourceProviderOrIndexStore()
    {
        string assemblyPath = typeof(ISourceProvider).Assembly.Location;

        List<string> violations = FindForbiddenFileSystemCalls(assemblyPath);

        Assert.True(violations.Count == 0,
            "Found direct System.IO.File/Directory/FileInfo/DirectoryInfo access outside " +
            $"ISourceProvider's implementation and IndexStore:{Environment.NewLine}" +
            string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// Walks every method body in <paramref name="assemblyPath"/> and returns one
    /// human-readable description per <c>call</c>/<c>callvirt</c> instruction whose resolved
    /// target type is one of <see cref="ForbiddenTypeNames"/>, skipping the two exempt
    /// locations described in the class remarks.
    /// </summary>
    private static List<string> FindForbiddenFileSystemCalls(string assemblyPath)
    {
        List<string> violations = new();

        using FileStream fileStream = File.OpenRead(assemblyPath);
        using PEReader peReader = new(fileStream);
        MetadataReader reader = peReader.GetMetadataReader();

        foreach (TypeDefinitionHandle typeHandle in reader.TypeDefinitions)
        {
            TypeDefinition typeDefinition = reader.GetTypeDefinition(typeHandle);

            string outerNamespace = ResolveOuterNamespace(reader, typeHandle);
            string outerFullName = ResolveOuterFullName(reader, typeHandle);

            if (string.Equals(outerNamespace, ExemptNamespace, StringComparison.Ordinal))
                continue;

            if (string.Equals(outerFullName, ExemptFullTypeName, StringComparison.Ordinal))
                continue;

            string displayTypeName = BuildDisplayTypeName(reader, typeHandle);

            foreach (MethodDefinitionHandle methodHandle in typeDefinition.GetMethods())
            {
                MethodDefinition methodDefinition = reader.GetMethodDefinition(methodHandle);
                if (methodDefinition.RelativeVirtualAddress == 0)
                    continue; // Abstract/extern/interface method: no IL body to inspect.

                string methodName = reader.GetString(methodDefinition.Name);
                MethodBodyBlock body = peReader.GetMethodBody(methodDefinition.RelativeVirtualAddress);

                // GetILBytes() is annotated nullable but only ever returns null for a body with
                // zero-length IL, which cannot contain a call/callvirt to anything.
                byte[]? il = body.GetILBytes();
                if (il is null)
                    continue;

                foreach (string forbiddenType in FindForbiddenCallsInMethodBody(reader, il))
                {
                    violations.Add($"{displayTypeName}.{methodName}() calls {forbiddenType}");
                }
            }
        }

        return violations;
    }

    /// <summary>Namespace of the outermost (non-nested) ancestor of <paramref name="typeHandle"/>.
    /// Nested types — including compiler-generated async state machines and iterators, which
    /// are what <c>async</c>/<c>yield</c> methods actually compile into — carry an empty
    /// <see cref="TypeDefinition.Namespace"/> of their own in ECMA-335 metadata, so this must
    /// walk <see cref="TypeDefinition.GetDeclaringType"/> up to the top before reading it.
    /// Skipping this walk would leave every async/iterator method's state machine type
    /// unattributed to its real namespace.</summary>
    private static string ResolveOuterNamespace(MetadataReader reader, TypeDefinitionHandle typeHandle)
    {
        TypeDefinitionHandle outerHandle = ResolveOuterTypeHandle(reader, typeHandle);
        TypeDefinition outerType = reader.GetTypeDefinition(outerHandle);
        return reader.GetString(outerType.Namespace);
    }

    /// <summary><c>Namespace.OutermostTypeName</c> for <paramref name="typeHandle"/>'s
    /// outermost ancestor — used only to compare against <see cref="ExemptFullTypeName"/>,
    /// which names a top-level type.</summary>
    private static string ResolveOuterFullName(MetadataReader reader, TypeDefinitionHandle typeHandle)
    {
        TypeDefinitionHandle outerHandle = ResolveOuterTypeHandle(reader, typeHandle);
        TypeDefinition outerType = reader.GetTypeDefinition(outerHandle);
        string ns = reader.GetString(outerType.Namespace);
        string name = reader.GetString(outerType.Name);
        return ns.Length == 0 ? name : $"{ns}.{name}";
    }

    private static TypeDefinitionHandle ResolveOuterTypeHandle(MetadataReader reader, TypeDefinitionHandle typeHandle)
    {
        TypeDefinition typeDefinition = reader.GetTypeDefinition(typeHandle);
        while (typeDefinition.IsNested)
        {
            typeHandle = typeDefinition.GetDeclaringType();
            typeDefinition = reader.GetTypeDefinition(typeHandle);
        }

        return typeHandle;
    }

    /// <summary>Builds a readable "Namespace.Outer+Nested" name for diagnostics, so a violation
    /// inside a compiler-generated state machine (e.g. <c>FileSystemSourceProvider+&lt;EnumerateAsync&gt;d__2</c>)
    /// still points a reader at the source method that produced it.</summary>
    private static string BuildDisplayTypeName(MetadataReader reader, TypeDefinitionHandle typeHandle)
    {
        List<string> names = new();
        TypeDefinition typeDefinition = reader.GetTypeDefinition(typeHandle);

        while (true)
        {
            names.Insert(0, reader.GetString(typeDefinition.Name));
            if (!typeDefinition.IsNested)
            {
                string ns = reader.GetString(typeDefinition.Namespace);
                string joined = string.Join("+", names);
                return ns.Length == 0 ? joined : $"{ns}.{joined}";
            }

            typeHandle = typeDefinition.GetDeclaringType();
            typeDefinition = reader.GetTypeDefinition(typeHandle);
        }
    }

    private static IEnumerable<string> FindForbiddenCallsInMethodBody(MetadataReader reader, byte[] il)
    {
        int position = 0;

        while (position < il.Length)
        {
            OpCode opcode = ReadOpCode(il, ref position);
            int operandStart = position;
            int operandSize = GetOperandSize(opcode.OperandType, il, position);

            if (opcode == OpCodes.Call || opcode == OpCodes.Callvirt)
            {
                int token = BitConverter.ToInt32(il, operandStart);
                string? forbiddenType = TryResolveForbiddenTargetType(reader, token);
                if (forbiddenType is not null)
                {
                    yield return forbiddenType;
                }
            }

            position += operandSize;
        }
    }

    /// <summary>
    /// If <paramref name="token"/> is a <see cref="MemberReference"/> (a call to a method
    /// defined in another assembly, e.g. anything in <c>System.IO</c>) — unwrapping one level
    /// of <see cref="MethodSpecification"/> first for a generic method instantiation — whose
    /// declaring type is a <see cref="TypeReference"/> named in <see cref="ForbiddenTypeNames"/>,
    /// returns a display string for it. A call to a method defined in this same assembly
    /// resolves to a <see cref="MethodDefinition"/> instead and is never forbidden — this
    /// project does not, and cannot, declare its own type named <c>File</c>/<c>Directory</c>.
    /// </summary>
    private static string? TryResolveForbiddenTargetType(MetadataReader reader, int token)
    {
        EntityHandle handle = MetadataTokens.EntityHandle(token);

        if (handle.Kind == HandleKind.MethodSpecification)
        {
            MethodSpecification spec = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
            handle = spec.Method;
        }

        if (handle.Kind != HandleKind.MemberReference)
            return null;

        MemberReference memberReference = reader.GetMemberReference((MemberReferenceHandle)handle);

        if (memberReference.Parent.Kind != HandleKind.TypeReference)
            return null;

        TypeReference typeReference = reader.GetTypeReference((TypeReferenceHandle)memberReference.Parent);
        string typeName = reader.GetString(typeReference.Name);

        if (!ForbiddenTypeNames.Contains(typeName))
            return null;

        string typeNamespace = reader.GetString(typeReference.Namespace);
        string memberName = reader.GetString(memberReference.Name);
        return typeNamespace.Length == 0 ? $"{typeName}.{memberName}" : $"{typeNamespace}.{typeName}.{memberName}";
    }

    private static OpCode ReadOpCode(byte[] il, ref int position)
    {
        byte first = il[position];
        if (first != 0xFE)
        {
            position += 1;
            return SingleByteOpCodes[first];
        }

        byte second = il[position + 1];
        position += 2;
        return TwoByteOpCodes[second];
    }

    /// <summary>
    /// Byte length of the operand that follows an opcode already consumed by
    /// <see cref="ReadOpCode"/>, per the ECMA-335 operand-type table. <see
    /// cref="OperandType.InlineSwitch"/> is the one variable-length case: its first four bytes
    /// are a jump-table entry count <c>N</c>, followed by <c>N</c> four-byte branch offsets.
    /// </summary>
    private static int GetOperandSize(OperandType operandType, byte[] il, int position) => operandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget => 1,
        OperandType.ShortInlineI => 1,
        OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget => 4,
        OperandType.InlineField => 4,
        OperandType.InlineI => 4,
        OperandType.InlineMethod => 4,
        OperandType.InlineSig => 4,
        OperandType.InlineString => 4,
        OperandType.InlineTok => 4,
        OperandType.InlineType => 4,
        OperandType.ShortInlineR => 4,
        OperandType.InlineI8 => 8,
        OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, position),
        _ => throw new NotSupportedException($"Unsupported IL operand type: {operandType}"),
    };

    private static readonly OpCode[] SingleByteOpCodes = BuildOpCodeTable(twoByte: false);
    private static readonly OpCode[] TwoByteOpCodes = BuildOpCodeTable(twoByte: true);

    /// <summary>
    /// Builds a 256-entry lookup table from every public static <see cref="OpCode"/> field on
    /// <see cref="OpCodes"/>, indexed by its encoded byte — the standard technique for a
    /// hand-rolled IL reader, since the BCL does not expose a ready-made opcode-by-byte lookup.
    /// Single-byte opcodes have <see cref="OpCode.Value"/> in <c>0x00..0xFF</c>; two-byte
    /// opcodes (the <c>0xFE</c>-prefixed extended set) have <see cref="OpCode.Value"/> in
    /// <c>0xFE00..0xFEFF</c>, and this indexes them by their low byte.
    /// </summary>
    private static OpCode[] BuildOpCodeTable(bool twoByte)
    {
        OpCode[] table = new OpCode[256];

        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(OpCode))
                continue;

            OpCode opCode = (OpCode)field.GetValue(null)!;
            ushort value = unchecked((ushort)opCode.Value);
            bool isTwoByte = (value & 0xFF00) == 0xFE00;

            if (isTwoByte == twoByte)
            {
                table[value & 0xFF] = opCode;
            }
        }

        return table;
    }
}
