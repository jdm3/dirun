// Copyright (C) 2023 Jefferson Montgomery
// SPDX-License-Identifier: MIT
using System.Text;

namespace dirunlib;

public class Command
{
    public enum Continue {
        None,
        Pipe,
        Always,
        IfPass,
        IfFail
    };

    public enum RedirectHandle {
        Input,
        StdOut,
        StdErr,
        Warning,
        Verbose,
        Debug,
        Info,
        H7,
        H8,
        H9,
        Count,
        NotSet,
        File,
    }

    // Note: NUL is ToHandle=File with ToPath=null
    public class Redirection {
        public RedirectHandle ToHandle = RedirectHandle.NotSet;
        public string? ToPath = null;
        public bool Append = false;

        public void AppendAsString(StringBuilder sb, RedirectHandle fromHandle)
        {
            if (ToHandle != RedirectHandle.NotSet) {
                if (fromHandle == RedirectHandle.Input) {
                    sb.Append(" < ");
                } else {
                    sb.Append(' ');
                    sb.Append((char) ('0' + fromHandle));
                    sb.Append('>');
                    if (Append) {
                        sb.Append('>');
                    }
                    sb.Append(' ');
                }

                if (ToHandle == RedirectHandle.File) {
                    if (ToPath == null) {
                        sb.Append("NUL");
                    } else {
                        ParseContext.AddQuotesIfNeeded(sb, ToPath);
                    }
                } else {
                    sb.Append('&');
                    sb.Append((char) ('0' + ToHandle));
                }
            }
        }
    }

    public class Exe {
        public string Path = string.Empty;
        public string Args = string.Empty;
        public Continue Continue = Continue.None;
        public Redirection[] Redirections = new Redirection [(int) RedirectHandle.Count];
        public Exe()
        {
            for (int i = 0; i < (int) RedirectHandle.Count; ++i) {
                Redirections[i] = new Redirection();
            }
        }
    }

    public List<Exe> Exes = new List<Exe>();

    class ParseContext {
        public Exe? Exe = null;
        public StringBuilder ExeArgs = new StringBuilder();
        public Redirection PendingRedirect = new Redirection();
        public RedirectHandle PendingRedirectFrom = RedirectHandle.NotSet;

        public static void AddQuotesIfNeeded(StringBuilder sb, string s)
        {
            // Check if we need to add quotes (there is whitespace or varibles, which might add
            // whitespace)
            var chars = " \t\"".ToCharArray();
            bool needQuotes = false;
            for (int i = 0; i < s.Length; )
            {
                int j = s.IndexOfAny(chars, i);
                int k = s.IndexOf("%DIRUN_", i, StringComparison.CurrentCultureIgnoreCase);

                if (j == -1 && k == -1) {
                    break;
                }

                if (k != -1 || (j != -1 && s[j] != '\"')) {
                    needQuotes = true;
                    break;
                }

                // Skip any parts that are already quoted
                i = s.IndexOf('\"', j + 1) + 1;
                if (i == 0) {
                    break;
                }
            }

            if (!needQuotes) {
                sb.Append(s);
                return;
            }

            sb.Append('\"');
            for (int i = 0; i < s.Length; )
            {
                int j = s.IndexOf('\"', i);
                if (j == -1) {
                    sb.Append(s.Substring(i));
                    break;
                }

                sb.Append(s.Substring(i, j - i));
                sb.Append('\"');
                i = j + 1;
            }
            sb.Append('\"');
        }

        public void CompletePendingRedirect()
        {
            var redirect = Exe!.Redirections[(int) PendingRedirectFrom];
            if (redirect.ToHandle == RedirectHandle.NotSet) {
                redirect.ToHandle = PendingRedirect.ToHandle;
                redirect.ToPath   = PendingRedirect.ToPath;
                redirect.Append   = PendingRedirect.Append;
            }

            PendingRedirect.ToHandle = RedirectHandle.NotSet;
            PendingRedirect.ToPath = null;
            PendingRedirect.Append = false;
            PendingRedirectFrom = RedirectHandle.NotSet;
        }

        public void CompleteExe(ref List<Exe> exes, Continue cont)
        {
            if (Exe != null) {
                Exe.Args = ExeArgs.ToString();
                Exe.Continue = cont;
                exes.Add(Exe);
                Exe = null;
                ExeArgs.Clear();
            }
        }

        public void AppendWord(string arg, ref int begin, int end)
        {
            if (end <= begin) {
                return;
            }

            string word = arg.Substring(begin, end - begin);
            begin = end;

            if (Exe == null) {
                Exe = new Exe { Path = word };
            } else if (PendingRedirectFrom == RedirectHandle.NotSet) {
                if (ExeArgs.Length > 0) {
                    ExeArgs.Append(' ');
                }
                AddQuotesIfNeeded(ExeArgs, word);
            } else {
                PendingRedirect.ToHandle = RedirectHandle.File;
                PendingRedirect.ToPath = string.Equals(word, "NUL", StringComparison.CurrentCultureIgnoreCase) ? null : word;
                CompletePendingRedirect();
            }
        }
    }

    static bool IsEscaped(string arg, int j) { return j > 0 && arg[j - 1] == '^'; }
    static bool IsDoubled(string arg, int j) { return j + 1 < arg.Length && arg[j + 1] == arg[j]; }
    static bool HasDigitBefore(string arg, int j) { return j > 1 && char.IsDigit(arg[j - 1]) && char.IsWhiteSpace(arg[j - 2]); }
    static bool HasDigitAfter(string arg, int j) { return j + 1 < arg.Length && char.IsDigit(arg[j + 1]) && (j + 2 == arg.Length || char.IsWhiteSpace(arg[j + 2])); }

    void Parse(string[] args, int begin, int end)
    {
        var ctxt = new ParseContext();
        var chars = "><|&".ToCharArray();
        for (int a = begin; a < end; a++) {
            var arg = args[a];

            for (int i = 0, ibegin=0; i < arg.Length; ) {
                int j = arg.IndexOfAny(chars, i);
                if (j == -1) {
                    ctxt.AppendWord(arg, ref ibegin, arg.Length);
                    break;
                }
                
                if (IsEscaped(arg, j)) {
                    i = j + 1;
                } else {
                    switch (arg[j]) {
                    case '>':
                        if (HasDigitBefore(arg, j)) {
                            ctxt.AppendWord(arg, ref ibegin, j - 1);
                            ctxt.PendingRedirectFrom = (RedirectHandle) (arg[j - 1] - '0');
                        } else {
                            ctxt.AppendWord(arg, ref ibegin, j);
                            ctxt.PendingRedirectFrom = RedirectHandle.StdOut;
                        }
                        if (IsDoubled(arg, j)) {
                            ctxt.PendingRedirect.Append = true;
                            i = j + 2;
                        } else {
                            i = j + 1;
                        }
                        break;
                    case '<':
                        ctxt.AppendWord(arg, ref ibegin, j - 1);
                        ctxt.PendingRedirectFrom = RedirectHandle.Input;
                        i = j + 1;
                        break;
                    case '|':
                        if (IsDoubled(arg, j)) {
                            ctxt.AppendWord(arg, ref ibegin, j - 1);
                            ctxt.CompleteExe(ref Exes, Continue.IfFail);
                            i = j + 2;
                        } else {
                            ctxt.AppendWord(arg, ref ibegin, j - 1);
                            ctxt.CompleteExe(ref Exes, Continue.Pipe);
                            i = j + 1;
                        }
                        break;
                    case '&':
                        if (HasDigitAfter(arg, j) && !ctxt.PendingRedirect.Append) {
                            ctxt.AppendWord(arg, ref ibegin, j - 1);
                            ctxt.PendingRedirect.ToHandle = (RedirectHandle) (arg[j + 1] - '0');
                            ctxt.CompletePendingRedirect();
                            i = j + 2;
                        } else if (IsDoubled(arg, j)) {
                            ctxt.AppendWord(arg, ref ibegin, j - 1);
                            ctxt.CompleteExe(ref Exes, Continue.IfPass);
                            i = j + 2;
                        } else {
                            ctxt.AppendWord(arg, ref ibegin, j - 1);
                            ctxt.CompleteExe(ref Exes, Continue.Always);
                            i = j + 1;
                        }
                        break;
                    }

                    ibegin = i;
                }
            }
        }

        ctxt.CompleteExe(ref Exes, Continue.None);
    }

    public Command(string[] args, int begin, int end)
    {
        Parse(args, begin, end);
    }

    void AddArg(List<string> args, string command, int i0, int n)
    {
        if (n >= 2 && command[i0] == '\"' && command[i0 + n - 1] == '\"') {
            i0 += 1;
            n -= 2;
        }
        args.Add(command.Substring(i0, n));
    }

    public Command(string command)
    {
        int i0 = 0;
        while (i0 < command.Length && char.IsWhiteSpace(command[i0])) {
            i0 += 1;
        }

        var args = new List<string>();
        var chars = " \t\"".ToCharArray();
        for (int i = i0; i < command.Length; )
        {
            int j = command.IndexOfAny(chars, i);
            if (j == -1) {
                break;
            }

            switch (command[j]) {
            case '\"':
                int k = command.IndexOfAny(chars, j + 1);
                if (k == -1) {
                    i = command.Length;
                } else {
                    i = k + 1;
                }
                break;

            default:
                if (i0 < j) {
                    AddArg(args, command, i0, j - i0);
                }

                i = j + 1;
                while (i < command.Length && char.IsWhiteSpace(command[i])) {
                    i += 1;
                }
                i0 = i;
                break;
            }
        }

        if (i0 < command.Length) {
            AddArg(args, command, i0, command.Length - i0);
        }

        Parse(args.ToArray(), 0, args.Count);
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var exe in Exes) {
            ParseContext.AddQuotesIfNeeded(sb, exe.Path);
            if (exe.Args.Length > 0) {
                sb.Append(' ');
                sb.Append(exe.Args);
            }

            for (int i = 0; i < (int) RedirectHandle.Count; ++i) {
                exe.Redirections[i].AppendAsString(sb, (RedirectHandle) i);
            }

            switch (exe.Continue) {
            case Continue.Pipe:   sb.Append(" | "); break;
            case Continue.Always: sb.Append(" & "); break;
            case Continue.IfPass: sb.Append(" && "); break;
            case Continue.IfFail: sb.Append(" || "); break;
            }
        }
        return sb.ToString();
    }
}
