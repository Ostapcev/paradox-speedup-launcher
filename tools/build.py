"""Compile the launcher with the C# compiler that ships with .NET Framework 4.x.

Usage: python tools/build.py   ->  release/Uskorenie_Paradox.exe
"""
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CSC = r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
WPF = os.path.join(os.path.dirname(CSC), 'WPF') + os.sep
OUT = os.path.join(HERE, 'release', 'Uskorenie_Paradox.exe')


def main() -> None:
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    cmd = [CSC, '/nologo', '/target:winexe', '/optimize+', '/codepage:65001', '/out:' + OUT,
           '/r:System.Web.Extensions.dll', '/r:System.IO.Compression.dll',
           '/r:System.Windows.Forms.dll', '/r:System.Xaml.dll',
           '/r:' + WPF + 'PresentationFramework.dll', '/r:' + WPF + 'PresentationCore.dll', '/r:' + WPF + 'WindowsBase.dll',
           '/resource:' + os.path.join(HERE, 'src', 'MainWindow.xaml') + ',MainWindow.xaml',
           os.path.join(HERE, 'src', 'Launcher.cs')]
    manifest = os.path.join(HERE, 'src', 'app.manifest')
    if os.path.isfile(manifest):
        cmd.insert(3, '/win32manifest:' + manifest)
    r = subprocess.run(cmd, capture_output=True, text=True, encoding='cp866', errors='replace')
    sys.stdout.write(r.stdout + r.stderr)
    if r.returncode:
        raise SystemExit(r.returncode)
    print('built', OUT, os.path.getsize(OUT))


if __name__ == '__main__':
    main()
