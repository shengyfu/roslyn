// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Extensions.ContextQuery;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;
using Microsoft.CodeAnalysis.Completion.Providers;
using System.IO;
using System;
using Microsoft.CodeAnalysis.CSharp.Recommendations;
using System.Text.RegularExpressions;
using System.Diagnostics;
using Newtonsoft.Json;

namespace Microsoft.CodeAnalysis.CSharp.Completion.Providers
{
    internal partial class PythiaCompletionProvider : CommonCompletionProvider
    {
        private const string MODELS_PATH = @"C:\Users\yasiyu\Source\Repos\roslyn\models\"; // @"..\..\..\..\roslyn\models\";

        // global models
        private const string SCORING_MODEL_CSV_PATH = MODELS_PATH + @"model-all.tsv";
        private const string SCORING_MODEL_JSON_PATH = MODELS_PATH + @"model-all.json";

        private const string POPULARITY_MODEL_CSV_PATH = MODELS_PATH + @"freqs-new2.txt";
        private const string POPULARITY_MODEL_JSON_PATH = MODELS_PATH + @"model-frequency.json";

        // personalized models (hardcoded for the botBuilder project currently)
        private const string PROJECT_SCORING_MODEL_CSV_PATH = MODELS_PATH + @"botBuilder-model-all.tsv";
        private const string PROJECT_SCORING_MODEL_JSON_PATH = MODELS_PATH + @"botBuilder-model-all.json";

        private const string PROJECT_POPULARITY_MODEL_CSV_PATH = MODELS_PATH + @"botBuilder-model-frequency.txt";
        private const string PROJECT_POPULARITY_MODEL_JSON_PATH = MODELS_PATH + @"botBuilder-model-frequency.json";

        private static readonly SymbolDisplayFormat SYMBOL_DISPLAY_FORMAT = new SymbolDisplayFormat(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);

        private Dictionary<string, IEnumerable<string[]>> scoringModel; // model based on method co-occurrence in crawled repos
        private Dictionary<string, int> popularityModel; // model based on frequency of occurrences in crawled repos

        private Dictionary<string, IEnumerable<string[]>> projectScoringModel;


        private void BuildScoringModel(string inPath, string outPath)
        {
            // Read and parse model file
            var lines = File.ReadLines(inPath)
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Select(line => line.Split('\t'));

            var modelTemp = new Dictionary<string, IEnumerable<string[]>>();
            for (var i = 0; i < lines.Count(); i = i + 6)
            {
                Debug.WriteLineIf(i % 100 == 0, "Read line " + i);
                var className = lines.ElementAt(i)[0];
                var methodNames = lines.ElementAt(i + 1);

                var classModel = new List<string[]>();
                classModel.Add(methodNames);
                for (var j = i + 2; j < i + 6; j++)
                {
                    classModel.Add(lines.ElementAt(j));
                }
                modelTemp[className] = classModel;
            }
            string json = JsonConvert.SerializeObject(modelTemp);
            File.WriteAllText(outPath, json);
        }

        private void BuildPopularityModel(string inPath, string outPath)
        {
            var modelTemp = File.ReadAllLines(inPath)
                                .Select(line => line.Split('\t'))
                                .ToDictionary(line => line[1].Replace("\"", ""), line => Convert.ToInt32(line[2]));

            string json = JsonConvert.SerializeObject(modelTemp);
            File.WriteAllText(outPath, json);
        }

        private void BuildGlobalScoringModel()
        {
            BuildScoringModel(SCORING_MODEL_CSV_PATH, SCORING_MODEL_JSON_PATH);
        }

        private void BuildProjectScoringModel()
        {
            BuildScoringModel(PROJECT_SCORING_MODEL_CSV_PATH, PROJECT_SCORING_MODEL_JSON_PATH);
        }

        private void BuildGlobalPopularityModel()
        {
            BuildPopularityModel(POPULARITY_MODEL_CSV_PATH, POPULARITY_MODEL_JSON_PATH);
        }

        private void BuildProjectPopularityModel()
        {
            BuildPopularityModel(PROJECT_POPULARITY_MODEL_CSV_PATH, PROJECT_POPULARITY_MODEL_JSON_PATH);
        }

        private void DeserializeModels()
        {
            string json = File.ReadAllText(SCORING_MODEL_JSON_PATH);
            scoringModel = JsonConvert.DeserializeObject<Dictionary<string, IEnumerable<string[]>>>(json);

            string jsonFreq = File.ReadAllText(POPULARITY_MODEL_JSON_PATH);
            popularityModel = JsonConvert.DeserializeObject<Dictionary<string, int>>(jsonFreq);

            string jsonProject = File.ReadAllText(PROJECT_SCORING_MODEL_JSON_PATH);
            projectScoringModel = JsonConvert.DeserializeObject<Dictionary<string, IEnumerable<string[]>>>(jsonProject);
        }

        public PythiaCompletionProvider()
        {
            Debug.WriteLine("PythiaCompletionProvider constructor called");

            if (!File.Exists(SCORING_MODEL_JSON_PATH))
            {
                Debug.WriteLine("Did not find serialized scoring model, building it from text file...");
                BuildGlobalScoringModel();
            }
            if (!File.Exists(POPULARITY_MODEL_JSON_PATH))
            {
                Debug.WriteLine("Did not find serialized popularity model, building it from text file...");
                BuildGlobalPopularityModel();
            }
            if (!File.Exists(PROJECT_SCORING_MODEL_JSON_PATH))
            {
                Debug.WriteLine("Did not find serialized project scoring model, building it from text file...");
                BuildProjectScoringModel();
            }
            //if (!File.Exists(PROJECT_POPULARITY_MODEL_JSON_PATH))
            //{
            //    Debug.WriteLine("Did not find serialized project popularity model, building it from text file...");
            //    BuildProjectPopularityModel();
            //}

            DeserializeModels();
        }

        // Explicitly remove ":" from the set of filter characters because (by default)
        // any character that appears in DisplayText gets treated as a filter char.

        private static readonly CompletionItemRules s_rules_top = CompletionItemRules.Default
        .WithMatchPriority(MatchPriority.Preselect)
        .WithSelectionBehavior(CompletionItemSelectionBehavior.HardSelection);

        private static readonly CompletionItemRules s_rules = CompletionItemRules.Default
            .WithMatchPriority(MatchPriority.Default)
            .WithSelectionBehavior(CompletionItemSelectionBehavior.HardSelection);

        internal override bool IsInsertionTrigger(SourceText text, int characterPosition, OptionSet options)
        {
            // return CompletionUtilities.IsTriggerCharacter(text, characterPosition, options);
            bool isInsertionTrigger = text[characterPosition] == '.';
            char character = text[characterPosition];
            return isInsertionTrigger; // temp
        }

        // Is there a place to get the current document's semantic model for all completion providers? - Siyu
        public override async Task ProvideCompletionsAsync(CompletionContext context)
        {
            // obtaining the syntax tree and the semantic model
            var document = context.Document;
            var position = context.Position;
            var cancellationToken = context.CancellationToken;

            var span = new TextSpan(position, 0);
            var semanticModel = await document.GetSemanticModelForSpanAsync(span, cancellationToken).ConfigureAwait(false);

            var syntaxTree = semanticModel.SyntaxTree;

            //var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
            if (syntaxTree.IsInNonUserCode(position, cancellationToken))
            {
                return;
            }

            // determine the namespace from which to recommend methods
            var tokenLeft = syntaxTree.FindTokenOnLeftOfPosition(position, cancellationToken); // character left of current cursor position
            tokenLeft = tokenLeft.GetPreviousTokenIfTouchingWord(position);

            if (tokenLeft.Kind() != SyntaxKind.DotToken) return; // for testing - only handling dot

            SyntaxNode memberAccess = tokenLeft.Parent;
            Debug.WriteLine("MemberAccess: " + memberAccess);
            var memberKind = memberAccess.Kind(); // both types of imports would have the parent as a SimpleMemberAccessExpression

            if (memberKind != SyntaxKind.QualifiedName & memberKind != SyntaxKind.SimpleMemberAccessExpression)
            {
                return;
            }

            var workspace = document.Project.Solution.Workspace;

            if (semanticModel == null)
            {
                return;
            }

            var syntaxContext = CSharpSyntaxContext.CreateContext(workspace, semanticModel, position, cancellationToken);

            ImmutableArray<ISymbol> candidateMethods;
            ITypeSymbol container;

            ExtractCandidateMethods(memberAccess, memberKind, syntaxContext, out candidateMethods, out container, cancellationToken);

            if (candidateMethods.LongCount() == 0)
            {
                return;
            }

            // Context Evaluation

            // Check if target token is in IfConditional.  
            var inIfConditional = CheckIfTokenInIfConditional(memberAccess);

            IEnumerable<ISymbol> invokingSymbs = GetCoOccuringInvocations(syntaxTree, semanticModel, container);

            Dictionary<string, double> completionSet = new Dictionary<string, double>();
            if (invokingSymbs.Count() > 0)
            {
                Debug.WriteLine("Using Scoring Model to Recommend");
                var completionModel = GetScoringModelRecommendations(memberAccess, inIfConditional, invokingSymbs, container);
                completionSet = MergeScoringModelCompletionsWithCandidates(candidateMethods, completionModel);
            }
            else
            {
                Debug.WriteLine("Using Popularity Model to Recommend");
                completionSet = GetPopularityModelRecommendations(candidateMethods); // already takes into consideration what are the allowed candidate methods
            }

            if (completionSet.Count == 0)
            {
                Debug.WriteLine("Both completion models resulted in 0 recommendations");
                return;
            }

            Debug.Write($"There are {candidateMethods.Length} candidate methods, and {completionSet.Count} recommendations");

            int count = 0;
            foreach (var m in completionSet)
            {
                var methods = candidateMethods.Where(entry => entry.Name == m.Key).ToList(); // get all the methods

                context.AddItem(SymbolCompletionItem.CreateWithSymbolId(
                  displayText: m.Key,
                  insertionText: null,
                  symbols: methods,
                  filterText: m.Key,
                  contextPosition: tokenLeft.SpanStart,
                  rules: s_rules_top,
                  sortText: count.ToString()
                ));

                count++;
            }

            context.IsExclusive = false; // Exclusive list 
        }

        private static void ExtractCandidateMethods(SyntaxNode memberAccess, SyntaxKind memberKind, CSharpSyntaxContext syntaxContext,
            out ImmutableArray<ISymbol> candidateMethods, out ITypeSymbol container, CancellationToken cancellationToken)
        {
            candidateMethods = new ImmutableArray<ISymbol>();
            container = null;

            // when would it be a QualifiedName?
            if (memberKind == SyntaxKind.QualifiedName)
            {
                var name = ((QualifiedNameSyntax)memberAccess).Left;
                candidateMethods = CSharpRecommendationService.GetSymbolsOffOfName(syntaxContext, name, cancellationToken);

                if (name.IsFoundUnder<LocalDeclarationStatementSyntax>(d => d.Declaration.Type) ||
                    name.IsFoundUnder<FieldDeclarationSyntax>(d => d.Declaration.Type))
                {
                    var speculativeBinding = syntaxContext.SemanticModel.GetSpeculativeSymbolInfo(
                        name.SpanStart, name, SpeculativeBindingOption.BindAsExpression);

                    container = syntaxContext.SemanticModel.GetSpeculativeTypeInfo(
                        name.SpanStart, name, SpeculativeBindingOption.BindAsExpression).Type;

                    var containerNamespace = container.ContainingNamespace;
                    candidateMethods = candidateMethods.Where(m => m.ContainingNamespace == containerNamespace).ToImmutableArray<ISymbol>();
                }
            }
            else if (memberKind == SyntaxKind.SimpleMemberAccessExpression)
            {
                var expression = ((MemberAccessExpressionSyntax)memberAccess).Expression; // expression = File, 

                candidateMethods = CSharpRecommendationService.GetSymbolsOffOfExpression(syntaxContext, expression, cancellationToken);
                container = syntaxContext.SemanticModel.GetTypeInfo(expression, cancellationToken).Type; // NamedType System.IO.File
                //container = CSharpRecommendationService.GetSymbolTypeOffOfExpression(syntaxContext, expression, cancellationToken);
                //var tmp = container.ContainingNamespace.ToString();
                //var tmp2 = container.OriginalDefinition.ToString();
                //var tmp3 = container.ContainingSymbol.ToString();


                //var containerOriginalDefinition = container.OriginalDefinition;
                //candidateMethods = candidateMethods.Where(m => m.ContainingSymbol == containerOriginalDefinition).ToImmutableArray<ISymbol>();

            }
            //else
            //{
            //    return;
            //}
        }

        // merge to make sure we only include candidates that are valid candidateMethods
        private static Dictionary<string, double> MergeScoringModelCompletionsWithCandidates(
            ImmutableArray<ISymbol> candidateMethods, Dictionary<string, double> scoringModelCompletions)
        {
            // merge scoring model completions with candidate completions           
            var completionSet = new Dictionary<string, double>();

            foreach (var candidateFromModel in scoringModelCompletions)
            {
                var candidateMethodName = candidateFromModel.Key;
                candidateMethodName = candidateMethodName.Substring(0, candidateMethodName.IndexOf('-'));

                string regex = "(\\[.*\\])|(\".*\")|('.*')|(\\(.*\\))";

                var c = candidateMethods
                    .Where(i => candidateMethodName.Equals(Regex.Replace(i.ToString(), regex, "")))
                    .FirstOrDefault(); // only take the first if this method is overloaded

                if (c != null) // some methods in the model are not applicable to this situation so they will not be in candidateMethods
                {
                    completionSet[c.Name] = candidateFromModel.Value;
                }
            }
            return completionSet;
        }

        private Dictionary<string, double> GetPopularityModelRecommendations(ImmutableArray<ISymbol> candidateMethods)
        {
            var candidateMethodsStringified = candidateMethods.Select(i => i.ToString());

            // get each candidate method's popularity score if they are in our popularity model
            var subDictionary = candidateMethodsStringified.Where(k => popularityModel.ContainsKey(k))
                                                           .ToDictionary(k => k, k => popularityModel[k]);

            // aggregate the popularity count for all overload methods of this name
            Dictionary<string, int> aggMethodsByName = new Dictionary<string, int>();
            foreach (var m in subDictionary)
            {
                var matchingMethod = candidateMethods.FirstOrDefault(i => i.ToString() == m.Key);
                var matchingName = matchingMethod.Name;

                int value = 0;
                aggMethodsByName.TryGetValue(matchingName, out value);

                aggMethodsByName[matchingName] = m.Value + value;

            }

            Dictionary<string, double> completionModel = aggMethodsByName.OrderByDescending(entry => entry.Value)
                                                                         .Take(5)
                                                                         .ToDictionary(pair => pair.Key, pair => (double)pair.Value);
            return completionModel;
        }

        private static IEnumerable<ISymbol> GetCoOccuringInvocations(SyntaxTree syntaxTree, SemanticModel semanticModel, ITypeSymbol container)
        {
            // Check for co-occuring functions from the same namespace
            var root = (CompilationUnitSyntax)syntaxTree.GetRoot();
            var invocations = root
                              .DescendantNodes()
                              .OfType<MemberAccessExpressionSyntax>();

            invocations = invocations.Reverse();

            var invokingSymbs = new List<ISymbol>();
            if (container != null)
            {
                foreach (var invocation in invocations)
                {
                    try
                    {
                        var inv_symb = semanticModel.GetSymbolInfo(invocation).Symbol;
                        var cand_symb_vec = semanticModel.GetSymbolInfo(invocation).CandidateSymbols;

                        ISymbol cand_symb = null;

                        if (cand_symb_vec.LongCount() > 0)
                        {
                            cand_symb = cand_symb_vec.First();
                        }

                        if (inv_symb == null & cand_symb != null)
                        {
                            if (cand_symb.ContainingSymbol == container.OriginalDefinition)
                            {
                                // append cand_symb to invokingSymbs
                                invokingSymbs.Add(cand_symb);
                            }
                        }
                        else if (inv_symb != null)
                        {
                            if (inv_symb.ContainingSymbol == container.OriginalDefinition)
                            {
                                // append inv_symb to invokingSymbs
                                invokingSymbs.Add(inv_symb);
                            }
                        }
                    }
                    catch (NullReferenceException exception)
                    {
                        Debug.WriteLine("Invocation get symbol failed with exception: " + exception);
                    }
                }
            }
            return invokingSymbs;
        }

        private Dictionary<string, double> GetScoringModelRecommendations(SyntaxNode memberAccess,
            bool inIfConditional, IEnumerable<ISymbol> invokingSymbs, ITypeSymbol container)
        {
            Dictionary<string, double> scoringRecommendations = new Dictionary<string, double>();

            if (invokingSymbs.LongCount() > 0)
            {
                // var className = container.ToDisplayString(SYMBOL_DISPLAY_FORMAT); // will give System.String
                var className = container.ToString(); // will give string for both System.String and string
                Debug.WriteLine("GetScoringModelRecommendations, className: " + className);

                IEnumerable<string[]> classModel;

                if (scoringModel.ContainsKey(className))
                {
                    classModel = scoringModel[className];
                }
                else
                {
                    return scoringRecommendations;
                }

                // Filter only to relevant variable types.
                var methodsInClass = classModel.First();
                var nameCount = methodsInClass.LongCount(); // don't think array index can be a long size? Siyu

                var ifWeights = classModel.ElementAt(1).Select(i => Convert.ToDouble(i));

                var centroids = classModel.Skip(2);

                // from invocationSet, create a co-occurrence vector
                double[] usageVector = new double[nameCount];
                var counter = 0;
                foreach (var methodName in methodsInClass)
                {
                    string regex = "(\\[.*\\])|(\".*\")|('.*')|(\\(.*\\))"; // matches bracketed content and brackets

                    usageVector[counter] = 0;

                    // replacing the regex with "", System.IO.File.Exists(string) becomes System.IO.File.ReadLines
                    var invokingSymbsNoArgs = invokingSymbs.Select(i => Regex.Replace(i.ToString(), regex, ""));

                    // if there are invoking symbols which starts with the current methodName, usageVector for this methodName becomes 1
                    if (invokingSymbs.Where(i => methodName.StartsWith(Regex.Replace(i.ToString(), regex, ""))).LongCount() > 0)
                    {
                        usageVector[counter] = 1;
                    }
                    counter++;
                }

                // calculate the similarity between the invocationSet to each of the centroids in the model for this class
                var numCentroids = centroids.Count();
                var cosineSimilarities = new double[numCentroids]; // should just record the max - Siyu

                counter = 0;
                foreach (var c in centroids)
                {
                    var centroid = c.Select(i => Convert.ToDouble(i));
                    cosineSimilarities[counter] = GetCosineSimilarity(centroid.ToList(), usageVector.ToList());
                    counter++;
                }

                // select elements in min distance point, with non-zero weights, 
                // this should be representated as a dictionary, with "names" as key
                var simList = cosineSimilarities.ToList();
                var maxIndex = simList.IndexOf(simList.Max());

                var keyCentroid = centroids.ElementAt(maxIndex).Select(i => Convert.ToDouble(i));

                // If in IfConditional, reweight the scores
                if (inIfConditional)
                {
                    keyCentroid = keyCentroid.Zip(ifWeights, (s, i) => s * i);
                }
                else
                {
                    keyCentroid = keyCentroid.Zip(ifWeights, (s, i) => s * (1 - i));
                }

                scoringRecommendations = methodsInClass.Zip(keyCentroid, (k, v) => new { k, v })
                   .OrderBy(i => i.v)
                   .Reverse() // methods with more usage in that cluster's usage pattern is ranked on the top
                   .Where(i => i.v > 0)
                   .ToDictionary(x => x.k, x => x.v);
            }

            return scoringRecommendations;
        }

        private static void DebugPrintSymbols(IEnumerable<ISymbol> array)
        {
            foreach (var symbol in array)
            {
                Debug.WriteLine(symbol.ToString());
            }
        }

        private static void DebugPrintString(IEnumerable<string> array)
        {
            foreach (var symbol in array)
            {
                Debug.WriteLine(symbol);
            }
        }

        private static bool CheckIfTokenInIfConditional(SyntaxNode ttp)
        {
            var ancestors = ttp.Ancestors();
            var isInIfConditional = false;

            var ifAncestors = ancestors.OfType<IfStatementSyntax>();

            if (ifAncestors.LongCount() > 0)
            {
                var ifCondDesc = ifAncestors.First().DescendantNodes().First().DescendantNodesAndSelf();

                var match = ifCondDesc.Where(i => ttp.Equals(i));
                if (match.LongCount() > 0)
                {
                    isInIfConditional = true;
                }
            }
            return isInIfConditional;
        }

        protected override Task<CompletionDescription> GetDescriptionWorkerAsync(Document document, CompletionItem item, CancellationToken cancellationToken)
        => SymbolCompletionItem.GetDescriptionAsync(item, document, cancellationToken);

        // Calculate the cosine similarity measure between two vectors (higher the measure, the more similar)
        public static double GetCosineSimilarity(List<double> V1, List<double> V2)
        {
            //double sim = 0.0d;
            int N = 0;
            N = ((V2.Count < V1.Count) ? V2.Count : V1.Count);
            double dot = 0.0d;
            double mag1 = 0.0d;
            double mag2 = 0.0d;
            for (int n = 0; n < N; n++)
            {
                dot += V1[n] * V2[n];
                mag1 += Math.Pow(V1[n], 2);
                mag2 += Math.Pow(V2[n], 2);
            }

            if (mag1 == 0 || mag2 == 0)
            {
                return 0;
            }

            return dot / (Math.Sqrt(mag1) * Math.Sqrt(mag2));
        }
    }
}



