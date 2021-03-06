using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MappingGenerator.Mappings;
using MappingGenerator.Mappings.MappingMatchers;
using MappingGenerator.Mappings.SourceFinders;
using MappingGenerator.RoslynHelpers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;

namespace MappingGenerator.Features.Refactorings
{
    [ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = nameof(EmptyInitializationBlockRefactoring)), Shared]
    public class UpdateLambdaParameterRefactoring : CodeRefactoringProvider
    {
        private const string Title = "Update lambda parameter with local variables";

        public sealed override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var node = root.FindNode(context.Span);

            var container = node.FindContainer<LambdaExpressionSyntax>();
            if (container != null)
            {
                var semanticModel = await context.Document.GetSemanticModelAsync().ConfigureAwait(false);
                var symbol = semanticModel.GetSymbolInfo(container, context.CancellationToken).Symbol;
                if (symbol is IMethodSymbol methodSymbol && methodSymbol.Parameters.Length == 1 && methodSymbol.ReturnsVoid)
                {
                    context.RegisterRefactoring(CodeAction.Create(title: Title, createChangedDocument: c => InitializeWithLocals(context.Document, container, c), equivalenceKey: Title));
                }
            }
        }

        private async Task<Document> InitializeWithLocals(Document document, LambdaExpressionSyntax lambdaExpressionSyntax, CancellationToken cancellationToken)
        {
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var sourceFinders = GetAllPossibleSourceFinders(lambdaExpressionSyntax, semanticModel).ToList();
            var mappingMatcher = new BestPossibleMatcher(sourceFinders);
            return await ReplaceWithMappingBody(document, lambdaExpressionSyntax, semanticModel, mappingMatcher, cancellationToken).ConfigureAwait(false);
        }

        private static IEnumerable<IMappingSourceFinder> GetAllPossibleSourceFinders(LambdaExpressionSyntax lambdaExpression, SemanticModel semanticModel)
        {
            var localSymbols = semanticModel.GetLocalSymbols(lambdaExpression);
            yield return new LocalScopeMappingSourceFinder(semanticModel, localSymbols);
            foreach (var localSymbol in localSymbols)
            {
                
                var symbolType = semanticModel.GetTypeForSymbol(localSymbol);
                
                if (symbolType !=null && ObjectHelper.IsSimpleType(symbolType) == false)
                {
                    yield return new ObjectMembersMappingSourceFinder(new AnnotatedType(symbolType), SyntaxFactory.IdentifierName(localSymbol.Name));
                }
            }
        }

        private static async Task<Document> ReplaceWithMappingBody(Document document, LambdaExpressionSyntax lambda, SemanticModel semanticModel, IMappingMatcher mappingMatcher, CancellationToken cancellationToken)
        {
            var methodSymbol = (IMethodSymbol)semanticModel.GetSymbolInfo(lambda, cancellationToken).Symbol;
            var createdObjectType = methodSymbol.Parameters.First().Type;
            var mappingEngine = await MappingEngine.Create(document, cancellationToken).ConfigureAwait(false);
            var mappingContext = new MappingContext(lambda, semanticModel);
            var mappingTargetHelper = new MappingTargetHelper();
            var propertiesToSet = mappingTargetHelper.GetFieldsThaCanBeSetPublicly(createdObjectType, mappingContext);
            var mappings = await mappingEngine.MapUsingSimpleAssignment(propertiesToSet, mappingMatcher, mappingContext, globalTargetAccessor: SyntaxFactory.IdentifierName(GetParameterIdentifier(lambda))).ConfigureAwait(false);
            var statements = mappings.Select(x=>x.AsStatement().WithTrailingTrivia(SyntaxFactory.EndOfLine("\r\n")));
            
            var newLambda = UpdateLambdaBody(lambda, SyntaxFactory.Block(statements)).WithAdditionalAnnotations(Formatter.Annotation);
            return await document.ReplaceNodes(lambda, newLambda, cancellationToken).ConfigureAwait(false);
        }

        private static SyntaxToken GetParameterIdentifier(LambdaExpressionSyntax lambda)
        {
            return lambda switch
            {
                SimpleLambdaExpressionSyntax simpleLambda => simpleLambda.Parameter.Identifier,
                ParenthesizedLambdaExpressionSyntax parenthesizedLambda => parenthesizedLambda.ParameterList.Parameters.FirstOrDefault().Identifier,
                _ => SyntaxFactory.Token(SyntaxKind.None)
            };
        }

        private static LambdaExpressionSyntax UpdateLambdaBody(LambdaExpressionSyntax lambda, BlockSyntax blockSyntax)
        {
            return lambda switch
            {
                SimpleLambdaExpressionSyntax simpleLambda => simpleLambda.WithBody(blockSyntax),
                ParenthesizedLambdaExpressionSyntax parenthesizedLambda => parenthesizedLambda.WithBody(blockSyntax),
                _ => lambda
            };
        }

    }
}
