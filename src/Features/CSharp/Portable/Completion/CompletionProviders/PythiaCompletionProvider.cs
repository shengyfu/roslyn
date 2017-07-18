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

namespace Microsoft.CodeAnalysis.CSharp.Completion.Providers
{
    internal partial class PythiaCompletionProvider : CommonCompletionProvider
    {


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
            return CompletionUtilities.IsTriggerCharacter(text, characterPosition, options);
        }

        public override async Task ProvideCompletionsAsync(CompletionContext context)
        {
            var document = context.Document;
            var position = context.Position;
            var cancellationToken = context.CancellationToken;

            var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
            if (syntaxTree.IsInNonUserCode(position, cancellationToken))
            {
                return;
            }

            var token = syntaxTree
                .FindTokenOnLeftOfPosition(position, cancellationToken)
                .GetPreviousTokenIfTouchingWord(position);

            var tokenType = token.Kind();

            var tokendebug = syntaxTree
                .FindTokenOnLeftOfPosition(position, cancellationToken);

            var tokendebug2 = syntaxTree.FindTokenOnRightOfPosition(position, cancellationToken);

            // Temporary, just for testing purposes, only look at "." function invocations 
            if (!token.IsKind(SyntaxKind.DotToken))
            {
                return;
            }

            // Assuming current token is DotToken go to word token
            // JORGE, resolve proper target token method... you are taking 2 approaches in this here code.
            var targetToken = token.GetPreviousToken();
            var memberAccess = targetToken.Parent;


            var span = new TextSpan(position, 0);

            var semanticModel = await document.GetSemanticModelForSpanAsync(span, cancellationToken).ConfigureAwait(false);

            if (semanticModel == null)
            {
                return;
            }


            var memberKind = memberAccess.Parent.Kind();


            // Note that we cannot do anything with System.xxx. as System is considered left component  of Parent... i am guessing we would need to do some string parsing here 
            // If we want to return results for System.x then we need to figure out this issues
            // Once we include System.x types in our methods list, we will erroneously returns these methds for System.xxx. types, leading to incorrect recommendations. 
            // var memberName = memberAccess.Parent.ToString();

            if (memberKind != SyntaxKind.QualifiedName & memberKind != SyntaxKind.SimpleMemberAccessExpression)
            {
                return;

            }
            var workspace = document.Project.Solution.Workspace;

            var syntaxContext = CSharpSyntaxContext.CreateContext(workspace, semanticModel, position, cancellationToken);

            var tt = syntaxContext.TargetToken;
            var ttp = syntaxContext.TargetToken.Parent;

            ImmutableArray<ISymbol> candidateMethods;
            ITypeSymbol container;

            ExtractCandidateMethods(memberAccess, memberKind, syntaxContext, ttp, out candidateMethods, out container, cancellationToken);

            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);

            if (candidateMethods.LongCount() == 0)
            {
                return;
            }

            // Context Evaluation

            // Check if target token is in IfConditional.  
            var inIfConditional = CheckIfTokenInIfConditional(ttp);

            IEnumerable<ISymbol> invokingSymbs = GetCoOccuringInvocations(syntaxTree, semanticModel, container);

            var scoringModelCompletions = GetScoringModelRecommendations(inIfConditional, invokingSymbs);

            if (scoringModelCompletions.Count() == 0)
            {
                return;
            }

            Dictionary<string, double> completionSet = MergeScoringModelCompletionsWithCandidates(candidateMethods, scoringModelCompletions);

            //Dictionary<string, int> completionSet = GetPopularityModelRecommendations(candidateMethods);

            int count = 0;
            foreach (var m in completionSet)
            {
                var methods = candidateMethods.Where(entry => entry.Name == m.Key).ToList();

                context.AddItem(SymbolCompletionItem.CreateWithSymbolId(
                  displayText: m.Key,
                  insertionText: null,
                  symbols: methods,
                  filterText: m.Key,
                  contextPosition: token.SpanStart,
                  rules: s_rules_top,
                  sortText: count.ToString()
                ));

                count++;
            }


            context.IsExclusive = false; // Exclusive list 

        }

        private static void ExtractCandidateMethods(SyntaxNode memberAccess, SyntaxKind memberKind, CSharpSyntaxContext syntaxContext, SyntaxNode ttp, out ImmutableArray<ISymbol> candidateMethods, out ITypeSymbol container, CancellationToken cancellationToken)
        {
            candidateMethods = new ImmutableArray<ISymbol>();
            container = null;
            if (memberKind == SyntaxKind.QualifiedName)
            {
                var name = ((QualifiedNameSyntax)ttp).Left;
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
                var expression = ((MemberAccessExpressionSyntax)memberAccess.Parent).Expression;

                candidateMethods = CSharpRecommendationService.GetSymbolsOffOfExpression(syntaxContext, expression, cancellationToken);
                container = syntaxContext.SemanticModel.GetTypeInfo(expression, cancellationToken).Type;
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

        private static Dictionary<string, double> MergeScoringModelCompletionsWithCandidates(ImmutableArray<ISymbol> candidateMethods, IEnumerable<KeyValuePair<string, double>> scoringModelCompletions)
        {

            // merge scoring model completions with candidate completions           
            Dictionary<string, double> completionSet = new Dictionary<string, double>();

            foreach (var compCandidate in scoringModelCompletions)
            {
                var name = compCandidate.Key;


                string regex = "(\\[.*\\])|(\".*\")|('.*')|(\\(.*\\))";

                var c = candidateMethods
                    .Where(i => compCandidate.Key.StartsWith(Regex.Replace(i.ToString(), regex, "")))
                    .First()
                    ;

                completionSet[c.Name] = compCandidate.Value;
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
            var Invocations = root
                              .DescendantNodes()
                              .OfType<MemberAccessExpressionSyntax>();

            Invocations = Invocations.Reverse();

            List<ISymbol> invokingSymbs = new List<ISymbol>();
            if (container != null)
            {

                var s = Invocations.Select(i => semanticModel.GetSymbolInfo(i));
                var symbs = Invocations.Select(i => semanticModel.GetSymbolInfo(i).Symbol);
                var candsymbs = Invocations.Select(i => semanticModel.GetSymbolInfo(i).CandidateSymbols);

                // Possible null pointer here
                // Include values from CandidateSymbol symbol resolution as well.

                //Assuming access to symbol, but only candidates may be available if there is overload resolution issue, or is a local variable

                // InvokingSymbs may come invarious forms and so checking that null is not appropriate.

                foreach (var invocation in Invocations)
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
            }

            return invokingSymbs;

        }

        // TODO(jobanuel): Jorge generalize this to more common scenarios outside of File.
        private static IEnumerable<KeyValuePair<string, double>> GetScoringModelRecommendations(bool inIfConditional, IEnumerable<ISymbol> invokingSymbs)
        {
            Dictionary<string, double> ScoringRecommendations = new Dictionary<string, double>();

            if (invokingSymbs.LongCount() > 0)
            {
                // Read and parse model file

                //var lines = File.ReadLines("c:\\users\\jobanuel\\Documents\\model-if-scores-File.txt")
                var lines = File.ReadLines(@"C:\repos\roslyn\models\model-fileIO.csv").Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(line => line.Split('\t'))
                    ;

                //var relLines = lines.Where();

                // Filter only to relevant variable types. 


                // Need every other line filters
                // if (counter % 5 == 0) outfile.WriteLine(line);? 

                var names = lines.First();
                var nameCount = names.LongCount();
                var ifWeights = lines.ElementAt(1).Select(i => Convert.ToDouble(i));

                var centroids = lines.Skip(2);

                // from invocationSet, create a co-occurance vector
                double[] usageVector = new double[nameCount];
                var counter = 0;
                foreach (var n in names)
                {

                    string regex = "(\\[.*\\])|(\".*\")|('.*')|(\\(.*\\))";

                    usageVector[counter] = 0;
                    var mySymbs = invokingSymbs.Select(i => Regex.Replace(i.ToString(), regex, ""));


                    if (invokingSymbs.Where(i => n.StartsWith(Regex.Replace(i.ToString(), regex, ""))).LongCount() > 0)
                    {
                        usageVector[counter] = 1;
                    }
                    counter++;
                }

                // for each element in centroids, calculate the distance to invocationSet
                var n_centroids = centroids.LongCount();
                double[] cosineSimilarities = new double[n_centroids];

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

                var key_centroid = centroids.ElementAt(maxIndex)
                    .Select(i => Convert.ToDouble(i));

                // If in IfConditional, reweight the scores
                if (inIfConditional)
                {
                    key_centroid = key_centroid.Zip(ifWeights, (s, i) => s * i);
                }
                else
                {
                    key_centroid = key_centroid.Zip(ifWeights, (s, i) => s * (1 - i));
                }

                ScoringRecommendations = names.Zip(key_centroid, (k, v) => new { k, v })
                   .OrderBy(i => i.v)
                   .Reverse()
                   .Where(i => i.v > 0)
                   .ToDictionary(x => x.k, x => x.v)
                  ;
            }

            return ScoringRecommendations;
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
