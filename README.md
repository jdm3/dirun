# dirun

*dirun.exe* is a command line tool for Windows that helps you quickly run a command over a set of files.

```
usage: dirun.exe DIR FILES [options] -- COMMAND
options:
    DIR             The directory to search for files in (default=.)
    FILES           Which files to select as targets (default=*.*)
    COMMAND         The command to run on each target file found (default=file list)
    --working=DIR   The directory to run the command from (default=.)
    --pass=NUM      The exit code that command returns when it completes successfully (default=0)
    --norecurse     Don't recurse into subdirectories
    --repfile       Report status using the target file name, rather than the command
    --verbose       Print extra information during execution
```

If the *Command* requires redirection operators (>, >>, or &) then they need to be escaped using the '^' character. e.g.:

```bat
:: foo.exe > res.out
> dirun.exe -- foo.exe ^> res.out
```

If the *Command* requires double quotes, then they need to be escaped using two double quotes '""'. e.g.:

```bat
:: foo.exe --arg="this has spaces"
> dirun.exe -- foo.exe --arg="""this has spaces"""
```

To customize the execution for each target file, `COMMAND` and `--working` can be specified using the following variables:

| Variable                | Will be replaced with... |
| ----------------------- | ------------------------ |
| **%DIRUN\_FNAME%**      | The file name of the target file (without the extension). |
| **%DIRUN\_FEXT%**       | The extension of the target file (without the initial period). |
| **%DIRUN\_FPATH%**      | The full path to the target file. |
| **%DIRUN\_DPATH%**      | The full path to the directory containing the target file (without a trailing directory separator). |
| **%DIRUN\_?PATH\_REL%** | Any of the above variables ending in *PATH* also have a version that ends in *PATH\_REL* which is the path relative to the root search *Directory*. |
