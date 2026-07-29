using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeIndex.Core.Chunking;

/// <summary>
/// Splits a C# file into one chunk per type declaration plus one chunk per indexable
/// member (methods, constructors, properties, indexers, operators, conversion operators,
/// event fields, events with explicit add/remove accessors, delegates, and const/static-readonly
/// fields). Syntax-only — no semantic model, no compilation — so this stays fast and needs no
/// project references to run.
/// </summary>
public sealed partial class RoslynChunker
{
    public IReadOnlyList<CodeChunk> Chunk(string filePath, string sourceText)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(sourceText);
        CompilationUnitSyntax root = tree.GetCompilationUnitRoot();

        List<CodeChunk> chunks = new();

        foreach (BaseTypeDeclarationSyntax typeDeclaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            // C# 14 extension blocks are TypeDeclarationSyntax nodes with no name of their
            // own (Identifier is empty). They exist only to group extension members, so they
            // never get a type chunk — but their members are still indexed below, walking up
            // through them without contributing an empty segment to the qualified symbol.
            if (typeDeclaration.Identifier.Text.Length > 0)
            {
                chunks.Add(CreateTypeChunk(filePath, typeDeclaration));
            }

            if (typeDeclaration is not TypeDeclarationSyntax typeWithMembers)
            {
                continue;
            }

            foreach (MemberDeclarationSyntax member in typeWithMembers.Members)
            {
                // Nested types (including extension blocks) are visited independently by the
                // outer walk above; visiting them here too would produce duplicate chunks.
                if (member is BaseTypeDeclarationSyntax)
                {
                    continue;
                }

                chunks.AddRange(CreateMemberChunks(filePath, member));
            }
        }

        // Delegates are types, but Roslyn does not model them as BaseTypeDeclarationSyntax,
        // so they never appear in the walk above (nested or top-level). A single pass over
        // the whole tree finds both cases without risking a double chunk.
        foreach (DelegateDeclarationSyntax delegateDeclaration in root.DescendantNodes().OfType<DelegateDeclarationSyntax>())
        {
            chunks.Add(CreateDelegateChunk(filePath, delegateDeclaration));
        }

        return chunks;
    }

    private static CodeChunk CreateTypeChunk(string filePath, BaseTypeDeclarationSyntax typeDeclaration)
    {
        ChunkKind kind = GetTypeKind(typeDeclaration);
        string symbol = BuildSymbol(typeDeclaration, typeDeclaration.Identifier.Text);
        string signature = BuildTypeSignature(typeDeclaration, kind);
        string docComment = GetDocComment(typeDeclaration);
        string body = string.Join(", ", GetMemberNames(typeDeclaration));
        (int startLine, int endLine) = GetLineRange(typeDeclaration);

        return new CodeChunk
        {
            FilePath = filePath,
            StartLine = startLine,
            EndLine = endLine,
            Kind = kind,
            Symbol = symbol,
            Signature = signature,
            DocComment = docComment,
            EmbedText = BuildEmbedText(filePath, symbol, kind, signature, docComment, body),
        };
    }

    private static IEnumerable<CodeChunk> CreateMemberChunks(string filePath, MemberDeclarationSyntax member)
    {
        switch (member)
        {
            case MethodDeclarationSyntax method:
                yield return CreateSimpleMemberChunk(filePath, method, ChunkKind.Method, method.Identifier.Text, BuildMethodSignature(method));
                break;

            case ConstructorDeclarationSyntax constructor:
                yield return CreateSimpleMemberChunk(filePath, constructor, ChunkKind.Constructor, constructor.Identifier.Text, BuildConstructorSignature(constructor));
                break;

            case PropertyDeclarationSyntax property:
                yield return CreateSimpleMemberChunk(filePath, property, ChunkKind.Property, property.Identifier.Text, BuildPropertySignature(property));
                break;

            case OperatorDeclarationSyntax operatorDeclaration:
            {
                string name = "operator " + operatorDeclaration.OperatorToken.Text;
                yield return CreateSimpleMemberChunk(filePath, operatorDeclaration, ChunkKind.Method, name, BuildOperatorSignature(operatorDeclaration));
                break;
            }

            case ConversionOperatorDeclarationSyntax conversion:
            {
                string name = conversion.ImplicitOrExplicitKeyword.Text + " operator " + conversion.Type;
                yield return CreateSimpleMemberChunk(filePath, conversion, ChunkKind.Method, name, BuildConversionOperatorSignature(conversion));
                break;
            }

            case IndexerDeclarationSyntax indexer:
                yield return CreateSimpleMemberChunk(filePath, indexer, ChunkKind.Property, "this[]", BuildIndexerSignature(indexer));
                break;

            case EventFieldDeclarationSyntax eventField:
                foreach (CodeChunk chunk in CreateFieldLikeChunks(filePath, eventField, eventField.Declaration, eventField.Modifiers, "event ", ChunkKind.Field))
                {
                    yield return chunk;
                }

                break;

            case EventDeclarationSyntax eventDeclaration:
                yield return CreateSimpleMemberChunk(
                    filePath, eventDeclaration, ChunkKind.Field, eventDeclaration.Identifier.Text,
                    BuildEventSignature(eventDeclaration));
                break;

            case FieldDeclarationSyntax field when IsIndexableField(field):
                foreach (CodeChunk chunk in CreateFieldLikeChunks(filePath, field, field.Declaration, field.Modifiers, string.Empty, ChunkKind.Field))
                {
                    yield return chunk;
                }

                break;
        }
    }

    private static CodeChunk CreateSimpleMemberChunk(string filePath, MemberDeclarationSyntax member, ChunkKind kind, string name, string signature)
    {
        string symbol = BuildSymbol(member, name);
        string docComment = GetDocComment(member);
        string body = member.ToString();
        (int startLine, int endLine) = GetLineRange(member);

        return new CodeChunk
        {
            FilePath = filePath,
            StartLine = startLine,
            EndLine = endLine,
            Kind = kind,
            Symbol = symbol,
            Signature = signature,
            DocComment = docComment,
            EmbedText = BuildEmbedText(filePath, symbol, kind, signature, docComment, body),
        };
    }

    private static IEnumerable<CodeChunk> CreateFieldLikeChunks(
        string filePath,
        MemberDeclarationSyntax ownerNode,
        VariableDeclarationSyntax declaration,
        SyntaxTokenList modifiers,
        string keywordPrefix,
        ChunkKind kind)
    {
        string modifierPrefix = BuildModifierPrefix(modifiers);
        string typeText = declaration.Type.ToString();
        string docComment = GetDocComment(ownerNode);
        string body = ownerNode.ToString();
        (int startLine, int endLine) = GetLineRange(ownerNode);

        foreach (VariableDeclaratorSyntax declarator in declaration.Variables)
        {
            string name = declarator.Identifier.Text;
            string symbol = BuildSymbol(ownerNode, name);
            string signature = $"{modifierPrefix}{keywordPrefix}{typeText} {name}";

            yield return new CodeChunk
            {
                FilePath = filePath,
                StartLine = startLine,
                EndLine = endLine,
                Kind = kind,
                Symbol = symbol,
                Signature = signature,
                DocComment = docComment,
                EmbedText = BuildEmbedText(filePath, symbol, kind, signature, docComment, body),
            };
        }
    }

    private static CodeChunk CreateDelegateChunk(string filePath, DelegateDeclarationSyntax delegateDeclaration)
    {
        string modifierPrefix = BuildModifierPrefix(delegateDeclaration.Modifiers);
        string typeParameters = delegateDeclaration.TypeParameterList?.ToString() ?? string.Empty;
        string parameters = BuildParameterList(delegateDeclaration.ParameterList.Parameters);
        string signature = $"{modifierPrefix}delegate {delegateDeclaration.ReturnType} {delegateDeclaration.Identifier.Text}{typeParameters}({parameters})";

        const ChunkKind kind = ChunkKind.Method;
        string symbol = BuildSymbol(delegateDeclaration, delegateDeclaration.Identifier.Text);
        string docComment = GetDocComment(delegateDeclaration);
        string body = delegateDeclaration.ToString();
        (int startLine, int endLine) = GetLineRange(delegateDeclaration);

        return new CodeChunk
        {
            FilePath = filePath,
            StartLine = startLine,
            EndLine = endLine,
            Kind = kind,
            Symbol = symbol,
            Signature = signature,
            DocComment = docComment,
            EmbedText = BuildEmbedText(filePath, symbol, kind, signature, docComment, body),
        };
    }

    private static bool IsIndexableField(FieldDeclarationSyntax field)
    {
        bool isConst = field.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword));
        bool isStaticReadonly = field.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)) &&
            field.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword));

        return isConst || isStaticReadonly;
    }

    private static ChunkKind GetTypeKind(BaseTypeDeclarationSyntax typeDeclaration) => typeDeclaration switch
    {
        RecordDeclarationSyntax => ChunkKind.Record,
        ClassDeclarationSyntax => ChunkKind.Class,
        InterfaceDeclarationSyntax => ChunkKind.Interface,
        StructDeclarationSyntax => ChunkKind.Struct,
        EnumDeclarationSyntax => ChunkKind.Enum,
        _ => ChunkKind.Unknown,
    };

    /// <summary>
    /// Builds a fully-qualified name for <paramref name="name"/> by walking up from
    /// <paramref name="node"/>'s parent chain, collecting enclosing type names (innermost
    /// last) and the enclosing namespace, whether block-scoped or file-scoped. Anonymous
    /// ancestors (C# 14 extension blocks) contribute no segment of their own.
    /// </summary>
    private static string BuildSymbol(SyntaxNode node, string name)
    {
        List<string> typeNames = new();
        List<string> namespaceParts = new();
        SyntaxNode? current = node.Parent;

        while (current is not null)
        {
            switch (current)
            {
                case BaseTypeDeclarationSyntax { Identifier.Text.Length: > 0 } typeDeclaration:
                    typeNames.Insert(0, typeDeclaration.Identifier.Text);
                    break;
                case BaseNamespaceDeclarationSyntax namespaceDeclaration:
                    namespaceParts.Insert(0, namespaceDeclaration.Name.ToString());
                    break;
            }

            current = current.Parent;
        }

        typeNames.Add(name);
        string qualifiedName = string.Join(".", typeNames);
        string namespaceName = string.Join(".", namespaceParts);

        return namespaceName.Length == 0 ? qualifiedName : namespaceName + "." + qualifiedName;
    }

    private static (int StartLine, int EndLine) GetLineRange(SyntaxNode node)
    {
        FileLinePositionSpan lineSpan = node.SyntaxTree!.GetLineSpan(node.Span);
        return (lineSpan.StartLinePosition.Line + 1, lineSpan.EndLinePosition.Line + 1);
    }

    private static string BuildTypeSignature(BaseTypeDeclarationSyntax typeDeclaration, ChunkKind kind)
    {
        string modifiers = string.Join(" ", typeDeclaration.Modifiers.Select(m => m.Text));
        string keyword = BuildTypeKeyword(typeDeclaration, kind);
        string typeParameters = typeDeclaration is TypeDeclarationSyntax { TypeParameterList: { } typeParameterList }
            ? typeParameterList.ToString()
            : string.Empty;

        string prefix = modifiers.Length == 0 ? keyword : modifiers + " " + keyword;
        return $"{prefix} {typeDeclaration.Identifier.Text}{typeParameters}";
    }

    private static string BuildTypeKeyword(BaseTypeDeclarationSyntax typeDeclaration, ChunkKind kind)
    {
        if (kind == ChunkKind.Record && typeDeclaration is RecordDeclarationSyntax record)
        {
            return record.ClassOrStructKeyword.IsKind(SyntaxKind.None)
                ? "record"
                : "record " + record.ClassOrStructKeyword.Text;
        }

        return kind switch
        {
            ChunkKind.Class => "class",
            ChunkKind.Interface => "interface",
            ChunkKind.Struct => "struct",
            ChunkKind.Enum => "enum",
            _ => string.Empty,
        };
    }

    private static string BuildMethodSignature(MethodDeclarationSyntax method)
    {
        string modifiers = BuildModifierPrefix(method.Modifiers);
        string typeParameters = method.TypeParameterList?.ToString() ?? string.Empty;
        string parameters = BuildParameterList(method.ParameterList.Parameters);
        return $"{modifiers}{method.ReturnType} {method.Identifier.Text}{typeParameters}({parameters})";
    }

    private static string BuildConstructorSignature(ConstructorDeclarationSyntax constructor)
    {
        string modifiers = BuildModifierPrefix(constructor.Modifiers);
        string parameters = BuildParameterList(constructor.ParameterList.Parameters);
        return $"{modifiers}{constructor.Identifier.Text}({parameters})";
    }

    private static string BuildPropertySignature(PropertyDeclarationSyntax property)
    {
        string modifiers = BuildModifierPrefix(property.Modifiers);
        return $"{modifiers}{property.Type} {property.Identifier.Text}";
    }

    private static string BuildOperatorSignature(OperatorDeclarationSyntax operatorDeclaration)
    {
        string modifiers = BuildModifierPrefix(operatorDeclaration.Modifiers);
        string parameters = BuildParameterList(operatorDeclaration.ParameterList.Parameters);
        return $"{modifiers}{operatorDeclaration.ReturnType} operator {operatorDeclaration.OperatorToken.Text}({parameters})";
    }

    private static string BuildConversionOperatorSignature(ConversionOperatorDeclarationSyntax conversion)
    {
        string modifiers = BuildModifierPrefix(conversion.Modifiers);
        string parameters = BuildParameterList(conversion.ParameterList.Parameters);
        return $"{modifiers}{conversion.ImplicitOrExplicitKeyword.Text} operator {conversion.Type}({parameters})";
    }

    private static string BuildIndexerSignature(IndexerDeclarationSyntax indexer)
    {
        string modifiers = BuildModifierPrefix(indexer.Modifiers);
        string parameters = BuildParameterList(indexer.ParameterList.Parameters);
        return $"{modifiers}{indexer.Type} this[{parameters}]";
    }

    private static string BuildEventSignature(EventDeclarationSyntax eventDeclaration)
    {
        string modifiers = BuildModifierPrefix(eventDeclaration.Modifiers);
        return $"{modifiers}event {eventDeclaration.Type} {eventDeclaration.Identifier.Text}";
    }

    private static string BuildModifierPrefix(SyntaxTokenList modifiers)
    {
        string text = string.Join(" ", modifiers.Select(m => m.Text));
        return text.Length == 0 ? string.Empty : text + " ";
    }

    private static string BuildParameterList(SeparatedSyntaxList<ParameterSyntax> parameters) =>
        string.Join(", ", parameters.Select(BuildParameterText));

    private static string BuildParameterText(ParameterSyntax parameter)
    {
        string prefix = BuildModifierPrefix(parameter.Modifiers);
        return $"{prefix}{parameter.Type} {parameter.Identifier.Text}";
    }

    private static IEnumerable<string> GetMemberNames(BaseTypeDeclarationSyntax typeDeclaration)
    {
        if (typeDeclaration is EnumDeclarationSyntax enumDeclaration)
        {
            return enumDeclaration.Members.Select(m => m.Identifier.Text);
        }

        if (typeDeclaration is TypeDeclarationSyntax typeWithMembers)
        {
            return typeWithMembers.Members.SelectMany(GetMemberDisplayNames);
        }

        return [];
    }

    private static IEnumerable<string> GetMemberDisplayNames(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax method => [method.Identifier.Text],
        ConstructorDeclarationSyntax constructor => [constructor.Identifier.Text],
        PropertyDeclarationSyntax property => [property.Identifier.Text],
        FieldDeclarationSyntax field => field.Declaration.Variables.Select(v => v.Identifier.Text),
        EventFieldDeclarationSyntax eventField => eventField.Declaration.Variables.Select(v => v.Identifier.Text),
        EventDeclarationSyntax eventDeclaration => [eventDeclaration.Identifier.Text],
        IndexerDeclarationSyntax => ["this[]"],
        OperatorDeclarationSyntax op => [$"operator {op.OperatorToken.Text}"],
        ConversionOperatorDeclarationSyntax conv => [$"{conv.ImplicitOrExplicitKeyword.Text} operator {conv.Type}"],
        DelegateDeclarationSyntax nestedDelegate => [nestedDelegate.Identifier.Text],
        BaseTypeDeclarationSyntax { Identifier.Text.Length: > 0 } nested => [nested.Identifier.Text],
        _ => [],
    };

    private static string GetDocComment(SyntaxNode node)
    {
        foreach (SyntaxTrivia trivia in node.GetLeadingTrivia())
        {
            if (trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            {
                return CleanDocComment(trivia.ToFullString());
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Strips comment markers (<c>///</c>, <c>/**</c>, <c>*/</c>, leading <c>*</c>) and all
    /// XML tags, keeping the tags' text content (e.g. <c>&lt;param&gt;The a.&lt;/param&gt;</c>
    /// becomes <c>The a.</c>). Malformed markup is tolerated: this feeds an embedding model,
    /// not an XML parser.
    /// </summary>
    private static string CleanDocComment(string raw)
    {
        string normalised = raw.Replace("\r\n", "\n", StringComparison.Ordinal);
        List<string> lines = new();

        foreach (string rawLine in normalised.Split('\n'))
        {
            string line = rawLine.Trim();

            if (line.EndsWith("*/", StringComparison.Ordinal))
            {
                line = line[..^2].TrimEnd();
            }

            if (line.StartsWith("///", StringComparison.Ordinal))
            {
                line = line[3..].Trim();
            }
            else if (line.StartsWith("/**", StringComparison.Ordinal))
            {
                line = line[3..].Trim();
            }
            else if (line.StartsWith("*", StringComparison.Ordinal))
            {
                line = line[1..].Trim();
            }

            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        string joined = string.Join(" ", lines);
        string withoutTags = XmlTagPattern().Replace(joined, " ");
        return WhitespacePattern().Replace(withoutTags, " ").Trim();
    }

    private static string BuildEmbedText(string filePath, string symbol, ChunkKind kind, string signature, string docComment, string body)
    {
        StringBuilder builder = new();
        builder.Append("File: ").Append(filePath).Append('\n');
        builder.Append("Symbol: ").Append(symbol).Append('\n');
        builder.Append("Kind: ").Append(kind.ToString()).Append('\n');
        builder.Append("Signature: ").Append(signature).Append('\n');

        if (!string.IsNullOrEmpty(docComment))
        {
            builder.Append("Doc: ").Append(docComment).Append('\n');
        }

        builder.Append("Code:\n");
        builder.Append(ChunkTextLimits.Truncate(body, ChunkTextLimits.MaxBodyLength));

        return builder.ToString();
    }

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex XmlTagPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
