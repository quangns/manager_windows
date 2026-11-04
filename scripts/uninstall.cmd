@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0lib\uninstall.ps1" %*
