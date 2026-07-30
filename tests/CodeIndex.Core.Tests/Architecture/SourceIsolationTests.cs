using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using CodeIndex.Core.Sources;
using CodeIndex.Core.Storage;
using Xunit;

namespace CodeIndex.Core.Tests.Architecture;

/// <summary>
/// Verifies, by inspecting the IL of the built <c>CodeIndex.Core</c> assembly, that
/// <see cref="ISourceProvider"/> is the only route from this project into the real
/// filesystem. A direct <c>File.ReadAllText</c> or <c>Directory.EnumerateFiles</c> call
/// compiles and runs perfectly well from anywhere — nothing about the type system stops it —
/// so a code review is the only other way to catch a regression here, and a review does not
/// scale over time. Walking every <c>call</c>/<c>callvirt</c>/<c>newobj</c> instruction in every
/// method body and resolving its target type is what catches this reliably —
/// <c>newobj</c> matters as much as the other two: <c>new FileInfo(path).Length</c> never emits
/// a <c>call</c>/<c>callvirt</c> to anything named <c>File</c>/<c>Directory</c>/<c>FileInfo</c>/
/// <c>DirectoryInfo</c>, only a <c>newobj</c> for the constructor.
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
/// <c>CodeIndex.Core.Storage.IndexStore</c> and <c>CodeIndex.Core.Storage.OverlayRegistryStore</c>,
/// by exact type name — the sanctioned exceptions outside <c>Sources</c>. Both own on-disk index
/// cache state (<see cref="IndexStore"/>: <c>manifest.json</c>/<c>vectors.bin</c>; <see
/// cref="OverlayRegistryStore"/>: the per-branch overlay pool's <c>overlays/registry.json</c> and
/// the overlay slot directories it evicts), which lives outside the indexed project entirely:
/// that cache is not project source code, so it is not subject to <see cref="ISourceProvider"/>'s
/// remit, and there would be no way to persist or load it at all without touching the real
/// filesystem somewhere. An overlay slot's own chunk/fingerprint/vector data is persisted through
/// a plain <see cref="IndexStore"/> pointed at that slot's directory — reusing the already-exempt
/// type instead of adding a third one.
/// </description></item>
/// <item><description>
/// Any top-level (non-nested) type whose own metadata carries
/// <see cref="System.Runtime.CompilerServices.CompilerGeneratedAttribute"/> — a rule for
/// injected/synthesized types rather than a rule about one specific tool. It exists because
/// <c>dotnet test --collect:"XPlat Code Coverage"</c> makes coverlet rewrite the built assembly
/// in place before this test ever sees it: it weaves in a static tracker type (observed, at time
/// of writing, as <c>Coverlet.Core.Instrumentation.Tracker</c>-namespaced, name
/// <c>&lt;ModuleName&gt;_&lt;guid&gt;</c>) whose <c>WriteHits</c>/<c>UnloadModule</c> methods do
/// exactly the raw <c>File</c>/<c>Directory</c>/<c>FileStream</c> access this test exists to
/// forbid, in order to persist per-line hit counts to a temp file — that is coverlet's job, not a
/// violation of this codebase's rules. Matching on a hardcoded "Coverlet" namespace was
/// deliberately rejected: it would silently stop working the moment coverlet renames its
/// injected namespace (it already has, across major versions), and would need a fresh carve-out
/// for every other instrumentation tool (dotnet-coverage/Microsoft.CodeCoverage, AltCover,
/// OpenCover, ...) a future run might use. Verified empirically (dumping the actual instrumented
/// assembly's metadata) that coverlet's injected type is a top-level, non-nested type stamped
/// with <c>[CompilerGenerated]</c> — the same attribute Roslyn stamps on its own synthesized
/// state machines and closures — which makes "carries [CompilerGenerated]" a signal about
/// *provenance* (compiler- or tool-synthesized, not authored by a person) rather than about any
/// one tool's naming. <b>What this deliberately keeps catching:</b> a real violation written by a
/// person always lives, directly or indirectly, in a type <i>they</i> declared. Even when that
/// person's code is an <c>async</c> method or a lambda — which the C# compiler really does lower
/// into its own <c>[CompilerGenerated]</c> type — that lowered type is a <i>nested</i> type of the
/// class the person wrote, and <see cref="ResolveOuterNamespace"/>/<see cref="ResolveOuterFullName"/>
/// already attribute nested types to their outermost, non-generated ancestor for exemption
/// purposes; this new rule only inspects that same outer type's own attributes, so it never
/// exempts a state machine or closure on the strength of its own generated-ness. <b>What this
/// would stop catching, plainly stated:</b> nothing reachable through ordinary C#, but it is not
/// airtight against deliberate evasion — a person could hand-write
/// <c>[System.Runtime.CompilerServices.CompilerGenerated] class Evil { void M() =&gt; File.Delete(...); }</c>
/// as a top-level type and this rule would skip it. That is indistinguishable, from IL alone,
/// from genuine tool output; catching it would require also verifying provenance against the PDB
/// (e.g. "this type has no sequence points because it was never compiled from a source file"),
/// which this test does not currently do. Slapping an unrelated compiler-services attribute on a
/// hand-written type is exactly the kind of conspicuous, deliberate act a code review — the
/// fallback this test exists to reduce reliance on, not eliminate — remains well-suited to catch.
/// </description></item>
/// </list>
/// Everywhere else — <c>Chunking</c>, <c>Indexing</c>, <c>Search</c>, <c>Embedding</c> — must
/// reach project sources exclusively through the <see cref="ISourceProvider"/> injected into it.
/// <para/>
/// <b>Deliberately scoped to the <c>CodeIndex.Core</c> assembly only — this does not, and
/// should not, extend to <c>CodeIndex.Server</c>.</b> The whole reason this rule exists is
/// stated on <see cref="ISourceProvider"/> itself: it "keeps chunking and indexing testable
/// against in-memory inputs". That is a property of the <i>business logic</i> — Core is where
/// <c>InMemorySourceProvider</c> stands in for a real project tree in tests, and where an
/// untracked direct filesystem call would silently make a unit test not actually be a unit test.
/// <c>CodeIndex.Server</c> is the composition root and CLI host: process startup, DI wiring,
/// console output, and (as of this writing) reporting the on-disk size of the cache directory
/// for the <c>--status</c> path — none of it is chunking or indexing, none of it needs to run
/// against an in-memory fake, and treating "this process is a CLI that necessarily touches its
/// own working directory" as a violation would either force an ever-growing pile of
/// Server-specific exemptions or push legitimate host-level I/O through <c>ISourceProvider</c>,
/// an abstraction for *project source*, not for the host's own bookkeeping. This was a real
/// judgement call, not a default: a genuine violation was found in <c>Program.cs</c> (computing
/// the cache directory's size with raw <c>Directory</c>/<c>FileInfo</c> calls for
/// <c>--status</c>), and the fix routes that computation through
/// <see cref="global::CodeIndex.Core.Storage.IndexStore.ComputeCacheSizeBytes"/> — the already-
/// sanctioned exception above — rather than either leaving Server unscanned-and-violating or
/// scanning Server and exempting it. Server ends up with *zero* <c>File</c>/<c>Directory</c>/
/// <c>FileInfo</c>/<c>DirectoryInfo</c> references anywhere in its own code as a result, so if a
/// future change wants this same IL scan applied to that assembly too, it would start from a
/// clean slate rather than needing a new exemption carved out for it on day one.
/// </remarks>
public sealed class SourceIsolationTests
{
    private static readonly HashSet<string> ForbiddenTypeNames = new(StringComparer.Ordinal)
    {
        "File", "Directory", "FileInfo", "DirectoryInfo",
        "FileStream", "StreamReader", "StreamWriter", "FileSystemWatcher",
    };

    private const string ForbiddenNamespace = "System.IO";

    private const string ExemptNamespace = "CodeIndex.Core.Sources";

    private static readonly HashSet<string> ExemptFullTypeNames = new(StringComparer.Ordinal)
    {
        "CodeIndex.Core.Storage.IndexStore",
        "CodeIndex.Core.Storage.OverlayRegistryStore",
    };

    private const string CompilerGeneratedAttributeNamespace = "System.Runtime.CompilerServices";
    private const string CompilerGeneratedAttributeName = "CompilerGeneratedAttribute";

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
    /// human-readable description per <c>call</c>/<c>callvirt</c>/<c>newobj</c> instruction whose
    /// resolved target type is one of <see cref="ForbiddenTypeNames"/>, skipping the two exempt
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

            if (ExemptFullTypeNames.Contains(outerFullName))
                continue;

            if (IsOuterTypeCompilerGenerated(reader, typeHandle))
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
    /// outermost ancestor — used only to compare against <see cref="ExemptFullTypeNames"/>,
    /// which names top-level types.</summary>
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

    /// <summary>
    /// True if <paramref name="typeHandle"/>'s outermost (non-nested) ancestor is itself directly
    /// decorated with <see cref="System.Runtime.CompilerServices.CompilerGeneratedAttribute"/> —
    /// see the third <c>&lt;item&gt;</c> in the class remarks for why this is a sound, tool-agnostic
    /// exemption and exactly what it does and does not stop the scan from catching. Reusing
    /// <see cref="ResolveOuterTypeHandle"/> (the same walk <see cref="ResolveOuterNamespace"/> and
    /// <see cref="ResolveOuterFullName"/> already perform) is what keeps this from ever exempting a
    /// nested, compiler-generated async state machine or lambda closure on its own generated-ness:
    /// those are attributed to their non-generated declaring type before this check ever runs.
    /// </summary>
    private static bool IsOuterTypeCompilerGenerated(MetadataReader reader, TypeDefinitionHandle typeHandle)
    {
        TypeDefinitionHandle outerHandle = ResolveOuterTypeHandle(reader, typeHandle);
        TypeDefinition outerType = reader.GetTypeDefinition(outerHandle);

        foreach (CustomAttributeHandle attributeHandle in outerType.GetCustomAttributes())
        {
            CustomAttribute attribute = reader.GetCustomAttribute(attributeHandle);
            if (IsCompilerGeneratedAttributeConstructor(reader, attribute.Constructor))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True if <paramref name="constructorHandle"/> — a <see cref="CustomAttribute.Constructor"/>
    /// handle — refers to <see cref="System.Runtime.CompilerServices.CompilerGeneratedAttribute"/>'s
    /// constructor. Always resolves through the <see cref="MemberReference"/> path in practice: the
    /// attribute is declared in the BCL, external to this assembly, exactly like the forbidden
    /// <c>System.IO</c> members <see cref="TryResolveForbiddenTargetType"/> resolves the same way.
    /// A <see cref="MethodDefinitionHandle"/> would mean this project declared its own type named
    /// <c>CompilerGeneratedAttribute</c>, which it does not.
    /// </summary>
    private static bool IsCompilerGeneratedAttributeConstructor(MetadataReader reader, EntityHandle constructorHandle)
    {
        if (constructorHandle.Kind != HandleKind.MemberReference)
            return false;

        MemberReference memberReference = reader.GetMemberReference((MemberReferenceHandle)constructorHandle);

        if (memberReference.Parent.Kind != HandleKind.TypeReference)
            return false;

        TypeReference typeReference = reader.GetTypeReference((TypeReferenceHandle)memberReference.Parent);
        string typeName = reader.GetString(typeReference.Name);
        string typeNamespace = reader.GetString(typeReference.Namespace);

        return string.Equals(typeNamespace, CompilerGeneratedAttributeNamespace, StringComparison.Ordinal) &&
               string.Equals(typeName, CompilerGeneratedAttributeName, StringComparison.Ordinal);
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

            // newobj (constructing a FileInfo/DirectoryInfo) is exactly as much a filesystem
            // access as call/callvirt is: "new FileInfo(path).Length" never emits a call to
            // anything named File/Directory/FileInfo/DirectoryInfo — the constructor invocation
            // itself is a newobj instruction, invisible to a scan that only looks at call and
            // callvirt. Resolving its target type uses the exact same MemberReference path as a
            // call to an instance method: a constructor reference is a MemberReference like any
            // other, just with a well-known name (".ctor").
            if (opcode == OpCodes.Call || opcode == OpCodes.Callvirt || opcode == OpCodes.Newobj)
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
        string typeNamespace = reader.GetString(typeReference.Namespace);

        // Namespace-qualify the match, not just the bare type name: an unrelated type that
        // happens to be called "File" or "Directory" outside System.IO (e.g. a domain model)
        // must not trip this check.
        if (!string.Equals(typeNamespace, ForbiddenNamespace, StringComparison.Ordinal) ||
            !ForbiddenTypeNames.Contains(typeName))
        {
            return null;
        }

        string memberName = reader.GetString(memberReference.Name);
        return $"{typeNamespace}.{typeName}.{memberName}";
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
