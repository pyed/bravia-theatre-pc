' Launch BRAVIA Theatre PC silently in background without keeping a CMD window open
Set WshShell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
strPath = fso.GetParentFolderName(WScript.ScriptFullName)

' Run pythonw (or python3 fallback) hidden (window style 0, false = non-blocking)
WshShell.CurrentDirectory = strPath
WshShell.Run "pythonw.exe src/app.py", 0, False
