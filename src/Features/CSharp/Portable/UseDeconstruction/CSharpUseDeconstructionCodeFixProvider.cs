﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.CodeGeneration;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.UseDeconstruction
{
    [ExportCodeFixProvider(LanguageNames.CSharp), Shared]
    internal class CSharpUseDeconstructionCodeFixProvider : SyntaxEditorBasedCodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds
            => ImmutableArray.Create(IDEDiagnosticIds.UseDeconstructionDiagnosticId);

        public override Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            context.RegisterCodeFix(
                new MyCodeAction(c => FixAsync(context.Document, context.Diagnostics[0], c)),
                context.Diagnostics);

            return SpecializedTasks.EmptyTask;
        }

        protected override Task FixAllAsync(
            Document document, ImmutableArray<Diagnostic> diagnostics,
            SyntaxEditor editor, CancellationToken cancellationToken)
        {
            var nodesToProcess = diagnostics.SelectAsArray(d => d.Location.FindToken(cancellationToken).Parent);

            // When doing a fix all, we have to avoid introducing the same name multiple times
            // into the same scope.  However, checking results after each change would be very
            // expensive (lots of forking + new semantic models, etc.).  So we use 
            // ApplyMethodBodySemanticEditsAsync to help out here.  It will only do the forking
            // if there are multiple results in the same method body.  If there's only one 
            // result in a method body, we will just apply it without doing any extra analysis.
            return editor.ApplyMethodBodySemanticEditsAsync(
                document, nodesToProcess,
                (semanticModel, node) => true,
                (semanticModel, currentRoot, node) => UpdateRoot(semanticModel, currentRoot, node, cancellationToken),
                cancellationToken);
        }

        private SyntaxNode UpdateRoot(SemanticModel semanticModel, SyntaxNode root, SyntaxNode node, CancellationToken cancellationToken)
        {
            var editor = new SyntaxEditor(root, CSharpSyntaxGenerator.Instance);

            // We use the callback form of ReplaceNode because we may have nested code that
            // needs to be updated in fix-all situations.  For example, nested foreach statements.
            // We need to see the results of the inner changes when making the outer changes.

            ImmutableArray<MemberAccessExpressionSyntax> memberAccessExpressions = default;
            if (node is VariableDeclaratorSyntax variableDeclarator)
            {
                var variableDeclaration = (VariableDeclarationSyntax)variableDeclarator.Parent;
                if (CSharpUseDeconstructionDiagnosticAnalyzer.TryAnalyzeVariableDeclaration(
                        semanticModel, variableDeclaration,
                        out var tupleType, out memberAccessExpressions,
                        cancellationToken))
                {
                    editor.ReplaceNode(
                        variableDeclaration.Parent,
                        (current, _) =>
                        {
                            var currentDeclarationStatement = (LocalDeclarationStatementSyntax)current;
                            return CreateDeconstructionStatement(tupleType, currentDeclarationStatement, currentDeclarationStatement.Declaration.Variables[0]);
                        });
                }
            }
            else if (node is ForEachStatementSyntax forEachStatement)
            {
                if (CSharpUseDeconstructionDiagnosticAnalyzer.TryAnalyzeForEachStatement(
                        semanticModel, forEachStatement,
                        out var tupleType, out memberAccessExpressions,
                        cancellationToken))
                {
                    editor.ReplaceNode(
                        forEachStatement,
                        (current, _) => CreateForEachVariableStatement(tupleType, (ForEachStatementSyntax)current));
                }
            }

            foreach (var memberAccess in memberAccessExpressions.NullToEmpty())
            {
                editor.ReplaceNode(
                    memberAccess,
                    (current, _) =>
                    {
                        var currentMemberAccess = (MemberAccessExpressionSyntax)current;
                        return currentMemberAccess.Name.WithTriviaFrom(currentMemberAccess);
                    });
            }

            return editor.GetChangedRoot();
        }

        private ForEachVariableStatementSyntax CreateForEachVariableStatement(INamedTypeSymbol tupleType, ForEachStatementSyntax forEachStatement)
        {
            // Copy all the tokens/nodes from the existing foreach statement to the new foreach statement.
            // However, convert the existing declaration over to a "var (x, y)" declaration or (int x, int y)
            // tuple expression.
            return SyntaxFactory.ForEachVariableStatement(
                forEachStatement.ForEachKeyword,
                forEachStatement.OpenParenToken,
                CreateTupleOrDeclarationExpression(tupleType, forEachStatement.Type),
                forEachStatement.InKeyword,
                forEachStatement.Expression,
                forEachStatement.CloseParenToken,
                forEachStatement.Statement);
        }

        private ExpressionStatementSyntax CreateDeconstructionStatement(
            INamedTypeSymbol tupleType, LocalDeclarationStatementSyntax declarationStatement, VariableDeclaratorSyntax variableDeclarator)
        {
            // Copy all the tokens/nodes from the existing declaration statement to the new assignment
            // statement. However, convert the existing declaration over to a "var (x, y)" declaration 
            // or (int x, int y) tuple expression.
            return SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    CreateTupleOrDeclarationExpression(tupleType, declarationStatement.Declaration.Type),
                    variableDeclarator.Initializer.EqualsToken,
                    variableDeclarator.Initializer.Value),
                declarationStatement.SemicolonToken);
        }

        private ExpressionSyntax CreateTupleOrDeclarationExpression(INamedTypeSymbol tupleType, TypeSyntax typeNode)
        {
            // If we have an explicit tuple type in code, convert that over to a tuple expression.
            // i.e.   (int x, int y) t = ...   will be converted to (int x, int y) = ...
            //
            // If we had the "var t" form we'll convert that to the declaration expression "var (x, y)"
            return typeNode.IsKind(SyntaxKind.TupleType)
                ? (ExpressionSyntax)CreateTupleExpression((TupleTypeSyntax)typeNode)
                : CreateDeclarationExpression(tupleType, typeNode);
        }

        private DeclarationExpressionSyntax CreateDeclarationExpression(INamedTypeSymbol tupleType, TypeSyntax typeNode)
            => SyntaxFactory.DeclarationExpression(
                typeNode, SyntaxFactory.ParenthesizedVariableDesignation(
                    SyntaxFactory.SeparatedList<VariableDesignationSyntax>(tupleType.TupleElements.Select(
                        e => SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier(e.Name))))));

        private TupleExpressionSyntax CreateTupleExpression(TupleTypeSyntax typeNode)
            => SyntaxFactory.TupleExpression(
                typeNode.OpenParenToken,
                SyntaxFactory.SeparatedList<ArgumentSyntax>(new SyntaxNodeOrTokenList(typeNode.Elements.GetWithSeparators().Select(ConvertTupleTypeElementComponent))),
                typeNode.CloseParenToken);

        private SyntaxNodeOrToken ConvertTupleTypeElementComponent(SyntaxNodeOrToken nodeOrToken)
        {
            if (nodeOrToken.IsToken)
            {
                // return commas directly as is.
                return nodeOrToken;
            }

            // "int x" as a tuple element directly translates to "int x" (a declaration expression
            // with a variable designation 'x').
            var node = (TupleElementSyntax)nodeOrToken.AsNode();
            return SyntaxFactory.Argument(
                SyntaxFactory.DeclarationExpression(
                    node.Type,
                    SyntaxFactory.SingleVariableDesignation(node.Identifier)));
        }

        private class MyCodeAction : CodeAction.DocumentChangeAction
        {
            public MyCodeAction(Func<CancellationToken, Task<Document>> createChangedDocument) 
                : base(FeaturesResources.Deconstruct_variable_declaration, createChangedDocument, FeaturesResources.Deconstruct_variable_declaration)
            {
            }
        }
    }
}
