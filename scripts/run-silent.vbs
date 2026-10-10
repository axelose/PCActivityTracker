Option Explicit
If WScript.Arguments.Count < 2 Then WScript.Quit 2

Dim executable, localRuntime, shell
executable = WScript.Arguments(0)
localRuntime = WScript.Arguments(1)
Set shell = CreateObject("WScript.Shell")

If CreateObject("Scripting.FileSystemObject").FolderExists(localRuntime) Then
    shell.Environment("PROCESS")("DOTNET_ROOT") = localRuntime
End If

shell.Run Chr(34) & executable & Chr(34) & " --background", 0, False
