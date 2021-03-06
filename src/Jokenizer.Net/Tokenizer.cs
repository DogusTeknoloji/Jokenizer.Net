using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace Jokenizer.Net {
    using Tokens;

    public class Tokenizer {
        static char[] unary = new[] { '-', '+', '!', '~' };
        static Dictionary<string, int> binary = new Dictionary<string, int> {
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
        static Dictionary<string, object> knowns = new Dictionary<string, object> {
            { "true", true },
            { "false", false },
            { "null", null }
        };
        static string separator = Thread.CurrentThread.CurrentCulture.NumberFormat.NumberDecimalSeparator;

        readonly string exp;
        readonly int len;
        readonly IDictionary<string, object> externals;
        int idx = 0;
        char ch;

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

        Token GetToken() {
            Skip();

            Token t = TryLiteral()
                ?? TryVariable()
                ?? TryParameter()
                ?? TryUnary()
                ?? (Token)TryGroup();

            if (t == null) return t;

            if (t is VariableToken vt) {
                if (knowns.TryGetValue(vt.Name, out var value)) {
                    t = new LiteralToken(value);
                } else if (vt.Name == "new") {
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
                    ?? (Token)TryBinary(t);
            } while (t != null);

            return r;
        }

        string GetNumber() {
            var n = "";
            while (IsNumber()) {
                n += ch;
                Move();
            }
            return n;
        }

        LiteralToken TryNumber() {
            var n = GetNumber();

            bool isFloat = false;
            if (Get(separator)) {
                n += separator;
                n += GetNumber();
                isFloat = true;
            }

            if (n != "") {
                if (IsVariableStart())
                    throw new Exception($"Unexpected character (${ch}) at index ${idx}");

                var val = isFloat ? float.Parse(n) : Convert.ChangeType(int.Parse(n), typeof(int));
                return new LiteralToken(val);
            }

            return null;
        }

        Token TryString() {
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
                            s += "\\" + c;
                            break;
                    }
                } else if (inter && Get("{")) {
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

        Token TryLiteral() {
            return TryNumber() ?? TryString();
        }

        string GetVariableName() {
            var v = "";

            if (IsVariableStart()) {
                do {
                    v += ch;
                    Move();
                } while (StillVariable());
            }

            return v;
        }

        VariableToken TryVariable() {
            var v = GetVariableName();
            return v != "" ? new VariableToken(v) : null;
        }

        VariableToken TryParameter() {
            if (!Get("@")) return null;

            var n = GetNumber();
            if (string.IsNullOrEmpty(n))
                throw new Exception($"Invalid parameter at {idx}");

            return new VariableToken("@" + n);
        }

        UnaryToken TryUnary() {
            if (unary.Contains(ch)) {
                var u = ch;
                Move();
                return new UnaryToken(u, GetToken());
            }

            return null;
        }

        GroupToken TryGroup() {
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

        ObjectToken GetObject() {
            To("{");

            var es = new List<AssignToken>();
            do {
                Skip();
                var member = GetVariableName();
                if (string.IsNullOrEmpty(member))
                    throw new Exception($"Invalid assignment at {idx}");

                Skip();
                if (Get("=")) {
                    Skip();

                    es.Add(new AssignToken(member, GetToken()));
                } else {
                    es.Add(new AssignToken(member, new VariableToken(member)));
                }
            } while (Get(","));

            To("}");

            return new ObjectToken(es);
        }

        MemberToken TryMember(Token t) {
            if (!Get(".")) return null;

            Skip();
            var v = GetVariableName();
            if (string.IsNullOrEmpty(v)) throw new Exception($"Invalid member identifier at {idx}");

            return new MemberToken(t, v);
        }

        IndexerToken TryIndexer(Token t) {
            if (!Get("[")) return null;

            Skip();
            var k = GetToken();
            To("]");

            return new IndexerToken(t, k);
        }

        LambdaToken TryLambda(Token t) {
            if (!Get("=>"))
                return null;

            return new LambdaToken(GetToken(), GetParameters(t));
        }

        IEnumerable<string> GetParameters(Token t) {
            if (t is GroupToken gt) {
                return gt.Tokens.Select(x => {
                    if (!(x is IVariableToken xv))
                        throw new Exception($"Invalid parameter at {idx}");

                    return xv.Name;
                });
            }

            if (!(t is IVariableToken vt))
                throw new Exception($"Invalid parameter at {idx}");

            return new[] { vt.Name };
        }

        CallToken TryCall(Token t) {
            return Get("(") ? GetCall(t) : null;
        }

        CallToken GetCall(Token t) {
            var args = GetGroup();

            return new CallToken(t, args);
        }

        TernaryToken TryTernary(Token t) {
            if (!Get("?")) return null;

            var whenTrue = GetToken();
            To(":");
            var whenFalse = GetToken();

            return new TernaryToken(t, whenTrue, whenFalse);
        }

        BinaryToken TryBinary(Token t) {
            var op = binary.FirstOrDefault(b => Get(b.Key.ToString()));

            if (op.Equals(default(KeyValuePair<string, int>))) return null;

            var right = GetToken();

            if (right is BinaryToken bt)
                return FixPrecedence(t, op.Key, bt);

            return new BinaryToken(op.Key, t, right);
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
            return IsVariableStart() || IsNumber();
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
                throw new Exception($"Expected {c} at index {idx}, found {ch}");

            Move(c.Length);
        }

        BinaryToken FixPrecedence(Token left, string leftOp, BinaryToken right) {
            var p1 = Tokenizer.binary[leftOp];
            var p2 = Tokenizer.binary[right.Operator];

            return p2 < p1
                ? new BinaryToken(right.Operator, new BinaryToken(leftOp, left, right.Left), right.Right)
                : new BinaryToken(leftOp, left, right);
        }

        public static Token Parse(string exp) {
            return new Tokenizer(exp).GetToken();
        }

        public static T Parse<T>(string exp) where T : Token {
            return (T)Parse(exp);
        }
    }
}
