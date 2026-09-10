@echo off
rem Build mmd2pdf.exe with the csc.exe bundled in Windows (.NET Framework 4.x). No SDK needed.
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist mermaid.min.js (
  echo Downloading mermaid.min.js ...
  curl.exe -sSfL -o mermaid.min.js https://cdn.jsdelivr.net/npm/mermaid@11.4.1/dist/mermaid.min.js || exit /b 1
)
"%CSC%" /nologo /codepage:65001 /optimize+ /target:exe /platform:anycpu /out:mmd2pdf.exe /resource:mermaid.min.js,mermaid.min.js mmd2pdf.cs || exit /b 1
echo Build OK: %~dp0mmd2pdf.exe
