@echo off
rem Builds the English and Japanese exes with the C# compiler that ships with Windows (.NET Framework 4).
setlocal
chcp 65001 >nul
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo csc.exe not found at %CSC%
    exit /b 1
)
cd /d "%~dp0"
"%CSC%" /nologo /target:winexe /platform:x64 /optimize "/out:iX-GX Extra Stages.exe" src\IxGxExtraStages.cs || exit /b 1
"%CSC%" /nologo /target:winexe /platform:x64 /optimize /define:JAPANESE "/out:iX-GX エクストラステージ.exe" src\IxGxExtraStages.cs || exit /b 1
echo Built "iX-GX Extra Stages.exe" and "iX-GX エクストラステージ.exe".
