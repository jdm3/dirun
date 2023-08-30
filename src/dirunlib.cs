// Copyright (C) 2023 Jefferson Montgomery
// SPDX-License-Identifier: MIT
using System.ComponentModel;
using System.Diagnostics;

using CConsole = dirunlib.ColorConsole;

namespace dirunlib;

public class Dirun
{
    public class DirectoryInfo {
        public string Name = string.Empty;
        public FileInfo[] Files = Array.Empty<FileInfo>();
        public DirectoryInfo[] Dirs = Array.Empty<DirectoryInfo>();
        public int NumFiles = 0;
    }

    public class FileInfo {
        public string Path = string.Empty;
        public string PathRel = string.Empty;
        public string Name = string.Empty;
        public string Stdout = string.Empty;
        public string Stderr = string.Empty;
        public int ExitCode = 0;
        public bool Completed = false;
    }

    public Command? Command = null;
    public string WorkingDirectory = string.Empty;
    public bool Recurse = true;

    public bool CollectStdout = false;
    public bool CollectStderr = false;

    public Action<FileInfo>? OnFileCompleted = null;

    public static bool ContainsVariables(string s)
    {
        return s.IndexOf("%DIRUN_", StringComparison.CurrentCultureIgnoreCase) != -1;
    }

    public static string ReplaceVariables(string s, FileInfo file)
    {
        for (int i = 0; ;) {
            int j = s.IndexOf("%DIRUN_", i, StringComparison.CurrentCultureIgnoreCase);
            if (j == -1) {
                break;
            }

            j += 7;
            int k = s.IndexOf("%", j, StringComparison.CurrentCultureIgnoreCase);
            if (k == -1) {
                break;
            }

            int n = k - j;
            string? rep = null;
            switch (n) {
            case 4:      if (string.Compare(s, j, "FEXT",      0, 4, true) == 0) rep = Path.GetExtension(file.Path).Substring(1); break;
            case 5:      if (string.Compare(s, j, "DPATH",     0, 5, true) == 0) rep = Path.GetDirectoryName(file.Path);
                    else if (string.Compare(s, j, "FNAME",     0, 5, true) == 0) rep = Path.GetFileNameWithoutExtension(file.Path);
                    else if (string.Compare(s, j, "FPATH",     0, 5, true) == 0) rep = file.Path; break;
            case 9:      if (string.Compare(s, j, "DPATH_REL", 0, 9, true) == 0) rep = Path.GetDirectoryName(file.PathRel);
                    else if (string.Compare(s, j, "FPATH_REL", 0, 9, true) == 0) rep = file.PathRel; break;
            }

            if (rep == null) {
                i = k + 1;
                continue;
            }

            s = string.Concat(s.AsSpan(0, j - 7), rep, s.AsSpan(k + 1));
            i = j - 7 + rep.Length;
        }

        return s;
    }

    TaskCompletionSource<bool>? traversalCompletionSource_ = null;
    DirectoryInfo? rootDirInfo_ = null;
    int numFilesCompleted_ = 0;
    bool traversalCompleted_ = false;

    public void TraverseDirectory(string dirPath, string fileSpec, DirectoryInfo dirInfo, CancellationToken cancel)
    {
        if (dirPath.EndsWith(Path.DirectorySeparatorChar)) {
            dirPath = dirPath.TrimEnd(Path.DirectorySeparatorChar);
        }

        if (Command != null) {
            traversalCompletionSource_ = new TaskCompletionSource<bool>();
            rootDirInfo_ = dirInfo;
            numFilesCompleted_ = 0;
            traversalCompleted_ = false;
        }

        TraverseDirectory(dirPath, fileSpec, dirInfo, cancel, dirPath.Length + 1, 0);
        traversalCompleted_ = true;
        dirInfo.Name = Path.GetFullPath(dirPath);

        if (Command != null) {
            if (numFilesCompleted_ < rootDirInfo_!.NumFiles) {
                traversalCompletionSource_!.Task.Wait();
            }
            traversalCompletionSource_ = null;
            rootDirInfo_ = null;
            numFilesCompleted_ = 0;
            traversalCompleted_ = false;
        }
    }

    void TraverseDirectory(string dirPath, string fileSpec, DirectoryInfo dirInfo, CancellationToken cancel,
                           int relPathIndex, int nameIndex)
    {
        if (cancel.IsCancellationRequested) {
            return;
        }

        Debug.WriteLine($"DIRSCAN: TraverseDirectory({dirPath})");

        string[] filePaths;
        try {
            filePaths = Directory.GetFiles(dirPath, fileSpec);
        }
        catch (ArgumentException) {
            throw new Exception($"error: invalid FILES argument: {fileSpec}");
        }
        catch (UnauthorizedAccessException) {
            CConsole.WriteLineError(CConsole.Yellow, $"warning: access denied: {dirPath}");
            filePaths = Array.Empty<string>();
        }

        var fileCount = filePaths.Length;

        dirInfo.Name = dirPath.Substring(nameIndex);
        int nextNameIndex = dirPath.Length + 1;

        dirInfo.NumFiles = fileCount;
        dirInfo.Files = new FileInfo [fileCount];
        Parallel.For(0, fileCount, i => {
            var fileInfo = new FileInfo() { Name = filePaths[i].Substring(nextNameIndex) };
            dirInfo.Files[i] = fileInfo;

            if (Command != null) {
                fileInfo.Path    = filePaths[i];
                fileInfo.PathRel = filePaths[i].Substring(relPathIndex);

                // INTENDED: Because this call is not awaited, execution of the current method
                // continues before the call is completed.
                #pragma warning disable 4014
                ExecuteAsync(fileInfo, cancel);
                #pragma warning restore 4014
            }
        });

        if (Recurse) {
            string[] subdirPaths;
            if (Recurse) {
                try {
                    subdirPaths = Directory.GetDirectories(dirPath);
                }
                catch (UnauthorizedAccessException) {
                    CConsole.WriteLineError(CConsole.Yellow, $"warning: access denied: {dirPath}");
                    subdirPaths = Array.Empty<string>();
                }
            } else {
                subdirPaths = Array.Empty<string>();
            }

            var dirCount = subdirPaths.Length;
            dirInfo.Dirs = new DirectoryInfo [dirCount];
            Parallel.For(0, dirCount, i => {
                dirInfo.Dirs[i] = new DirectoryInfo();
                TraverseDirectory(subdirPaths[i], fileSpec, dirInfo.Dirs[i], cancel,
                                  relPathIndex, nextNameIndex);

                Interlocked.Add(ref dirInfo.NumFiles, dirInfo.Dirs[i].NumFiles);
            });
        }

        Debug.WriteLine($"DIRSCAN: {dirPath} subdirs={dirInfo.Dirs.Length} files={dirInfo.Files.Length}");
    }

    async Task ExecuteAsync(FileInfo fileInfo, CancellationToken cancel)
    {
        Debug.WriteLine($"EXEC: ExecuteAsync({fileInfo.Path})");

        // Caller ensures that Command is not null before calling, but the analysis isn't detecting
        // that.
        #pragma warning disable 8602
        var Exes = Command.Exes;
        #pragma warning restore 8602

        foreach (var exe in Exes) {

            // Create streams for the stdout/stderr
            var stdOutHandle = ChooseRedirectHandle(exe, new Command.RedirectHandle[] {
                Command.RedirectHandle.StdOut,
                Command.RedirectHandle.Verbose,
                Command.RedirectHandle.Debug,
                Command.RedirectHandle.Info,
                Command.RedirectHandle.H7,
                Command.RedirectHandle.H8,
                Command.RedirectHandle.H9
            });

            var stdErrHandle = ChooseRedirectHandle(exe, new Command.RedirectHandle[] {
                Command.RedirectHandle.StdErr,
                Command.RedirectHandle.Warning,
            });

            TextWriter? stdOutWriter;
            TextWriter? stdErrWriter;
            try {
                stdOutWriter = CreateRedirectStream(exe, fileInfo, stdOutHandle, CollectStdout);
                stdErrWriter = CreateRedirectStream(exe, fileInfo, stdErrHandle, CollectStderr);
            }
            catch (Exception e) {
                fileInfo.ExitCode = -1;
                fileInfo.Stderr = $"error: {e.Message}";
                break;
            }

            // Create the process
            Process proc = new Process();
            proc.StartInfo.FileName = ReplaceVariables(exe.Path, fileInfo);
            proc.StartInfo.Arguments = ReplaceVariables(exe.Args, fileInfo);
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.CreateNoWindow = true;
            proc.EnableRaisingEvents = true;

            if (WorkingDirectory.Length > 0) {
                proc.StartInfo.WorkingDirectory = ReplaceVariables(WorkingDirectory, fileInfo);
            }

            if (stdOutWriter != null) {
                proc.StartInfo.RedirectStandardOutput = true;
                proc.OutputDataReceived += (sender, args) => stdOutWriter.WriteLine(args.Data);
            }

            if (stdErrWriter != null) {
                proc.StartInfo.RedirectStandardError = true;
                proc.ErrorDataReceived += (sender, args) => stdErrWriter.WriteLine(args.Data);
            }

            // Start the process
            Debug.WriteLine($"EXEC: START: {proc.StartInfo.FileName} {proc.StartInfo.Arguments}");

            try {
                proc.Start();
                if (stdOutWriter != null) proc.BeginOutputReadLine();
                if (stdErrWriter != null) proc.BeginErrorReadLine();
            }
            catch (Win32Exception e) {
                fileInfo.ExitCode = -1;
                fileInfo.Stderr = $"error: {e.Message}";
                break;
            }

            await proc.WaitForExitAsync(); // TODO: can pass a cancel token here

            Debug.WriteLine($"EXEC: DONE: {proc.StartInfo.FileName} {proc.StartInfo.Arguments}");

            fileInfo.Stdout = CloseRedirectStream(exe, stdOutHandle, stdOutWriter);
            fileInfo.Stderr = CloseRedirectStream(exe, stdErrHandle, stdErrWriter);
            fileInfo.ExitCode = proc.ExitCode;

            proc.Dispose();

            bool done = false;
            switch (exe.Continue) {
            case Command.Continue.None:
                fileInfo.Completed = true;
                done = true;
                break;
            case Command.Continue.Pipe:
                fileInfo.ExitCode = -1;
                fileInfo.Stderr = "error: command pipe (|) is not yet implemented."; // TODO
                done = true;
                break;
            case Command.Continue.IfPass:
                done = fileInfo.ExitCode != 0;
                break;
            case Command.Continue.IfFail:
                done = fileInfo.ExitCode == 0;
                break;
            }
            if (done) {
                break;
            }
        }

        OnFileCompleted?.Invoke(fileInfo);

        int numFilesCompleted = Interlocked.Increment(ref numFilesCompleted_);
        if (numFilesCompleted == rootDirInfo_!.NumFiles && traversalCompleted_) {
            traversalCompletionSource_!.SetResult(true);
        }

        Debug.WriteLine($"EXEC: LEAVE: ExecuteAsync({fileInfo.Path})");
    }

    Command.RedirectHandle ChooseRedirectHandle(Command.Exe exe, Command.RedirectHandle[] srcHandles)
    {
        foreach (var h in srcHandles) {
            if (exe.Redirections[(int) h].ToHandle != Command.RedirectHandle.NotSet) {
                return h;
            }
        }
        return Command.RedirectHandle.NotSet;
    }

    TextWriter? CreateRedirectStream(Command.Exe exe, FileInfo file, Command.RedirectHandle srcHandle, bool collect)
    {
        // If the command doesn't specify a redirection, collect a string if tool wants to collect
        if (srcHandle == Command.RedirectHandle.NotSet) {
            return collect
                ? new StringWriter()
                : null;
        }

        // Setup the command's redirect
        var redirect = exe.Redirections[(int) srcHandle];
        if (redirect.ToHandle == Command.RedirectHandle.File) {
            if (redirect.ToPath == null) {
                return null;
            } else {
                var path = ReplaceVariables(redirect.ToPath, file);
                if (WorkingDirectory.Length > 0) {
                    path = Path.Combine(ReplaceVariables(WorkingDirectory, file), path);
                }
                return new StreamWriter(path, redirect.Append);
            }
        } else {
            return new StringWriter();
        }
    }

    public string CloseRedirectStream(Command.Exe exe, Command.RedirectHandle fromHandle, TextWriter? writer)
    {
        var sw = writer as StringWriter;
        if (sw != null) {
            return sw.ToString().Replace("\r\n", "\n").TrimEnd('\n');
        }

        if (fromHandle != Command.RedirectHandle.NotSet) {
            var redirection = exe.Redirections[(int) fromHandle];
            if (redirection.ToPath != null) {
                writer!.Close();
            }
        }

        return string.Empty;
    }
}
