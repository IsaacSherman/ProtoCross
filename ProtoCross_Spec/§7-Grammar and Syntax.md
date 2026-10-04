## 7. Grammar and Syntax

This section describes the grammar implemented by the parser. The grammar is still summarized rather
than mechanically exhaustive.

### 7.1 Implemented Grammar

```ebnf
source_file       = { import_decl | extend_decl | test_decl };

import_decl       = "import" "proto" string_literal ";";

extend_decl       = "extend" qualified_name "{" { method_decl } "}";

method_decl       = [ "mut" ] "fn" identifier
                    "(" [ parameter_list ] ")"
                    [ "->" type_ref ]
                    block;

parameter_list    = parameter { "," parameter };
parameter         = identifier ":" type_ref;

block             = "{" { statement } "}";

statement         = var_decl
                  | return_stmt
                  | if_stmt
                  | while_stmt
                  | for_in_stmt
                  | break_stmt
                  | continue_stmt
                  | block
                  | assignment_stmt
                  | compound_stmt
                  | expression_stmt;

var_decl          = "var" identifier [ ":" type_ref ] "=" expression ";";
return_stmt       = "return" [ expression ] ";";
if_stmt           = "if" expression block [ "else" ( if_stmt | block ) ];
while_stmt        = "while" expression block;
for_in_stmt       = "for" identifier "in" expression block;
break_stmt        = "break" ";";
continue_stmt     = "continue" ";";
assignment_stmt   = expression "=" expression ";";
compound_stmt     = expression compound_op expression
                    [ "on_zero" ( expression | "fail" ) ] ";";
compound_op       = "+=" | "-=" | "*=" | "/=" | "%=" | "&=" | "|=" | "^=" | "<<=" | ">>=";
expression_stmt   = expression ";";                                  (* a call; see below *)

test_decl         = "test" qualified_name string_literal
                    "{" { receiver_fixture | test_arg | test_expectation } "}";
receiver_fixture  = "receiver" "{" [ field_list ] "}";
test_arg          = "arg" identifier "=" expression ";";
test_expectation  = "expect" ( "return" expression | "fail" ) ";";

conversion        = expression "as" type_ref                       (* an expression *)
                    [ "on_unknown" ( expression | "fail" ) ];
message_literal   = "new" qualified_name "{" [ field_list ] "}";   (* an expression *)
field_list        = field_init { "," field_init } [ "," ];
field_init        = identifier ":" field_value;
field_value       = list_value | expression;
list_value        = "[" [ expression { "," expression } [ "," ] ] "]";
```

Normative Requirement:

- The final grammar must be unambiguous.
- Backend code generation must not depend on parser quirks or target-language parsing.
- Semicolons are mandatory after imports, variable declarations, `return`, `break`, `continue`,
  assignment and compound assignment statements, expression statements, test arguments, and test
  expectations.
- The fields of a fixture or a message literal, and the elements of a list, are separated by commas,
  and a comma after the last one is allowed ([13.2](./§13-Messages.md#132-message-construction)).
- `new` is not a keyword. It begins a message literal only when an identifier follows it, and is an
  identifier anywhere else ([6.4](./§6-Lexical%20Structure.md#64-keywords)). A message literal is a primary expression, so it may appear
  wherever an expression may, an unparenthesized `if` or `while` condition included: the brace after
  `new` and a type name is the literal's, and the body's is the first one after it. A list is a
  repeated field's value only.
- `mut` is not a keyword. It marks a method only when `fn` follows it, and is an identifier
  anywhere else ([6.4](./§6-Lexical%20Structure.md#64-keywords), [18](./§18-Mutability.md#18-mutability)).
- `on_unknown` is not a keyword. It begins a conversion's clause only when `fail` or a name follows
  it, and is an identifier anywhere else. The clause belongs to the one conversion it follows, and
  its fallback is a postfix expression, so a conversion after it converts the whole conversion
  ([6.4](./§6-Lexical%20Structure.md#64-keywords), [12.1](./§12-Enums.md#121-an-enums-number)).
- A semicolon between fields -- before another field or before the closing brace -- is reported as
  one written where a comma goes, because it was the separator fixtures used before #80. Outside a
  fixture, a semicolon anywhere else among a literal's fields ends them, and the statement the
  literal is in: no expression contains one, so the literal's closing brace is what is missing.
- **An expression statement is a call**: a method call, an `append` included
  ([14.1](./§14-Repeated%20Fields%20and%20Collections.md#141-supported-operations)), or one written in parentheses. Any other expression standing as a statement is
  `PC0099`, a message literal included, and its help says to assign the value or delete the
  statement. Such a statement is nearly always a slip, `total + 1;` written for `total += 1;`, and it
  is refused rather than dropped, because it is not always free of effects: `count / divisor;` can end
  the program under `on_zero fail` ([10.2.1](./§10-Numeric%20Semantics.md#1021-the-on_zero-clause)). An expression that failed to bind has already said why,
  and a statement whose semicolon is missing is one still being typed, which the parser has
  reported. Neither is told as well that it is not a call.
- Top-level helper functions are not implemented.
- Variable declarations may state an explicit type or infer from the initializer.
- Every binary operator is left-associative, and operators bind in the order
  [9.2](./§9-Expressions%20and%20Operators.md#92-operators) gives.
- A compound operator is one token, the longest spelling that fits, so `a>>=b` is `>>=` and never
  `>` followed by `>=`. The right side of a compound assignment is a whole expression, and its
  `on_zero` clause binds as [10.2.1](./§10-Numeric%20Semantics.md#1021-the-on_zero-clause) says a division's does.
- A test declaration must contain a receiver fixture and an expectation. The parser accepts
  `receiver`, `arg`, and `expect` members in any order and reports missing required members after
  the block is parsed.
- Parser recovery synthesizes missing tokens and missing names so later compiler stages can continue
  reporting useful diagnostics and editor tooling can still anchor completion points.
- How deeply a construct may nest is bounded, and a chain of member accesses, calls, conversions,
  operators or `else if` branches counts one level per link; see
  [28](./§28-Security%20and%20Determinism.md#28-security-and-determinism).

Open Questions:

- Should top-level helper functions be allowed in a later version?
- Should this section be replaced with exact EBNF generated from or checked against the parser?
