using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace CodeAtlas.Collector.Tests.Support;

/// <summary>Compiles C# source to a DLL with a side-by-side Portable PDB on disk, like a Debug build, and finds tokens.</summary>
internal sealed class FixtureAssembly
{
    public required string DllPath { get; init; }
    public required string SourcePath { get; init; }
    public required Guid Mvid { get; init; }
    private readonly List<(string Type, string Name, string? FirstParameter, int Token)> _methods = [];
    private readonly Dictionary<string, int> _types = [];

    public static string FixtureText => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/sourcemap/Fixture.cs.txt"));

    public static FixtureAssembly Build(string directory, string sourceText, string fileName = "Fixture.cs", bool crlf = false, bool bom = false,
        string assemblyName = "Fixture", string? sourcePathOverride = null)
    {
        Directory.CreateDirectory(directory);
        var text = crlf ? sourceText.Replace("\r\n", "\n").Replace("\n", "\r\n") : sourceText.Replace("\r\n", "\n");
        var bytes = (bom ? Encoding.UTF8.GetPreamble() : []).Concat(new UTF8Encoding(false).GetBytes(text)).ToArray();
        var sourcePath = sourcePathOverride ?? Path.Combine(directory, fileName);
        if (sourcePathOverride is null) File.WriteAllBytes(sourcePath, bytes);

        var source = SourceText.From(bytes, bytes.Length, Encoding.UTF8, SourceHashAlgorithm.Sha256, throwIfBinaryDetected: false, canBeEmbedded: true);
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), path: sourcePath);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create(assemblyName, [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug));

        var dllPath = Path.Combine(directory, assemblyName + ".dll");
        var pdbPath = Path.Combine(directory, assemblyName + ".pdb");
        using (var dll = File.Create(dllPath))
        using (var pdb = File.Create(pdbPath))
        {
            var result = compilation.Emit(dll, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb, pdbFilePath: pdbPath));
            if (!result.Success) throw new InvalidOperationException(string.Join('\n', result.Diagnostics));
        }

        using var pe = new PEReader(File.OpenRead(dllPath));
        var md = pe.GetMetadataReader();
        var fixture = new FixtureAssembly { DllPath = dllPath, SourcePath = sourcePath, Mvid = md.GetGuid(md.GetModuleDefinition().Mvid) };
        foreach (var typeHandle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(typeHandle);
            var typeName = FullName(md, type);
            fixture._types[typeName] = MetadataTokens.GetToken(typeHandle);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = md.GetMethodDefinition(methodHandle);
                var signature = method.DecodeSignature(new FirstParameterName(md), null);
                fixture._methods.Add((typeName, md.GetString(method.Name), signature.ParameterTypes.FirstOrDefault(), MetadataTokens.GetToken(methodHandle)));
            }
        }
        return fixture;
    }

    public int Token(string type, string method, string? firstParameter = null) =>
        _methods.Single(m => m.Type == type && m.Name == method && (firstParameter is null || m.FirstParameter == firstParameter)).Token;

    public int ConstructorToken(string type) => Token(type, ".ctor");

    public int LambdaToken(string containingMethod) =>
        _methods.Single(m => m.Name.StartsWith($"<{containingMethod}>b__", StringComparison.Ordinal)).Token;

    public int LocalFunctionToken(string name) => _methods.Single(m => m.Name.Contains($">g__{name}|", StringComparison.Ordinal)).Token;

    public int TypeToken(string type) => _types[type];

    private static string FullName(MetadataReader md, TypeDefinition type)
    {
        var name = md.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil) return $"{FullName(md, md.GetTypeDefinition(declaring))}+{name}";
        var ns = md.GetString(type.Namespace);
        return ns.Length == 0 ? name : $"{ns}.{name}";
    }

    private sealed class FirstParameterName(MetadataReader md) : ISignatureTypeProvider<string, object?>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => md.GetString(md.GetTypeDefinition(handle).Name);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => md.GetString(md.GetTypeReference(handle).Name);
        public string GetTypeFromSpecification(MetadataReader reader, object? context, TypeSpecificationHandle handle, byte rawTypeKind) => "spec";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
        public string GetByReferenceType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType;
        public string GetGenericMethodParameter(object? context, int index) => $"M{index}";
        public string GetGenericTypeParameter(object? context, int index) => $"T{index}";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
    }
}
