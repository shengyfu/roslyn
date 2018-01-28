using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Text.RegularExpressions;
using System.IO;
using Microsoft.CodeAnalysis.MSBuild;
using System.Threading;
using Newtonsoft.Json;
using System.Reflection;
using System.Configuration;

namespace PythiaMatrixBuilder
{
    class Program
    {
        const int OPEN_SOLUTION_TIME = 60; // in seconds
        const int COMPILE_PROJECT_TIME = 120; // seconds to Open and Compile
        const int NUGET_RESTORE_TIME = 240000; // 60000; // in milliseconds, for nuget restore to run for a solution
        const string OUTPUT_SUFFIX = "_usage.json";
        const string MEMBERS_MAP_SUFFIX = "_membersMap.json";
        static string OUTPUT_PREFIX = @"F:\UsageOutput_VS\";
        static string PREVIOUS_OUTPUT_PREFIX = @"F:\UsageOutput_VS\";
        static string MEMBERS_MAP_PREFIX = @"F:\MembersMapOutput_VS\";

        static int repoCount = 4716; // replace with largest count yet
        static List<string> solutionsAlreadyParsed;
        static string nugetInstallPath;


        //[Import(typeof(IVsPackageRestorer))]
        //public static IVsPackageRestorer packageRestorer;

        static void Main(string[] args)
        {
            // repo(s) path 
            string reposPath = args[0];

            string processAllSolutions = null;
            if (args.Length > 1)
            {
                processAllSolutions = args[1];
            }
            nugetInstallPath = ConfigurationSettings.AppSettings["NugetInstallPath"];
            OUTPUT_PREFIX = ConfigurationSettings.AppSettings["OutputPath"];
            PREVIOUS_OUTPUT_PREFIX = ConfigurationSettings.AppSettings["PreviousOutputPath"];
            MEMBERS_MAP_PREFIX = ConfigurationSettings.AppSettings["MembermapOutputPath"];
            CreateDirs();

            string[] repos = Directory.GetDirectories(reposPath);

            // get the list of solution names that are already parsed
            //solutionsAlreadyParsed = GetSolutionsAlreadyParsed();
            solutionsAlreadyParsed = new List<string>();

            // For each document, extract as a single matrix row, the methods used from the namespace

            Console.Write("repoIndex, repoPath, numSolutions, solutionUsed, success, numProjects, notes");

            // There should be one column per method type
            var counter = 0;
            foreach (string repoPath in repos)
            {
                Console.WriteLine();
                counter++;

                string[] solutions = Directory.GetFiles(repoPath, "*.sln", SearchOption.AllDirectories);

                Console.Write($"{counter.ToString()},{repoPath},{solutions.Length.ToString()},");

                // skip if there are no solution in the repo
                if (solutions.Length == 0)
                {
                    Console.Write($"NA,{false.ToString()},NA,zero solutions in repo,");
                    continue;
                }

                // if not processing 'all' solutions, only put the first solution path in the solutions queue
                if (processAllSolutions != "all")
                {
                    string solutionPath = solutions.First(); // TODO try parse a different solution if this solution fails to parse
                    solutions = new string[] { solutionPath };
                }

                foreach (string solutionPath in solutions)
                {
                    // skip if already parsed
                    string name = solutionPath.Split(Path.DirectorySeparatorChar).Last().Replace('.', '_') + OUTPUT_SUFFIX;
                    if (solutionsAlreadyParsed.Contains(name))
                    {
                        Console.Write($"{solutionPath},{true.ToString()},NA,already parsed,");
                        continue;
                    }

                    // process the solution for repos that contain at least one solution that's not already parsed
                    var result = ProcessSolution(repoPath, solutionPath);
                    Console.Write($"{solutionPath},{result.success.ToString()},{result.numProjects.ToString()},{result.note}");
                }
            }
        }

        private static void CreateDirs()
        {
            if (!Directory.Exists(PREVIOUS_OUTPUT_PREFIX))
            {
                Directory.CreateDirectory(PREVIOUS_OUTPUT_PREFIX);
            }

            if (!Directory.Exists(OUTPUT_PREFIX))
            {
                Directory.CreateDirectory(OUTPUT_PREFIX);
            }

            if (!Directory.Exists(MEMBERS_MAP_PREFIX))
            {
                Directory.CreateDirectory(MEMBERS_MAP_PREFIX);
            }
        }

        private static List<string> GetSolutionsAlreadyParsed()
        {
            var list = new List<string>();

            string[] outputPaths = Directory.GetFiles(PREVIOUS_OUTPUT_PREFIX, "*.json");
            foreach (string outputPath in outputPaths)
            {
                var fileName = outputPath.Split(Path.DirectorySeparatorChar).Last();
                var parts = fileName.Split('_');
                var solution = parts.Skip(1);
                list.Add(string.Join("_", solution));
            }

            return list;
        }

        private static (bool success, int numProjects, string note) ProcessSolution(string repoPath, string solutionPath)
        {
            bool success = true;
            int numProjects = 0;
            string note = "";

            MSBuildWorkspace msWorkspace;

            try
            {
                msWorkspace = MSBuildWorkspace.Create();
                Solution solution = null;
                try
                {
                    CancellationTokenSource cancellationToken = new CancellationTokenSource(TimeSpan.FromSeconds(OPEN_SOLUTION_TIME));
                    solution = msWorkspace.OpenSolutionAsync(solutionPath, cancellationToken.Token).Result;
                }
                catch (Exception e)
                {
                    return (false, 0, $"exception opening solution: {e}");
                }

                // if opening is successful, restore the solution
                var toRestore = true;

                if (toRestore)
                {
                    var processStartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        WorkingDirectory = repoPath,
                        WindowStyle = System.Diagnostics.ProcessWindowStyle.Normal,
                        FileName = nugetInstallPath,
                        Arguments = "restore " + solutionPath,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    var process = System.Diagnostics.Process.Start(processStartInfo);
                    if (!process.WaitForExit(NUGET_RESTORE_TIME))
                    {
                        process.Kill();
                        note += "Nuget restore timed out,";
                    }
                    else
                    {
                        note += "Nuget restore finished,";
                    }
                    StringBuilder sb = new StringBuilder();
                    sb.Append(process.StandardOutput.ReadToEnd());
                    sb.Append(process.StandardError.ReadToEnd());
                    note += $"{sb.ToString()},";
                }

                // initiate the storage structures
                List<UsageDataModel> usageData = new List<UsageDataModel>();
                Dictionary<string, List<string>> membersMap = new Dictionary<string, List<string>>();

                // iterate through solution
                numProjects = solution.Projects.Count();
                foreach (var project in solution.Projects)
                {
                    var currProjectPath = project.FilePath; // path to the .csproj

                    // Console.WriteLine("Reading Project Data :::: " + currProjectPath);

                    //var references = project.MetadataReferences;
                    //if (references != null && references.Count > 0)
                    //{
                    //    var versions = project.GetDependentSemanticVersionAsync().Result;
                    //    var versions2 = project.GetDependentVersionAsync().Result;
                    //    var outputFilePath = project.OutputFilePath;

                    //    InstallPackagesForProject(references, currProjectPath + "/packages");
                    //}

                    // packageRestorer.RestorePackages(project);

                    Compilation compilation;
                    CancellationTokenSource cancellationToken = new CancellationTokenSource(TimeSpan.FromSeconds(COMPILE_PROJECT_TIME));

                    try
                    {
                        //Project currProject = msWorkspace.OpenProjectAsync(currProjectPath, cancellationToken.Token).Result;

                        compilation = project.GetCompilationAsync(cancellationToken.Token).Result;
                    }
                    catch (Exception e)
                    {
                        success = false;
                        note += $"project {currProjectPath} cannot compile: {e},";
                        continue;
                    }

                    var compilationErrors = compilation.GetDiagnostics();

                    foreach (var currentDocument in project.Documents)
                    {
                        try
                        {
                            SyntaxTree tree = currentDocument.GetSyntaxTreeAsync().Result;
                            var res = ParseDocument(tree, compilation, currentDocument, ref membersMap);
                            if (res.Count > 0)
                            {
                                UsageDataModel data = new UsageDataModel
                                {
                                    Repo = repoPath,
                                    Solution = solutionPath,
                                    Project = project.Name,
                                    Document = currentDocument.Name,
                                    References = res
                                };
                                usageData.Add(data);
                            }

                        }
                        catch (Exception e)
                        {
                            success = false;
                            note += $"document {currentDocument.Name} cannot parse: {e},";
                        }
                    }
                }

                string solutionName = solutionPath.Split(Path.DirectorySeparatorChar).Last().Replace('.', '_');
                string usageDataPath = OUTPUT_PREFIX + repoCount + "_" + solutionName + OUTPUT_SUFFIX;
                string membersMapPath = MEMBERS_MAP_PREFIX + repoCount + "_" + solutionName + MEMBERS_MAP_SUFFIX;
                repoCount++;

                var json = JsonConvert.SerializeObject(usageData);
                File.WriteAllText(usageDataPath, json);
                json = JsonConvert.SerializeObject(membersMap);
                File.WriteAllText(membersMapPath, json);
            }
            catch (ReflectionTypeLoadException ex)
            {
                var sb = new StringBuilder();
                foreach (Exception exSub in ex.LoaderExceptions)
                {
                    sb.AppendLine(exSub.Message);
                    var exFileNotFound = exSub as FileNotFoundException;
                    if (exFileNotFound != null)
                    {
                        if (!string.IsNullOrEmpty(exFileNotFound.FusionLog))
                        {
                            sb.AppendLine("Fusion Log:");
                            sb.AppendLine(exFileNotFound.FusionLog);
                        }
                    }
                    sb.AppendLine();
                }
                string errorMessage = sb.ToString();
                success = false;
                note += $"exception processing solution: {errorMessage},";
            }
            catch (Exception e)
            {
                success = false;
                note += $"general exception processing solution: {e},";
            }

            return (success, numProjects, note);
        }

        //private static void InstallPackagesForProject(IReadOnlyList<MetadataReference> references, string installPath)
        //{
        //    IPackageRepository packageRepository = PackageRepositoryFactory.Default.CreateRepository("https://packages.nuget.org/api/v2");
        //    PackageManager packageManager = new PackageManager(packageRepository, installPath);

        //    foreach (var reference in references)
        //    {
        //        // download and unzip the package
        //        string packageId = reference.Display; // e.g. "EntityFramework"
        //        var packageVersion = reference.Properties; // e.g. "5.0.0"
        //        //packageManager.InstallPackage(packageId, SemanticVersion.Parse(packageVersion));
        //    }
        //}

        //private static void RestorePackagesForProject(Project project)
        //{
        //    Console.WriteLine("Restoring pacakges for " + project.ToString());

        //    packageRestorer.RestorePackages(project);
        //}

        private static bool CheckIfTokenInIfConditional(SyntaxNode ttp)
        {
            var ancestors = ttp.Ancestors();
            var isInIfConditional = false;

            var ifAncestors = ancestors.OfType<IfStatementSyntax>();

            if (ifAncestors.Count() > 0 && ifAncestors.First().Condition.Contains(ttp))
            {

                isInIfConditional = true;

            }
            return isInIfConditional;
        }

        public static Dictionary<string, Dictionary<string, MethodProperties>> ParseDocument(
            SyntaxTree tree, Compilation compilation, Document currentDocument,
            ref Dictionary<string, List<string>> membersMap)
        {
            var references = new Dictionary<string, Dictionary<string, MethodProperties>>();
            var root = (CompilationUnitSyntax)tree.GetRoot();

            var model = compilation.GetSemanticModel(tree);

            var diag = model.GetDiagnostics();

            // Filter to desired usings
            //var usings = root.Usings.Where(md => md.Name.GetText().ToString() == "System.Text");

            // get all the classes in this document
            var classes = root
                              .DescendantNodes()
                              .OfType<ClassDeclarationSyntax>();
            foreach (var clazz in classes)
            {
                // get all the property and method declaration blocks in this class
                var properties = clazz
                              .DescendantNodes()
                              .OfType<PropertyDeclarationSyntax>();

                var methods = clazz
                              .DescendantNodes()
                              .OfType<MethodDeclarationSyntax>();

                foreach (var containingMethod in methods)
                {
                    bool allSuccessful = ProcessNode(references, model, clazz, containingMethod, ref membersMap, currentDocument);
                }
                foreach (var property in properties)
                {
                    ProcessNode(references, model, clazz, property, ref membersMap, currentDocument);
                }
            }

            return references;
        }

        private static bool ProcessNode(Dictionary<string, Dictionary<string, MethodProperties>> references,
            SemanticModel model, ClassDeclarationSyntax clazz, MemberDeclarationSyntax containingNode,
            ref Dictionary<string, List<string>> membersMap, Document currentDocument)
        {
            bool allSuccessful = true;

            var invocations = containingNode
                                          .DescendantNodes()
                                          .OfType<MemberAccessExpressionSyntax>();

            foreach (var invocation in invocations)
            {
                try
                {
                    var symbolInfo = model.GetSymbolInfo(invocation);
                    var invocationSymbol = symbolInfo.Symbol;

                    if (invocationSymbol == null)
                    {
                        // Console.WriteLine("Null invocation symbol: " + invocation.ToFullString());
                        //Console.WriteLine("Failure reason:" + symbolInfo.CandidateReason.ToString());
                        //Console.WriteLine("");
                        allSuccessful = false;
                        continue;
                    }

                    var parentNode = invocation.Parent;
                    var inIfConditional = CheckIfTokenInIfConditional(parentNode);

                    ITypeSymbol invocationType = invocationSymbol.ContainingType;
                    ITypeSymbol methodInvocationType = invocationSymbol.ContainingType;

                    if (invocationSymbol is IMethodSymbol)
                    {
                        var mSymbol = (IMethodSymbol)invocationSymbol;
                        if (mSymbol.IsExtensionMethod)
                        {
                            invocationType = mSymbol.ReceiverType;
                        }
                    }


                    if (invocationType == null)
                    {
                        // Console.WriteLine("Null method Type: " + invocationSymbol.ToDisplayString());
                        allSuccessful = false;
                        continue;
                    }

                    var invocationTypeName = GetTypeFullName(invocationType);

                    var containingFunction = containingNode is MethodDeclarationSyntax
                        ? (string)((MethodDeclarationSyntax)containingNode).Identifier.Value
                        : (string)((PropertyDeclarationSyntax)containingNode).Identifier.Value;

                    var methodProps = new MethodProperties
                    {
                        containingClass = new List<string> { (string)clazz.Identifier.Value },
                        containingFunction = new List<string> { containingFunction },
                        isAbstract = new List<string> { invocationSymbol.IsAbstract.ToString() },
                        isDefinition = new List<string> { invocationSymbol.IsDefinition.ToString() },
                        isOverride = new List<string> { invocationSymbol.IsOverride.ToString() },
                        isExtern = new List<string> { invocationSymbol.IsExtern.ToString() },
                        isSealed = new List<string> { invocationSymbol.IsSealed.ToString() },
                        isStatic = new List<string> { invocationSymbol.IsStatic.ToString() },
                        isVirtual = new List<string> { invocationSymbol.IsVirtual.ToString() },
                        kind = new List<string> { GetInvocationKind(model, invocation) },
                        spanStart = new List<string> { invocation.SpanStart.ToString() },
                        typePM = new List<string> { invocationSymbol.Kind.ToString() },
                        isInIfConditional = new List<string> { inIfConditional.ToString() }
                    };

                    var methodPropertyName = GetSymbolName(invocationSymbol);

                    if (!references.ContainsKey(invocationTypeName))
                    {
                        references[invocationTypeName] = new Dictionary<string, MethodProperties>();
                    }

                    if (references[invocationTypeName].ContainsKey(methodPropertyName))
                    {
                        references[invocationTypeName][methodPropertyName].Append(methodProps);

                    }
                    else
                    {
                        references[invocationTypeName][methodPropertyName] = methodProps;
                    }

                    var members = GetTypeMembers(invocationType);

                    if (methodInvocationType != invocationType)
                    {
                        var extensionMembers = GetTypeMembers(methodInvocationType);
                        members = members.Union(extensionMembers);
                    }

                    if (!membersMap.ContainsKey(invocationTypeName))
                    {
                        membersMap[invocationTypeName] = members.ToList();
                    }
                    else
                    {
                        membersMap[invocationTypeName] = membersMap[invocationTypeName].Union(members).ToList();
                    }
                }
                catch (Exception e)
                {
                    // Console.WriteLine(e.ToString());
                    allSuccessful = false;
                }
            }
            return allSuccessful;
        }

        private static IEnumerable<string> GetTypeMembers(ITypeSymbol invocationType)
        {
            return invocationType
                            .GetMembers()
                            .Select(symbol => GetSymbolName(symbol))
                            .Distinct()
                            .Where(x => !x.Contains(".") && char.IsUpper(x[0])) // a valid method or property name does not contain "." and starts with an upper case letter
                            ;
        }

        public static string GetTypeFullName(ISymbol container)
        {
            return container.OriginalDefinition.ContainingSymbol.ToString() + "." + container.OriginalDefinition.Name;
        }
        public static string GetSymbolName(ISymbol symbol)
        {
            return symbol.OriginalDefinition.Name;
        }

        private static string GetInvocationKind(SemanticModel model, MemberAccessExpressionSyntax memberAccess)
        {
            try
            {
                var symbol = model.GetSymbolInfo(memberAccess.Expression).Symbol;
                var kind = symbol.Kind.ToString();
                return kind;
            }
            catch (Exception e)
            {
                // Console.WriteLine("memberAccess.Expression = " + memberAccess.Expression.ToFullString() + ", exception: " + e.ToString());
            }
            return "unknown";
        }
    }
}
