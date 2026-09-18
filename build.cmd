@echo off
rem Build the WASAPI loopback capture helper with the csc.exe that ships with
rem .NET Framework 4.x (present on every Windows 10/11 machine, no downloads).
if not exist bin mkdir bin
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize+ /target:exe /out:bin\AudioRailCapture.exe src\AudioRailCapture.cs
if errorlevel 1 exit /b 1
echo Built bin\AudioRailCapture.exe
