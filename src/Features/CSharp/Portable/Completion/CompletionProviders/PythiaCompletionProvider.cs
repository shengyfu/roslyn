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
using Newtonsoft.Json.Linq;

namespace Microsoft.CodeAnalysis.CSharp.Completion.Providers
{
    internal partial class PythiaCompletionProvider : CommonCompletionProvider
    {
        private static readonly string MODELS_PATH = @"..\..\..\..\roslyn\models\";
        private static readonly string ALL_MODELS_JSON_PATH = MODELS_PATH + @"model-all.json";

        private static readonly SymbolDisplayFormat SYMBOL_DISPLAY_FORMAT = new SymbolDisplayFormat(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);

        private Dictionary<string, IEnumerable<string[]>> model;

        private void BuildModel (IEnumerable<string[]> lines)
        {
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
            File.WriteAllText(ALL_MODELS_JSON_PATH, json);
        }

        private void DeserializeModel ()
        {
            string json = File.ReadAllText(ALL_MODELS_JSON_PATH);
            model = JsonConvert.DeserializeObject<Dictionary<string, IEnumerable<string[]>>>(json);
        }

        public PythiaCompletionProvider ()
        {
            Debug.WriteLine("PythiaCompletionProvider constructor called");

            // Read and parse model file
            var lines = File.ReadLines(MODELS_PATH + @"model-all.csv")
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Select(line => line.Split('\t'));
            // BuildModel(lines);
            DeserializeModel();
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

        public override async Task ProvideCompletionsAsync(CompletionContext context)
        {
            // obtaining the syntax tree and the semantic model
            var document = context.Document;
            var position = context.Position;
            var cancellationToken = context.CancellationToken;

            var span = new TextSpan(position, 0);
            var semanticModelTask = document.GetSemanticModelForSpanAsync(span, cancellationToken).ConfigureAwait(false);

            var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
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

            var semanticModel = await semanticModelTask;
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

            var scoringModelCompletions = GetScoringModelRecommendations(memberAccess, inIfConditional, invokingSymbs, container);
            Debug.WriteLine("After GetScoringModelRecommendations");
            if (scoringModelCompletions.Count() == 0)
            {
                return;
            }

            Dictionary<string, double> completionSet = MergeScoringModelCompletionsWithCandidates(candidateMethods, scoringModelCompletions);
            Debug.WriteLine("After MergeScoringModelCompletionsWithCandidates");
            //Dictionary<string, int> completionSet = GetPopularityModelRecommendations(candidateMethods);

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
                Debug.WriteLine("completion candidateMethodName: " + candidateMethodName);

                string regex = "(\\[.*\\])|(\".*\")|('.*')|(\\(.*\\))";

                var c = candidateMethods
                    .Where(i => candidateMethodName.Equals(Regex.Replace(i.ToString(), regex, "")))
                    .FirstOrDefault(); // only take the first if this method is overloaded

                if (c != null) // some methods in the model are not applicable to this situation so they will not be in candidateMethods
                {
                    Debug.WriteLine("candidate method: " + c.Name);
                    completionSet[c.Name] = candidateFromModel.Value;
                }
            }
            return completionSet;
        }

        private static Dictionary<string, int> GetPopularityModelRecommendations(ImmutableArray<ISymbol> candidateMethods)
        {
            var methodDictionary = // File.ReadAllLines(@"C:\users\jobanuel\Desktop\usageFreqs.txt")
                                  File.ReadAllLines(@"C:\repos\roslyn\models\freqs-new2.txt")
                                      .Select(line => line.Split('\t'))
                                      .ToDictionary(line => line[1].Replace("\"", ""), line => Convert.ToInt32(line[2]));


            var keys = candidateMethods.Select(i => i.ToString());

            var subDictionary = keys.Where(k => methodDictionary.ContainsKey(k)).ToDictionary(k => k, k => methodDictionary[k]);

            //Aggregate same name values
            Dictionary<string, int> AggMethodsByName = new Dictionary<string, int>();
            foreach (var m in subDictionary)
            {
                var matchingMethod = candidateMethods.FirstOrDefault(i => i.ToString() == m.Key);
                var matchingName = matchingMethod.Name;

                int value = 0;
                AggMethodsByName.TryGetValue(matchingName, out value);

                AggMethodsByName[matchingName] = m.Value + value;

            }

            AggMethodsByName = AggMethodsByName.OrderByDescending(entry => entry.Value)
                .Take(5)
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            return AggMethodsByName;
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
                    catch(NullReferenceException exception)
                    {
                        Debug.WriteLine("Invocation get symbol failed with exception: " + exception);
                    }
                }
            }
            return invokingSymbs;
        }

        // TODO(jobanuel): Jorge generalize this to more common scenarios outside of File.
        private Dictionary<string, double> GetScoringModelRecommendations(SyntaxNode memberAccess,
            bool inIfConditional, IEnumerable<ISymbol> invokingSymbs, ITypeSymbol container)
        {
            Dictionary<string, double> scoringRecommendations = new Dictionary<string, double>();

            if (invokingSymbs.LongCount() > 0)
            {
                // var className = container.ToDisplayString(SYMBOL_DISPLAY_FORMAT); // will give System.String
                var className = container.ToString(); // will give string for both System.String and string
                Debug.WriteLine("className: " + className);

                IEnumerable<string[]> classModel;

                if (model.ContainsKey(className))
                {
                    classModel = model[className];
                } else
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



        // Calculating difference between two vectors.
        public static double GetCosineSimilarity(List<double> V1, List<double> V2)
        {
            double sim = 0.0d;
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





/// JUNK CODE I'M NOT READY TO GET RID OFF YET

//var tokenSymbol = semanticModel.GetSymbolInfo(memberAccess).Symbol;

//if (tokenSymbol == null)
//{
//    var candidateSymbols = semanticModel.GetSymbolInfo(memberAccess).CandidateSymbols;
//    if (candidateSymbols.Length == 0)
//    {
//        return;
//    }
//    tokenSymbol = candidateSymbols.ElementAt(0);

//}

//// Find Token Type

//var symbol_kind = tokenSymbol.Kind.ToString();

//ITypeSymbol tokenType = null;


//if (symbol_kind == "NamedType")
//{
//     tokenType = (INamedTypeSymbol)tokenSymbol;

//} else if (symbol_kind == "Local")
//{
//     var local_tokenSymbol = (ILocalSymbol)tokenSymbol;

//    if (local_tokenSymbol == null)
//    {
//        return;
//    }

//    tokenType = local_tokenSymbol.Type;
//} else
//{
//    return;
//}


//var ovld = tokenType.GetMembers();

//IEnumerable<ISymbol> candidateMethods = tokenType
//                                             .GetMembers()
//                                             .ToList()
//                                             .Cast<ISymbol>()
//                                            // .Where(s => s.Kind == SymbolKind.Method)
//                                            // .Cast<IMethodSymbol>()
//                                            ;
