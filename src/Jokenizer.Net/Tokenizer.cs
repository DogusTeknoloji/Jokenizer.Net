using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace Jokenizer.Net {
    using Tokens;

    public class Tokenizer {
        private static char[] unary = new[] { '-', '+', '!', '~' };
        private static Dictionary<string, int> binary = new Dictionary<string, int> {
            { "&&", 0 },
            { "||", 0 },
            { "??", 0 },
            { "|", 1 },
            { "^", 1 },
            { "&", 1 },
            { "==", 2 },
            { "!=", 2 },
            { "<=", 3 },
            { ">=", 3 },
            { "<", 3 },
            { ">", 3 },
            { "<<", 4 },
            { ">>", 4 },
            { "+", 5 },
            { "-", 5 },
            { "*", 6 },
            { "/", 6 },
            { "%", 6 }
        };
        private static Dictionary<string, object> knowns = new Dictionary<string, object> {
            { "true", true },
            { "false", false },
            { "null", null }
        };
        private static string separator = Thread.CurrentThread.CurrentCulture.NumberFormat.NumberDecimalSeparator;

        private readonly string exp;
        private readonly int len;
        private readonly IDictionary<string, object> externals;
        private int idx = 0;
        private char ch;

        private Tokenizer(string exp) {
            if (exp == null)
                throw new ArgumentNullException(nameof(exp));
            if (string.IsNullOrWhiteSpace(exp))
                throw new ArgumentException(nameof(exp));

            this.exp = exp;
            this.externals = externals ?? new Dictionary<string, object>();

            this.len = exp.Length;
            ch = exp.ElementAt(0);
        }

        public static Token Parse(string exp) {
            return new Tokenizer(exp).GetToken();
        }

        public static T Parse<T>(string exp) where T : Token {
            return (T)Parse(exp);
        }

        Token GetToken() {
            Skip();

            Token t = TryLiteral()
                ?? TryVariable()
                ?? TryUnary()
                ?? TryGroup();

            if (t == null) return t;

            if (t is VariableToken vt) {
                if (knowns.TryGetValue(vt.Name, out var value)) {
                    t = new LiteralToken(value);
                }
                else if (vt.Name == "new") {
                    t = GetObject();
                }
            }

            if (Done()) return t;

            Token r;
            do {
                Skip();

                r = t;
                t = TryMember(t)
                    ?? TryIndexer(t)
                    ?? TryLambda(t)
                    ?? TryCall(t)
                    ?? TryTernary(t)
                    ?? TryBinary(t);
            } while (t != null);

            return r;
        }

        Token TryLiteral() {

            LiteralToken tryNumber() {
                var n = "";

                void x() {
                    while (Char.IsNumber(ch)) {
                        n += ch;
                        Move();
                    }
                }

                x();
                bool isFloat = false;
                if (Get(separator)) {
                    n += separator;
                    x();
                    isFloat = true;
                }

                if (n != "") {
                    if (IsVariableStart())
                        throw new Exception($"Unexpected character (${ch}) at index ${idx}");

                    var val = isFloat ? float.Parse(n) : int.Parse(n);
                    return new LiteralToken(val);
                }

                return null;
            }

            Token tryString() {
                bool inter = false;
                if (ch == '$') {
                    inter = true;
                    Move();
                }
                if (ch != '"') return null;

                var q = ch;
                var es = new List<Token>();
                var s = "";

                for (char c = Move(); !Done(); c = Move()) {
                    if (c == q) {
                        Move();

                        if (es.Count > 0) {
                            if (s != "") {
                                es.Add(new LiteralToken(s));
                            }

                            return es.Aggregate(
                                (Token)new LiteralToken(""),
                                (p, n) => new BinaryToken("+", p, n)
                            );
                        }

                        return new LiteralToken(s);
                    }

                    if (c == '\\') {
                        c = Move();
                        switch (c) {
                            case 'a':
                                s += '\a';
                                break;
                            case 'b':
                                s += '\b';
                                break;
                            case 'f':
                                s += '\f';
                                break;
                            case 'n':
                                s += '\n';
                                break;
                            case 'r':
                                s += '\r';
                                break;
                            case 't':
                                s += '\t';
                                break;
                            case 'v':
                                s += '\v';
                                break;
                            case '0':
                                s += '\0';
                                break;
                            case '\\':
                                s += '\\';
                                break;
                            case '"':
                                s += '"';
                                break;
                            default:
                                s += '\\' + c;
                                break;
                        }
                    } else if (inter && Get("${")) {
                        if (s != "") {
                            es.Add(new LiteralToken(s));
                            s = "";
                        }
                        es.Add(GetToken());

                        Skip();
                        if (ch != '}')
                            throw new Exception($"Unterminated template literal at {idx}");
                    } else {
                        s += c;
                    }
                }

                throw new Exception($"Unclosed quote after {s}");
            }

            return tryNumber() ?? tryString();
        }

        Token TryVariable() {
            var v = "";

            if (IsVariableStart()) {
                do {
                    v += ch;
                    Move();
                } while (StillVariable());
            }

            return v != "" ? new VariableToken(v) : null;
        }

        Token TryUnary() {
            if (unary.Contains(ch)) {
                var u = ch;
                Move();
                return new UnaryToken(u, GetToken());
            }

            return null;
        }

        Token TryGroup() {
            return Get("(") ? new GroupToken(GetGroup()) : null;
        }

        IEnumerable<Token> GetGroup() {
            var es = new List<Token>();
            do {
                var e = GetToken();
                if (e != null) {
                    es.Add(e);
                }
            } while (Get(","));

            To(")");

            return es;
        }

        Token GetObject() {
            To("{");

            var es = new List<IVariableToken>();
            do {
                Skip();
                var vt = TryVariable() as IVariableToken;
                if (vt == null)
                    throw new Exception($"Invalid assignment at {idx}");

                Skip();
                if (Get("=")) {
                    Skip();

                    es.Add(new AssignToken(vt.Name, GetToken()));
                } else {
                    es.Add(vt);
                }
            } while (Get(","));

            To("}");

            return new ObjectToken(es);
        }

        Token TryMember(Token e) {
            if (!Get(".")) return null;

            Skip();
            var v = TryVariable() as IVariableToken;
            if (v == null) throw new Exception($"Invalid member identifier at {idx}");

            return new MemberToken(e, v);
        }

        Token TryIndexer(Token e) {
            return null;
        }

        Token TryLambda(Token e) {
            return null;
        }

        Token TryCall(Token e) {
            return null;
        }

        Token TryTernary(Token e) {
            return null;
        }

        Token TryBinary(Token e) {
            return null;
        }

        bool IsSpace() {
            return Char.IsWhiteSpace(ch);
        }

        bool IsNumber() {
            return char.IsNumber(ch);
        }

        bool IsVariableStart() {
            return ch == 95                 // `_`
                || (ch >= 65 && ch <= 90)   // A...Z
                || (ch >= 97 && ch <= 122); // a...z
        }

        bool StillVariable() {
            return IsVariableStart() || Char.IsNumber(ch);
        }

        bool Done() {
            return idx >= len;
        }

        char Move(int count = 1) {
            idx += count;
            var d = Done();
            return ch = d ? '\0' : exp.ElementAt(idx);
        }

        bool Get(string s) {
            if (Eq(idx, s)) {
                Move(s.Length);
                return true;
            }

            return false;
        }

        void Skip() {
            while (IsSpace()) Move();
        }

        bool Eq(int idx, string target) {
            if (idx + target.Length > exp.Length) return false;
            return exp.Substring(idx, target.Length) == target;
        }

        void To(string c) {
            Skip();

            if (!Eq(idx, c))
                throw new Exception($"Expected {c} at index {idx}, found {exp[idx]}");

            Move(c.Length);
        }

        Token FixPrecedence(Token left, string leftOp, BinaryToken right) {
            var p1 = Tokenizer.binary[leftOp];
            var p2 = Tokenizer.binary[right.Operator];

            return p2 < p1
                ? new BinaryToken(right.Operator, new BinaryToken(leftOp, left, right.Left), right.Right)
                : new BinaryToken(leftOp, left, right);
        }
    }
}
