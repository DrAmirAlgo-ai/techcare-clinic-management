@echo off
title PubMed download progress
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0pubmed-progress.ps1"
pause
