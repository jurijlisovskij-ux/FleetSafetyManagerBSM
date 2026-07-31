Option Explicit

Const SERVICE_NAME = "FleetSafetyManager"
Const APP_URL = "http://127.0.0.1:8088/"

Dim shell, fso, logPath, i, http, serviceState
Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
logPath = shell.ExpandEnvironmentStrings("%LOCALAPPDATA%") & "\FleetSafetyManagerLauncher.log"

Sub LogLine(message)
  On Error Resume Next
  Dim stream
  Set stream = fso.OpenTextFile(logPath, 8, True)
  stream.WriteLine Now & "  " & message
  stream.Close
  On Error GoTo 0
End Sub

Function IsServiceRunning()
  On Error Resume Next
  Dim exec, text
  Set exec = shell.Exec("cmd /c sc query """ & SERVICE_NAME & """")
  text = exec.StdOut.ReadAll
  IsServiceRunning = (InStr(1, text, "RUNNING", vbTextCompare) > 0)
  On Error GoTo 0
End Function

Function SiteIsReady()
  On Error Resume Next
  Set http = CreateObject("WinHttp.WinHttpRequest.5.1")
  http.SetTimeouts 2000, 2000, 2000, 2000
  http.Open "GET", APP_URL, False
  http.Send
  SiteIsReady = (http.Status >= 200 And http.Status < 500)
  On Error GoTo 0
End Function

LogLine "Launcher started."

If Not IsServiceRunning() Then
  LogLine "Starting Windows service."
  shell.Run "cmd /c sc start """ & SERVICE_NAME & """", 0, True
End If

For i = 1 To 30
  If SiteIsReady() Then
    LogLine "Application is ready."
    shell.Run APP_URL, 1, False
    WScript.Quit 0
  End If
  WScript.Sleep 1000
Next

LogLine "Application did not become ready."
MsgBox "Fleet Safety Manager could not start." & vbCrLf & _
       "Restart Windows and try again." & vbCrLf & _
       "Diagnostic log: " & logPath, _
       vbExclamation, "Fleet Safety Manager"
WScript.Quit 1
