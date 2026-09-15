# Windows installer

Build prerequisites: .NET 10 SDK and [Inno Setup 6](https://jrsoftware.org/isinfo.php).
From the repository root:

```powershell
winget install --id JRSoftware.InnoSetup -e -s winget
./installer/windows/build.ps1
```

The Inno Setup installation is needed only once on the build machine. The script
detects `ISCC.exe` on PATH and in standard machine/user installation directories.
For a custom location, pass `-IsccPath 'C:/path/to/ISCC.exe'`.

The build publishes a self-contained Windows x64 CLI and packages it as
`artifacts/installer/mailang-<version>-windows-x64-setup.exe`.
Users do not need Python, Java, or a separately installed .NET runtime.

Run the setup executable, then open a new terminal (restart the terminal app or
editor if it retains its previous environment):

```powershell
mailang --version
mailang validate ./workflow.mail
mailang run ./workflow.mail --input '{"text":"Hello"}'
```

Installation uses `%LOCALAPPDATA%\Programs\MAILang` and adds that directory to the
current user's PATH without administrator privileges. Rerunning setup upgrades
the same installation. Uninstall through Windows Installed apps; the uninstaller
removes the PATH entry only if this installer originally added it. SDK executable
names remain unchanged; the installed standalone command is `mailang`.

Before distributing a release, test on a disposable Windows user profile:

1. Install and confirm `mailang --version` works from a new terminal outside the repository.
2. Install again and confirm PATH contains only one MAILang entry.
3. Uninstall and confirm the executable and owned PATH entry are removed while other entries remain.
4. Repeat with a preexisting MAILang PATH entry and confirm uninstall preserves it.

The setup executable is unsigned unless signed separately by the distributor.
