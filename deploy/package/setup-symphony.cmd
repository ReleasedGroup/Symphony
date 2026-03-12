@echo off
setlocal EnableExtensions
set "SCRIPT_DIR=%~dp0"
"%SCRIPT_DIR%Symphony.exe" install %*
exit /b %ERRORLEVEL%
