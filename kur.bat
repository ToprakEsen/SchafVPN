@echo off
setlocal
cd /d "%~dp0"
title SchafVPN - Kurulum

echo.
echo   ==================================================
echo     SchafVPN - kendi VLESS profilinle uygulama olustur
echo   ==================================================

rem --- .NET 10 SDK kontrolu ---
where dotnet >nul 2>nul || goto :nodotnet
dotnet --list-sdks 2>nul | findstr /r /c:"^1[0-9]\." >nul || goto :nodotnet

rem --- VLESS linkini al -> profile.txt ---
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\set-profile.ps1"
if errorlevel 1 goto :fail

rem --- Cekirdekleri indir (ilk sefer) ve derle ---
echo.
echo   Derleniyor... ilk seferde cekirdekler indirilecegi icin birkac dakika surebilir.
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
if errorlevel 1 goto :fail

echo.
echo   Hazir: %~dp0dist\SchafVPN.exe
echo   Bu exe icinde senin VLESS linkin var, baskasiyla paylasma.
echo.
explorer "%~dp0dist"
pause
exit /b 0

:nodotnet
echo.
echo   .NET 10 SDK bulunamadi. Indirme sayfasi aciliyor:
echo   https://dotnet.microsoft.com/download/dotnet/10.0
echo   (".NET SDK x64" surumunu kur, sonra kur.bat dosyasini tekrar calistir.)
start "" "https://dotnet.microsoft.com/download/dotnet/10.0"
echo.
pause
exit /b 1

:fail
echo.
echo   Bir hata olustu, yukaridaki mesaji kontrol et.
echo.
pause
exit /b 1
