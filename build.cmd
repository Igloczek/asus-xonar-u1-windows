@echo off
rem Builds XonarU1.exe with the C# compiler that ships with Windows.
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
"%FW%\csc.exe" /nologo /optimize+ /target:winexe /platform:x64 /win32manifest:app.manifest ^
  /r:"%FW%\WPF\PresentationFramework.dll" /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\WindowsBase.dll" /r:"%FW%\System.Xaml.dll" ^
  /resource:Flyout.xaml,Flyout.xaml /out:XonarU1.exe XonarU1.cs Flyout.cs
