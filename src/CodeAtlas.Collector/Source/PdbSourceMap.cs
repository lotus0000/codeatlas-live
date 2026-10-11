using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeAtlas.Collector.Source;

public sealed record SourceLocation(
    bool Found, string? Reason, string? File, string? RelativePath, int DisplayStart, int DisplayEnd, int NavigationLine,
    IReadOnlyList<int> ExecutableLines, string? Kind, bool StateMachine, string? Mapping)
{
    public static SourceLocation Refused(string reason) => new(false, reason, null, null, 0, 0, 0, [], null, false, null);
}

/// <summary>
/// Maps a runtime method identity (assembly path + MVID + metadata token) to source through the assembly's Portable PDB.
/// Roslyn only widens the PDB location to the enclosing declaration. Refuses, with a reason, whenever the assembly, the
/// PDB or the file does not match: it never guesses.
/// </summary>
public sealed class PdbSourceMap(IReadOnlyList<string> roots) : IDisposable
{
    private static readonly Guid Sha256 = new("8829d00f-11b8-4213-878b-770e8597ac16");
    private static readonly Guid Sha1 = new("ff1816ec-aa5e-4d10-87f7-6f4963833460");
    private readonly Dictionary<string, (DateTime WrittenAt, Loaded? Value)> _cache = new();
    private readonly Lock _lock = new();
    private readonly string[] _roots = roots.Select(r => RealPath(r).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).ToArray();

    private sealed record Loaded(Guid Mvid, int MethodCount, PEReader Pe, MetadataReaderProvider Pdb, Dictionary<int, MethodDefinitionHandle> MoveNextByKickoff) : IDisposable
    {
        public void Dispose()
        {
            Pdb.Dispose();
            Pe.Dispose();
        }
    }

    public bool IsAllowed(string path)
    {
        string real;
        try { real = RealPath(path); }
        catch (Exception) { return false; }
        return _roots.Any(root => real.StartsWith(root, StringComparison.Ordinal));
    }

    /// <summary>
    /// The path with every symbolic link resolved, component by component (like realpath): a link anywhere in the path,
    /// not only the last part, is followed. Components that do not exist are kept as written.
    /// </summary>
    internal static string RealPath(string path, int depth = 0)
    {
        if (depth > 40) throw new IOException("Too many levels of symbolic links.");
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                next = RealPath(target.FullName, depth + 1);
            current = next;
        }
        return current;
    }

    public SourceLocation Resolve(string assemblyPath, Guid mvid, int token)
    {
        if (!IsAllowed(assemblyPath)) return SourceLocation.Refused("The assembly is outside the configured source roots.");
        if (!System.IO.File.Exists(assemblyPath)) return SourceLocation.Refused("The assembly file no longer exists.");
        var loaded = Load(assemblyPath);
        if (loaded is null) return SourceLocation.Refused("No Portable PDB matches this assembly (none found, or its PDB id differs).");
        if (loaded.Mvid != mvid) return SourceLocation.Refused("The assembly was rebuilt after this span was recorded (MVID differs). Repeat the request.");
        if (MetadataTokens.EntityHandle(token) is not { Kind: HandleKind.MethodDefinition } entity)
            return SourceLocation.Refused("The token is not a method definition.");
        var method = (MethodDefinitionHandle)entity;
        if (MetadataTokens.GetRowNumber(method) > loaded.MethodCount) return SourceLocation.Refused("The token is out of range for this assembly.");

        var pdb = loaded.Pdb.GetMetadataReader();
        var points = Visible(pdb.GetMethodDebugInformation(method));
        var stateMachine = false;
        if (points.Count == 0 && loaded.MoveNextByKickoff.TryGetValue(MetadataTokens.GetRowNumber(method), out var moveNext))
        {
            points = Visible(pdb.GetMethodDebugInformation(moveNext));
            stateMachine = true;
        }
        if (points.Count == 0) return SourceLocation.Refused("The PDB has no sequence points for this method (compiler-generated, abstract or extern).");

        var documents = points.Select(p => p.Document).Distinct().ToList();
        if (documents.Count != 1) return SourceLocation.Refused("The method's code spans several documents; refusing to pick one.");
        var document = pdb.GetDocument(documents[0]);
        var file = pdb.GetString(document.Name);
        if (!IsAllowed(file)) return SourceLocation.Refused("The source file is outside the configured source roots.");
        if (!System.IO.File.Exists(file))
            return SourceLocation.Refused(file.Replace('\\', '/').Contains("/obj/")
                ? "This code was produced by a source generator; it has no file on disk."
                : $"The source file from the PDB no longer exists: {file}");

        var bytes = System.IO.File.ReadAllBytes(file);
        var algorithm = pdb.GetGuid(document.HashAlgorithm);
        var actual = algorithm == Sha256 ? SHA256.HashData(bytes) : algorithm == Sha1 ? SHA1.HashData(bytes) : null;
        if (actual is null) return SourceLocation.Refused("Unknown checksum algorithm in the PDB; the file cannot be verified.");
        if (!actual.AsSpan().SequenceEqual(pdb.GetBlobBytes(document.Hash)))
            return SourceLocation.Refused("The source file changed after the build, so line numbers may be wrong. Rebuild to refresh.");

        var executable = points.Select(p => p.StartLine).Distinct().Order().ToList();
        var relative = Path.GetRelativePath(_roots.First(r => RealPath(file).StartsWith(r, StringComparison.Ordinal)), RealPath(file)).Replace('\\', '/');
        var declaration = Enclosing(bytes, points);
        return declaration is { } d
            ? new SourceLocation(true, null, file, relative, d.Start, d.End, d.Start, executable, d.Kind, stateMachine, "pdb + roslyn")
            : new SourceLocation(true, null, file, relative, executable[0], executable[^1], executable[0], executable, "method", stateMachine, "pdb only");
    }

    private Loaded? Load(string path)
    {
        var writtenAt = System.IO.File.GetLastWriteTimeUtc(path);
        lock (_lock)
        {
            if (_cache.TryGetValue(path, out var cached) && cached.WrittenAt == writtenAt) return cached.Value;
            // The replaced entry is not disposed: a lookup on another thread may still be reading it. It holds no file
            // handle (everything is read into memory), so the garbage collector frees it.
            var loaded = Open(path);
            _cache[path] = (writtenAt, loaded);
            return loaded;
        }
    }

    private static Loaded? Open(string path)
    {
        // Read into memory: no file stays open or mapped, so rebuilding the app (which rewrites these files) never
        // fails or races with a lookup.
        PEReader pe;
        try { pe = new PEReader(System.Collections.Immutable.ImmutableArray.Create(System.IO.File.ReadAllBytes(path))); }
        catch (IOException) { return null; }
        try
        {
            if (!pe.TryOpenAssociatedPortablePdb(path, p => System.IO.File.Exists(p) ? new MemoryStream(System.IO.File.ReadAllBytes(p)) : null, out var provider, out _) || provider is null)
            {
                pe.Dispose();
                return null;
            }
            var md = pe.GetMetadataReader();
            var pdb = provider.GetMetadataReader();
            var moveNext = new Dictionary<int, MethodDefinitionHandle>();
            foreach (var handle in pdb.MethodDebugInformation)
            {
                var kickoff = pdb.GetMethodDebugInformation(handle).GetStateMachineKickoffMethod();
                if (!kickoff.IsNil) moveNext[MetadataTokens.GetRowNumber(kickoff)] = handle.ToDefinitionHandle();
            }
            return new Loaded(md.GetGuid(md.GetModuleDefinition().Mvid), md.MethodDefinitions.Count, pe, provider, moveNext);
        }
        catch (Exception)
        {
            pe.Dispose();
            return null;
        }
    }

    private static List<SequencePoint> Visible(MethodDebugInformation debug) => debug.GetSequencePoints().Where(p => !p.IsHidden).ToList();

    // Innermost function-like syntax node containing every sequence point.
    private static (int Start, int End, string Kind)? Enclosing(byte[] bytes, List<SequencePoint> points)
    {
        var text = Microsoft.CodeAnalysis.Text.SourceText.From(bytes, bytes.Length, System.Text.Encoding.UTF8);
        var tree = CSharpSyntaxTree.ParseText(text);
        int Position(int line, int column) => text.Lines[line - 1].Start + column - 1;
        var positions = points.Select(p => Position(p.StartLine, p.StartColumn)).ToList();
        for (var node = tree.GetRoot().FindToken(positions[0]).Parent; node is not null; node = node.Parent)
        {
            var kind = node switch
            {
                AnonymousFunctionExpressionSyntax => "lambda",
                LocalFunctionStatementSyntax => "local function",
                ConstructorDeclarationSyntax => "constructor",
                BaseMethodDeclarationSyntax => "method",
                AccessorDeclarationSyntax => "accessor",
                PropertyDeclarationSyntax => "property",
                TypeDeclarationSyntax { ParameterList: not null } => "primary constructor",
                CompilationUnitSyntax => "top-level statements",
                _ => null,
            };
            if (kind is null || !positions.All(p => node.FullSpan.Contains(p))) continue;
            var span = node.GetLocation().GetLineSpan();
            return (span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1, kind);
        }
        return null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var (_, value) in _cache.Values) value?.Dispose();
            _cache.Clear();
        }
    }
}
