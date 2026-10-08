using ProtoCross.Diagnostics;
using ProtoCross.Ir;
using ProtoCross.Symbols;
using ProtoCross.Syntax;
using ProtoCross.Types;

namespace ProtoCross.Binding;

public sealed partial class Binder
{
    /// <summary>The name an assignment writes to, or null when its target names nothing.</summary>
    /// <remarks>
    /// Only the last name in the target is written. <c>line.quantity = 2;</c> reads <c>line</c> and
    /// writes <c>quantity</c>, and <c>1 = 2;</c> writes nothing at all -- which is a shape only a
    /// refused assignment can have, since a legal one names a local and nothing else.
    /// </remarks>
    private static SyntaxName? AssignedNameOf(Expression target) => target switch
    {
        NameExpression name => name.Name,
        MemberAccessExpression member => member.Name,

        // An element is written by writing its map, which is the name a reader of 'prices[sku] = 5;'
        // sees change.
        IndexExpression index => AssignedNameOf(index.Collection),
        _ => null,
    };

    /// <summary>Re-files an already-recorded reference as a write.</summary>
    /// <remarks>
    /// <para>
    /// Assignment is the only thing in the language that writes a name, and it is the statement --
    /// never the expression -- that knows which of the names it just bound was the one assigned to.
    /// The expression binder resolved <c>line</c> and <c>quantity</c> the same way and has nothing to
    /// tell them apart with, so rather than thread a kind down through every name binding for the one
    /// caller that needs it, the assignment amends the entry it is about.
    /// </para>
    /// <para>
    /// Backwards from the end, because the target has just been bound and its entries are the last
    /// ones added. A span nothing was recorded at -- a member name never written, a literal target --
    /// leaves the list alone.
    /// </para>
    /// </remarks>
    private void MarkWritten(SyntaxName? name)
    {
        if (name is not { } written)
        {
            return;
        }

        for (var index = _references.Count - 1; index >= 0; index--)
        {
            if (_references[index].Span == written.Span)
            {
                _references[index] = _references[index] with { Kind = ReferenceKind.Write };
                return;
            }
        }
    }

    private IrBlock BindBlock(BlockStatement block, Scope parent, MethodContext context)
    {
        var scope = new Scope(parent, LastVisibleOffsetIn(block));
        var statements = new List<IrStatement>();

        foreach (var statement in block.Statements)
        {
            var bound = BindStatement(statement, scope, context);
            statements.Add(bound);

            // A guard clause establishes presence for everything after it, not only inside its own
            // branch: 'if not has x { return 0; }' leaves x set for the rest of the block. What the
            // statement assigns is forgotten after that rather than before, because an assignment
            // inside it runs after its condition was tested, and so ends what the condition proved.
            context = context with
            {
                Present = ForgetChangedIn(statement, scope, context, Advance(context.Present, bound)),
            };
        }

        return new IrBlock(statements, block.Span) { IsClosed = block.IsClosed };
    }

    /// <summary>Binds a statement, and refuses a mutating call it makes inside an expression (spec 18).</summary>
    private IrStatement BindStatement(Statement statement, Scope scope, MethodContext context)
    {
        var bound = BindStatementItself(statement, scope, context);
        ReportMutatingCallsInsideExpressions(bound);
        return bound;
    }

    private IrStatement BindStatementItself(Statement statement, Scope scope, MethodContext context) => statement switch
    {
        BlockStatement block => BindBlock(block, scope, context),
        VariableDeclarationStatement declaration => BindVariableDeclaration(declaration, scope, context),
        ReturnStatement returnStatement => BindReturn(returnStatement, scope, context),
        IfStatement ifStatement => BindIf(ifStatement, scope, context),
        WhileStatement whileStatement => BindWhile(whileStatement, scope, context),
        BreakStatement breakStatement => BindBreak(breakStatement, context),
        ContinueStatement continueStatement => BindContinue(continueStatement, context),
        ForInStatement forIn => BindForIn(forIn, scope, context),
        SwitchStatement choice => BindSwitch(choice, scope, context),
        AssignmentStatement assignment => BindAssignment(assignment, scope, context),
        CompoundAssignmentStatement assignment => BindCompoundAssignment(assignment, scope, context),
        ExpressionStatement expression => BindExpressionStatement(expression, scope, context),
        _ => throw new ArgumentOutOfRangeException(nameof(statement), statement, "Unhandled statement."),
    };

    private IrStatement BindVariableDeclaration(
        VariableDeclarationStatement declaration,
        Scope scope,
        MethodContext context)
    {
        PlType? declaredType = declaration.DeclaredType is null
            ? null
            : ResolveTypeReference(declaration.DeclaredType);

        if (declaredType is VoidType)
        {
            _diagnostics.Report(
                DiagnosticCodes.VoidIsNotAValueType,
                $"{Capitalized(Refer(declaration.Name, "variable", "Variable"))} cannot be "
                + "declared void.",
                declaration.Span,
                "void is a return marker only (spec 8.1).");
            declaredType = ErrorType.Instance;
        }

        var initializer = BindExpression(declaration.Initializer, scope, context, declaredType);

        if (declaredType is not null
            && declaredType is not ErrorType
            && initializer.Type is not ErrorType
            && !TypesMatch(declaredType, initializer.Type))
        {
            _diagnostics.Report(
                DiagnosticCodes.VariableInitializerTypeMismatch,
                $"Cannot initialize {Refer(declaration.Name, "variable")} of type "
                + $"'{declaredType.DisplayName}' with a value of type "
                + $"'{initializer.Type.DisplayName}'.",
                declaration.Span,
                "ProtoCross does not apply implicit numeric conversions.");
        }

        var local = new IrLocal(
            new DeclarationSite(SymbolKind.Local, _document, declaration.Name, declaration.Span),
            declaredType ?? initializer.Type);

        // Same reasoning as an unnamed parameter: the declaration stays in the IR so the body can
        // still be walked, but nothing goes into scope under a name that was never written.
        if (declaration.Name.IsMissing)
        {
            return new IrVariableDeclaration(local, initializer, declaration.Span);
        }

        if (scope.TryDeclareLocal(local))
        {
            // From the end of its own declaration, because TryDeclareLocal runs after the
            // initializer above has been bound: 'var x: int64 = x;' is an unknown name, and a query
            // that offered x there would be offering a name that does not bind.
            Declare(scope, local.Declaration, local.Type, declaration.Span.End.Offset);
        }
        else
        {
            _diagnostics.Report(
                DiagnosticCodes.DuplicateVariable,
                $"A variable named '{declaration.Name}' is already in scope.",
                declaration.Span);
        }

        return new IrVariableDeclaration(local, initializer, declaration.Span);
    }

    private IrStatement BindReturn(ReturnStatement statement, Scope scope, MethodContext context)
    {
        if (statement.Value is null)
        {
            if (context.ReturnType is not VoidType)
            {
                _diagnostics.Report(
                    DiagnosticCodes.MissingReturnValue,
                    $"This method must return a value of type '{context.ReturnType.DisplayName}'.",
                    statement.Span);
            }

            return new IrReturn(null, statement.Span);
        }

        // A method that returns nothing refuses any value, which is said below, so a call written as
        // the value is bound as a statement's would be rather than told it has no value as well.
        var value = context.ReturnType is VoidType && statement.Value is InvocationExpression invocation
            ? BindInvocation(invocation, scope, context)
            : BindExpression(statement.Value, scope, context, context.ReturnType);

        if (context.ReturnType is VoidType)
        {
            _diagnostics.Report(
                DiagnosticCodes.UnexpectedReturnValue,
                "This method does not declare a return type.",
                statement.Span);
        }
        else if (value.Type is not ErrorType && !TypesMatch(context.ReturnType, value.Type))
        {
            _diagnostics.Report(
                DiagnosticCodes.ReturnTypeMismatch,
                $"Cannot return a value of type '{value.Type.DisplayName}' from a method "
                + $"declared '{context.ReturnType.DisplayName}'.",
                statement.Span,
                "ProtoCross does not apply implicit numeric conversions.");
        }

        return new IrReturn(value, statement.Span);
    }

    private IrStatement BindForIn(ForInStatement statement, Scope scope, MethodContext context)
    {
        var collection = BindExpression(statement.Collection, scope, context, null);

        PlType elementType;
        if (collection.Type is RepeatedType repeated)
        {
            elementType = repeated.ElementType;
        }
        else
        {
            if (collection.Type is not ErrorType)
            {
                _diagnostics.Report(
                    DiagnosticCodes.NotIterable,
                    $"Cannot iterate a value of type '{collection.Type.DisplayName}'.",
                    statement.Collection.Span,
                    collection.Type is MapType
                        ? "A map has no order to iterate in, and is read by key. Keep its keys in a repeated field, "
                          + "in the order wanted, and iterate that (spec 14.2)."
                        : "'for' iterates protobuf repeated fields (spec 14).");
            }

            elementType = ErrorType.Instance;
        }

        // The for statement ends where its body does, so the body is what says whether anything
        // closed it.
        var loopScope = new Scope(
            scope,
            ScopeEntry.LastOffsetInside(statement.Span, statement.Body.IsClosed));

        // The extent is the whole loop rather than its header: the parser records no span for the
        // header alone, and a client showing a loop binding in context wants the loop it binds over.
        var loop = new IrLocal(
            new DeclarationSite(SymbolKind.LoopBinding, _document, statement.VariableName, statement.Span),
            elementType);

        if (!statement.VariableName.IsMissing)
        {
            if (loopScope.TryDeclareLocal(loop))
            {
                // From inside the body, not from the loop and not from its brace: the collection was
                // bound above, against the enclosing scope, so 'for x in x { }' does not see its own
                // binding -- and the brace is where the collection ends rather than where the body
                // begins.
                Declare(loopScope, loop.Declaration, loop.Type, ScopeEntry.FirstOffsetInside(statement.Body.Span));
            }
            else
            {
                _diagnostics.Report(
                    DiagnosticCodes.DuplicateVariable,
                    $"A variable named '{statement.VariableName}' is already in scope.",
                    statement.Span);
            }
        }

        // Whatever the binding is an element of answers for it: whether it may be changed, and which
        // field a change through it reaches (spec 18).
        _elementsOf[loop.Id] = collection;

        // The collection is read once, before the first pass, so it keeps every fact. The body is
        // bound once for every pass, so it keeps only the facts a pass cannot end (#151). The binding
        // is in scope for that question, since a change through it may end a fact about another.
        var body = BindBlock(
            statement.Body,
            loopScope,
            Traversing(collection, context) with
            {
                LoopDepth = context.LoopDepth + 1,
                Present = ForgetChangedIn(statement.Body, loopScope, context, context.Present),
            });

        return new IrForEach(loop, collection, body, statement.Span);
    }

    private IrStatement BindIf(IfStatement statement, Scope scope, MethodContext context)
    {
        var condition = BindCondition(statement.Condition, scope, context, "if");
        var (whenTrue, whenFalse) = PresenceFacts(condition);

        var then = BindBlock(statement.Then, scope, context with { Present = Union(context.Present, whenTrue) });

        // The parser only ever puts a block or a nested 'if' here, and BindStatement handles both.
        var elseBranch = statement.Else is null
            ? null
            : BindStatement(statement.Else, scope, context with { Present = Union(context.Present, whenFalse) });

        return new IrIf(condition, then, elseBranch, statement.Span);
    }

    /// <remarks>
    /// The condition is tested again after every pass, so it is bound with only the facts that
    /// survive the body: a fact about a local the body assigns held on the first test and may not
    /// on the second (#151). What the condition proves is added after that, not forgotten with it,
    /// because it is proved afresh each time the body is entered.
    /// </remarks>
    private IrStatement BindWhile(WhileStatement statement, Scope scope, MethodContext context)
    {
        var everyPass = context with { Present = ForgetChangedIn(statement.Body, scope, context, context.Present) };
        var condition = BindCondition(statement.Condition, scope, everyPass, "while");
        var (whenTrue, _) = PresenceFacts(condition);
        var body = BindBlock(
            statement.Body,
            scope,
            everyPass with
            {
                LoopDepth = context.LoopDepth + 1,
                Present = Union(everyPass.Present, whenTrue),
            });

        return new IrWhile(condition, body, statement.Span);
    }

    /// <summary>
    /// Binds a branch or loop condition. ProtoCross has no truthiness, so the condition must already
    /// be a bool -- the same rule the logical operators follow (PC0047).
    /// </summary>
    private IrExpression BindCondition(Expression condition, Scope scope, MethodContext context, string keyword)
    {
        var bound = BindExpression(condition, scope, context, ScalarType.BoolType);

        if (bound.Type is not ErrorType && !TypesMatch(bound.Type, ScalarType.BoolType))
        {
            _diagnostics.Report(
                DiagnosticCodes.ConditionMustBeBool,
                $"The '{keyword}' condition has type '{bound.Type.DisplayName}'.",
                condition.Span,
                "ProtoCross does not treat non-bool values as true or false; compare explicitly.");
        }

        return bound;
    }

    /// <remarks>
    /// A <c>break</c> leaves the innermost loop or <c>switch</c> around it, whichever is nearer (spec
    /// 15.2). Which one it leaves is the IR's shape to say, so only whether there is one is asked here.
    /// </remarks>
    private IrStatement BindBreak(BreakStatement statement, MethodContext context)
    {
        if (context.LoopDepth == 0 && !context.InsideASwitch)
        {
            _diagnostics.Report(
                DiagnosticCodes.BreakOutsideALoop,
                "'break' can only appear inside a 'for' or 'while' loop, or an arm of a 'switch'.",
                statement.Span);
        }

        return new IrBreak(statement.Span);
    }

    private IrStatement BindContinue(ContinueStatement statement, MethodContext context)
    {
        if (context.LoopDepth == 0)
        {
            // Said only inside a switch, where 'continue' is the word a C reader reaches for.
            _diagnostics.Report(
                DiagnosticCodes.ContinueOutsideALoop,
                "'continue' can only appear inside a 'for' or 'while' loop.",
                statement.Span,
                context.InsideASwitch ? "A switch has no next pass to continue to. 'break' leaves it." : null);
        }

        return new IrContinue(statement.Span);
    }

    private IrStatement BindAssignment(AssignmentStatement statement, Scope scope, MethodContext context)
    {
        if (statement.Target is IndexExpression)
        {
            return BindElementAssignment(statement, scope, context);
        }

        if (WritesAField(statement.Target, scope, context))
        {
            return BindFieldAssignment(statement, scope, context);
        }

        if (AssignableLocal(statement.Target, scope) is not { } assignable)
        {
            return BindRefusedAssignment(statement.Target, [statement.Value], statement.Span, scope, context);
        }

        var (name, local) = assignable;

        Use(local.Id, name.Name.Span, ReferenceKind.Write);

        var value = BindExpression(statement.Value, scope, context, local.Type);

        if (value.Type is not ErrorType && local.Type is not ErrorType && !TypesMatch(local.Type, value.Type))
        {
            _diagnostics.Report(
                DiagnosticCodes.AssignmentTypeMismatch,
                $"Cannot assign a value of type '{value.Type.DisplayName}' to '{local.Name}' "
                + $"of type '{local.Type.DisplayName}'.",
                statement.Span,
                "ProtoCross does not apply implicit numeric conversions.");
        }

        var target = new IrLocalReference(local, name.Span);
        ReportIfTraversalChanges([target], $"This assigns '{local.Name}'", statement.Span, context);

        return new IrAssignment(target, value, statement.Span);
    }

    /// <summary>
    /// The local <paramref name="target"/> names, with the name as written, when it is one that may be
    /// assigned: a local declared with <c>var</c>, which a loop binding is not (spec 18).
    /// </summary>
    private static (NameExpression Name, IrLocal Local)? AssignableLocal(Expression target, Scope scope)
        => target is NameExpression name && scope.LookupLocal(name.Name.Text) is { } local && !IsLoopBinding(local)
            ? (name, local)
            : null;

    /// <summary>Binds <c>x op= y</c> as <c>x = x op y</c> (spec 9.2).</summary>
    /// <remarks>
    /// <para>
    /// The long form is built as syntax and bound by the code that binds <c>x op y</c> anywhere else,
    /// so a compound assignment cannot come to mean something its long form does not: how a literal
    /// is typed, the <c>on_zero</c> rule, the overflow policy and every refusal are the operator's
    /// own. What comes out is the long form's IR, so neither backend knows compound assignment exists,
    /// and each emits what it emits for the long form.
    /// </para>
    /// <para>
    /// The target is recorded once, as a write, although the operation reads it too. It is one name
    /// written once, and a read and a write at the same span would list it twice in every search for
    /// references. LSP's highlight kinds have no read-and-write, and the write is what makes the
    /// target worth telling apart.
    /// </para>
    /// <para>
    /// No check that the result suits the target follows, as one follows <c>=</c>. Every operator
    /// with a compound form produces the type of its left operand, and that operand is the target.
    /// </para>
    /// </remarks>
    private IrStatement BindCompoundAssignment(
        CompoundAssignmentStatement statement,
        Scope scope,
        MethodContext context)
    {
        if (statement.Target is IndexExpression)
        {
            return BindCompoundElementAssignment(statement, scope, context);
        }

        if (WritesAField(statement.Target, scope, context))
        {
            return BindCompoundFieldAssignment(statement, scope, context);
        }

        if (AssignableLocal(statement.Target, scope) is not { } assignable)
        {
            return BindRefusedAssignment(
                statement.Target,
                statement.OnZero?.Fallback is { } fallback ? [statement.Value, fallback] : [statement.Value],
                statement.Span,
                scope,
                context);
        }

        var (name, local) = assignable;

        var operation = BindBinary(LongFormOf(statement), scope, context, local.Type, OperatorForm.Compound);
        MarkWritten(name.Name);

        var target = new IrLocalReference(local, name.Span);
        ReportIfTraversalChanges([target], $"This assigns '{local.Name}'", statement.Span, context);

        return new IrAssignment(target, operation, statement.Span);
    }

    /// <summary>The operation a compound assignment stands for: <c>x op y</c>, for <c>x op= y</c>.</summary>
    /// <remarks>
    /// It spans the target through the right side and any clause after it, which is the whole
    /// statement but its semicolon. That is what a reader takes the operation to be, and it lies inside
    /// the assignment's own span, as every IR node's span lies inside its parent's.
    /// </remarks>
    private static BinaryExpression LongFormOf(CompoundAssignmentStatement statement)
        => new(
            statement.Operator,
            statement.Target,
            statement.Value,
            SourceSpan.Union(statement.Target.Span, statement.OnZero?.Span ?? statement.Value.Span),
            statement.OnZero);

    /// <summary>
    /// Binds an assignment to something that may not be assigned: <c>PC0034</c>, with the target and
    /// every operand bound all the same.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The target is bound even though nothing may be assigned to it. What PC0034 refuses is the
    /// assignment, not the expression on its left: <c>quantity</c>, <c>factor</c> and
    /// <c>line.quantity</c> all name something that resolves, and leaving them unbound put those names
    /// in the index nowhere and left an editor with nothing at a position the author is looking
    /// straight at. It is the same rule the map field in a fixture and the presence test on a field
    /// without presence already follow, and it is why binding a call that cannot be made keeps its
    /// arguments.
    /// </para>
    /// <para>
    /// Everything goes in a block, because a statement has one slot and the alternative is to bind an
    /// expression and throw it away. No backend sees this: PC0034 has been reported, so the
    /// compilation has errors and <c>EmittableModule</c> is null.
    /// </para>
    /// </remarks>
    private IrStatement BindRefusedAssignment(
        Expression target,
        IReadOnlyList<Expression> operands,
        SourceSpan span,
        Scope scope,
        MethodContext context)
    {
        ReportUnassignable(target, scope);

        var refusedTarget = BindExpression(target, scope, context, null);
        MarkWritten(AssignedNameOf(target));

        return Refused(refusedTarget, [.. operands.Select(operand => BindExpression(operand, scope, context, null))], span);
    }

    /// <summary>Reports <c>PC0034</c> for a target that names neither a local nor a field.</summary>
    /// <remarks>
    /// A loop binding and a parameter are named, because the reader can see each is a name and needs
    /// telling why it is not one that may be assigned (#153). Both are read-only by decision (spec 18),
    /// and the way out of both is the same: a local of one's own.
    /// </remarks>
    private void ReportUnassignable(Expression target, Scope scope)
    {
        var (message, help) = target is NameExpression name
            ? (scope.LookupLocal(name.Name.Text), scope.LookupParameter(name.Name.Text)) switch
            {
                ({ } binding, _) => (
                    $"'{binding.Name}' is a loop binding, which cannot be assigned.",
                    $"Copy it into a local first: 'var copy: {binding.Type.DisplayName} = {binding.Name};'."),
                (_, { } parameter) => (
                    $"'{parameter.Name}' is a parameter, which cannot be assigned.",
                    $"Copy it into a local first: 'var copy: {parameter.Type.DisplayName} = {parameter.Name};'."),
                _ => (UnassignableMessage, UnassignableHelp),
            }
            : (UnassignableMessage, UnassignableHelp);

        _diagnostics.Report(DiagnosticCodes.InvalidAssignmentTarget, message, target.Span, help);
    }

    private const string UnassignableMessage = "Only a local variable or a field can be assigned.";

    private const string UnassignableHelp =
        "Assign a local declared with 'var', or a field of a message this method may change (spec 18).";

    /// <summary>A refused assignment's target and operands, kept as statements of a block.</summary>
    private static IrBlock Refused(IrExpression target, IReadOnlyList<IrExpression> operands, SourceSpan span)
        => new(
            [
                new IrExpressionStatement(target, target.Span),
                .. operands.Select(operand => new IrExpressionStatement(operand, operand.Span)),
            ],
            span);
}
