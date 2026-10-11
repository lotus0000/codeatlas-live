using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using CodeAtlas.Collector.Source;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeAtlas.Collector.Tests;

/// <summary>
/// Spec §10: every user-written method in the two samples maps to source, and named methods land on their own
/// declaration line (0 wrong). Methods are read from the samples' real Debug builds and their Portable PDBs.
/// </summary>
public class SampleSourceMapTests
{
#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    public static TheoryData<string> Samples => new() { "MinimalApiSample", "ControllerSample" };

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CodeAtlas.Live.slnx"))) return dir.FullName;
        throw new InvalidOperationException("CodeAtlas.Live.slnx not found.");
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Every_user_written_method_maps_to_its_own_declaration(string sample)
    {
        var sampleRoot = Path.Combine(RepoRoot(), "samples", sample);
        var dll = Path.Combine(sampleRoot, "bin", Configuration, "net10.0", sample + ".dll");
        Assert.True(File.Exists(dll), $"{dll} is missing: the sample was not built.");

        using var pe = new PEReader(File.OpenRead(dll));
        var metadata = pe.GetMetadataReader();
        var mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        using var map = new PdbSourceMap([sampleRoot]);
        // "User-written" = the PDB has debug information for it: its own sequence points, or it is the kickoff method of
        // an async/iterator state machine. Compiler-synthesized members (implicit Program constructor, the sync wrapper
        // around an async Main) have none and are rightly refused.
        using var pdbProvider = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(Path.ChangeExtension(dll, ".pdb")));
        var pdb = pdbProvider.GetMetadataReader();
        var kickoffs = pdb.MethodDebugInformation
            .Select(h => pdb.GetMethodDebugInformation(h).GetStateMachineKickoffMethod())
            .Where(k => !k.IsNil)
            .ToHashSet();
        bool HasDebugInfo(MethodDefinitionHandle handle) =>
            kickoffs.Contains(handle) || pdb.GetMethodDebugInformation(handle).GetSequencePoints().Any(p => !p.IsHidden);

        var failures = new List<string>();
        var checkedNamed = 0;
        foreach (var typeHandle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(typeHandle);
            var typeName = metadata.GetString(type.Name);
            // Compiler-generated types (state machines, closures) are reached through their user-written method.
            if (typeName.StartsWith('<') || IsCompilerGenerated(metadata, type.GetCustomAttributes())) continue;

            foreach (var methodHandle in type.GetMethods())
            {
                var method = metadata.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0 || IsCompilerGenerated(metadata, method.GetCustomAttributes()) || !HasDebugInfo(methodHandle)) continue;
                var name = metadata.GetString(method.Name);
                var token = MetadataTokens.GetToken(methodHandle);
                var label = $"{typeName}.{name} (0x{token:x8})";

                var location = map.Resolve(dll, mvid, token);
                if (!location.Found)
                {
                    failures.Add($"{label}: not mapped — {location.Reason}");
                    continue;
                }
                if (name.StartsWith('<')) continue; // e.g. <Main>$ for top-level statements: mapped is all we can check.

                var expected = DeclarationLines(location.File!, name, typeName);
                if (!expected.Contains(location.NavigationLine))
                    failures.Add($"{label}: line {location.NavigationLine}, expected one of [{string.Join(", ", expected)}] in {location.File}");
                checkedNamed++;
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.True(checkedNamed >= 4, $"Only {checkedNamed} named methods were checked in {sample}; the scan is not finding the sample's code.");
    }

    private static bool IsCompilerGenerated(MetadataReader metadata, CustomAttributeHandleCollection attributes) =>
        attributes.Select(metadata.GetCustomAttribute).Any(a =>
            a.Constructor.Kind == HandleKind.MemberReference
            && metadata.GetMemberReference((MemberReferenceHandle)a.Constructor).Parent is { Kind: HandleKind.TypeReference } parent
            && metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)parent).Name) == "CompilerGeneratedAttribute");

    /// <summary>Start lines of every declaration in the file that a method with this metadata name can come from.</summary>
    private static HashSet<int> DeclarationLines(string file, string name, string typeName)
    {
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();
        int Line(Microsoft.CodeAnalysis.SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var nodes = name switch
        {
            // A declared constructor, a primary constructor, or (implicit constructor) the type whose initializers it runs.
            ".ctor" or ".cctor" => root.DescendantNodes().Where(n =>
                (n is ConstructorDeclarationSyntax c && c.Identifier.Text == typeName)
                || (n is TypeDeclarationSyntax t && t.Identifier.Text == typeName)),
            _ when name.StartsWith("get_") || name.StartsWith("set_") => root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
                .Where(p => p.Identifier.Text == name[4..])
                .SelectMany(p => new Microsoft.CodeAnalysis.SyntaxNode[] { p }.Concat(p.AccessorList?.Accessors ?? [])),
            _ => root.DescendantNodes().Where(n =>
                (n is MethodDeclarationSyntax m && m.Identifier.Text == name)
                || (n is LocalFunctionStatementSyntax l && l.Identifier.Text == name)),
        };
        return nodes.Select(Line).ToHashSet();
    }
}
