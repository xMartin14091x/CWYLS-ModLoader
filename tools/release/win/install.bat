@echo off
title CWY:LS Mod Loader installer
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0files\install.ps1" %*
rem Double-clicked (no arguments): keep the window open so the summary can be read.
if "%~1"=="" pause
