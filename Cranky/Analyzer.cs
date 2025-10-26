using Buildalyzer;
using Cranky.Output;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NuGet.Frameworks;

namespace Cranky;

internal class Analyzer(IReadOnlyCollection<FileSystemInfo> projectFiles, IOutput output, bool buildLogging)
{
    public async Task<AnalyzerResult> AnalyzeAsync(CancellationToken cancellationToken = default)
    {
        var totalProjects = projectFiles.Count;
        var reportedProjects = 0;

        var total = 0;
        var undocumented = 0;

        Dictionary<string, int> perFilePercentages = new();
        foreach (var projectFile in projectFiles)
        {
            var projectFilePath = projectFile.FullName.Replace("\\", "/").Replace("/", Path.DirectorySeparatorChar.ToString());

            output.OpenGroup("Analyzing project: " + projectFilePath);

            var sourceFiles = GetSourceFiles(projectFilePath, cancellationToken).ToList();
            var totalFiles = sourceFiles.Count;

            foreach (var sourceFile in sourceFiles)
            {
                if (!sourceFile.Exists)
                {
                    totalFiles--;

                    continue;
                }

                var result = await AnalyzeFileAsync(sourceFile, cancellationToken);
                var key = Path.GetRelativePath(Directory.GetCurrentDirectory(), sourceFile.FullName);
                var pct = 100;
                if (result.UndocumentedMembers.Count > 0)
                {
                    pct = (int)((1.0 - ((double)result.UndocumentedMembers.Count / result.PublicMembers.Count)) * 100);
                }

                if (!perFilePercentages.TryAdd(key, pct))
                {
                    perFilePercentages[key] = pct;
                }
                
                total += result.PublicMembers.Count;
                undocumented += result.UndocumentedMembers.Count;
            }

            if (totalFiles == 0)
                output.WriteWarning("No source files analyzed.");

            output.CloseGroup();

            output.SetProgress(totalProjects, ++reportedProjects);
        }

        if (totalProjects != reportedProjects)
            output.SetProgress(totalProjects, totalProjects);

        var outputResult = new AnalyzerResult(total, undocumented)
        {
            PerFilePercentage = perFilePercentages
        };
        return outputResult;
    }

    private IEnumerable<FileSystemInfo> GetSourceFiles(string projectFilePath, CancellationToken cancellationToken = default)
    {;
        // cause load of NuGet.Framework DLL
        _ = NuGetFramework.AnyFramework;

        var manager = new AnalyzerManager();
        var analyzer = manager.GetProject(projectFilePath);

        if (buildLogging)
            analyzer.AddBuildLogger(new BuildLogger(output));

        var results = analyzer.Build();
        foreach (var result in results)
        {
            foreach (var sourceFile in result.SourceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                yield return new FileInfo(sourceFile);
            }
        }
    }

    private async Task<MemberAnalysisResult> AnalyzeFileAsync(FileSystemInfo file, CancellationToken cancellationToken = default)
    {
        output.WriteDebug("Analyzing file: " + file.FullName);

        // 1. parse source file
        var text = await File.ReadAllTextAsync(file.FullName, cancellationToken);
        var tree = CSharpSyntaxTree.ParseText(text, cancellationToken: cancellationToken);
        var root = tree.GetCompilationUnitRoot(cancellationToken);

        var publicMembers = new List<MemberDeclarationSyntax>();
        var publicMembersWithoutDocumentation = new List<MemberDeclarationSyntax>();

        var publicRoots = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(tds => tds.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)));
        foreach (var publicRoot in publicRoots)
        {
            output.WriteDebug($"  Analyzing public type: {publicRoot.Identifier.Text}");
            // 2. get public api
            var detectedPublicMembers = publicRoot.DescendantNodes()
                .OfType<MemberDeclarationSyntax>()
                .Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword) || m.Modifiers.Any(SyntaxKind.ProtectedKeyword))
                .ToList();

            // 3. get api documentation
            var detectedPublicMembersWithoutDocumentation = detectedPublicMembers
                .Where(m => !m.HasLeadingTrivia|| !m.GetLeadingTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)))
                .ToList();

            publicMembers.AddRange(detectedPublicMembers);
            publicMembersWithoutDocumentation.AddRange(detectedPublicMembersWithoutDocumentation);

            output.WriteDebug($"  Total API Members: {detectedPublicMembers.Count}");
            output.WriteDebug($"  Undocumented Members: {detectedPublicMembersWithoutDocumentation.Count}");
        }

        return new(publicMembers, publicMembersWithoutDocumentation);
    }
}
